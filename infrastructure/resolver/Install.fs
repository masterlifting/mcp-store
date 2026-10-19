// role: reusable module/helper
// Installation orchestration: offline reuse of a re-verified cache, download only for absent
// or invalid content, same-pin cross-process serialization, and atomic publication of fully
// verified staging only.
module ResolverInstall

open System
open System.IO
open SharedFailure

// The lease is held for the launched process lifetime; releasing it ends the usage claim.
type Resolution =
    { Key: string
      Root: string
      Install: string
      Entrypoint: string
      Manifest: DistributionContract.DistributionManifest
      Runtime: DistributionContract.RuntimeRequirement
      Lease: ResolverLock.Lock }

// One bounded recovery pass keeps stale-staging cleanup proportional to an install attempt.
let private recoveryLimit = 16

type private State =
    | Absent
    | Usable
    | Invalid of string
    | Blocked of string

let private prepare (storage: ResolverStorage.Storage) : Result<unit, ReleaseError> =
    match ResolverDisk.ensureRoot storage.Root with
    | Error error -> Error error
    | Ok () ->
        match ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Lease) with
        | Error error -> Error error
        | Ok () -> ResolverDisk.ensureDirectory storage.Root storage.Staging

// The retained archive is the only cached copy of the trusted bytes; an unreadable or
// mismatching copy is not a failure here, because the caller downloads in that case.
let private retained
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : ResolverPayload.Verified option =
    if not (ResolverDisk.exists storage.Retained) then
        None
    else
        match ResolverDisk.read storage.Root storage.Retained DistributionArchive.maxArchiveBytes with
        | Error _ -> None
        | Ok bytes ->
            match ResolverPayload.verify pin bytes with
            | Ok verified -> Some verified
            | Error _ -> None

// Offline usability is decided against the trusted pin alone: the retained archive is
// re-verified and the installation inventory must equal its verified member set exactly.
let private usable
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : ResolverPayload.Verified option =
    match retained pin storage with
    | None -> None
    | Some verified ->
        match ResolverDisk.inventory storage.Install with
        | Error _ -> None
        | Ok present -> if (ResolverPayload.difference verified present).IsNone then Some verified else None

let private stateOf (verified: ResolverPayload.Verified) (storage: ResolverStorage.Storage) : State =
    if not (ResolverDisk.exists storage.Install) then
        Absent
    elif ResolverDisk.linked storage.Install then
        Blocked "the installation path is a link or reparse point"
    else
        match ResolverDisk.inventory storage.Install with
        | Error error -> Blocked(ReleaseError.message error)
        | Ok present ->
            match ResolverPayload.difference verified present with
            | None -> Usable
            // An installed tree is never replaced under a possible consumer: a parent that died
            // without releasing its lease cannot be distinguished from one still running.
            | Some detail ->
                if ResolverPayload.sameNames verified present then
                    Invalid(
                        $"the installed tree carries the trusted member names but different bytes; no installation is replaced while it may be in use, so move '{storage.Install}' aside offline and resolve again, which reinstalls it from the retained archive '{storage.Retained}' ({detail})"
                    )
                else
                    Blocked detail

// A staging entry is deleted only when its name is resolver-generated and every file in it is a
// byte-identical copy of a verified member; a name alone is no authority over other content.
let private stale (verified: ResolverPayload.Verified) (entry: string) : bool =
    match Guid.TryParseExact(Path.GetFileName entry, "N") with
    | true, _ ->
        match ResolverDisk.inventory entry with
        | Ok present -> ResolverPayload.subset verified present
        | Error _ -> false
    | _ -> false

// Entries that cannot be proven resolver-owned are preserved for the operator, as is any entry the
// bounded pass does not reach.
let private recover
    (verified: ResolverPayload.Verified)
    (storage: ResolverStorage.Storage)
    : Result<unit, ReleaseError> =
    match ResolverDisk.entries storage.Staging with
    | Error error -> Error error
    | Ok entries ->
        for entry in entries |> List.filter (stale verified) |> List.truncate recoveryLimit do
            ResolverDisk.remove storage.Root entry |> ignore

        Ok()

let private extract
    (staging: string)
    (memberFile: ResolverPayload.Member)
    : Result<unit, ReleaseError> =
    match DistributionContract.resolveUnderRoot staging memberFile.Path with
    | Error error -> Error error
    | Ok target ->
        match ResolverDisk.ensureDirectory staging (Path.GetDirectoryName target) with
        | Error error -> Error error
        | Ok () -> ResolverDisk.write staging target memberFile.Bytes

// Staging is unique per attempt and lives beside the installations, so publication stays a
// same-filesystem rename and no two attempts can write the same path.
let private stage
    (verified: ResolverPayload.Verified)
    (storage: ResolverStorage.Storage)
    : Result<string, ReleaseError> =
    let staging = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")

    match ResolverDisk.ensureDirectory storage.Staging staging with
    | Error error -> Error error
    | Ok () ->
        let mutable failure: ReleaseError option = None

        for memberFile in verified.Members do
            if failure.IsNone then
                match extract staging memberFile with
                | Ok () -> ()
                | Error error -> failure <- Some error

        match failure with
        | Some error -> Error error
        | None ->
            match ResolverDisk.inventory staging with
            | Error error -> Error error
            | Ok present ->
                match ResolverPayload.difference verified present with
                | None -> Ok staging
                | Some detail -> Error(MalformedArtifact(staging, detail))

let private publish (staging: string) (storage: ResolverStorage.Storage) : Result<unit, ReleaseError> =
    match ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install) with
    | Error error -> Error error
    | Ok () -> ResolverDisk.move storage.Root staging storage.Install

// A download happens only for content the trusted pin says is absent or invalid, and the
// fetched bytes are verified before they are retained or staged.
let private retain
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified, ReleaseError>> =
    async {
        match retained pin storage with
        | Some verified -> return Ok verified
        | None ->
            match! ResolverTransport.download transport pin with
            | Error error -> return Error error
            | Ok bytes ->
                match ResolverPayload.verify pin bytes with
                | Error error -> return Error error
                | Ok verified ->
                    match ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Retained) with
                    | Error error -> return Error error
                    | Ok () ->
                        match ResolverDisk.write storage.Root storage.Retained bytes with
                        | Error error -> return Error error
                        | Ok () -> return Ok verified
    }

// The caller holds the exclusive guard and the exclusive usage lease for this whole rebuild,
// so the payload is obtained once and no state can change underneath the mutation.
let private populate
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified, ReleaseError>> =
    async {
        match! retain transport pin storage with
        | Error error -> return Error error
        | Ok verified ->
            match stateOf verified storage with
            | Usable -> return Ok verified
            | Invalid detail -> return Error(CorruptInstall(storage.Install, detail))
            | Blocked detail -> return Error(CorruptInstall(storage.Install, detail))
            | Absent ->
                match recover verified storage with
                | Error error -> return Error error
                | Ok () ->
                    match stage verified storage with
                    | Error error -> return Error error
                    | Ok staging ->
                        match publish staging storage with
                        | Error error -> return Error error
                        | Ok () ->
                            // Final-state validation of the published tree, so a cache
                            // hit is never reported for content that did not land.
                            match usable pin storage with
                            | Some published -> return Ok published
                            | None ->
                                return
                                    Error(
                                        CorruptInstall(
                                            storage.Install,
                                            "the published installation did not validate against the trusted pin"
                                        )
                                    )
    }

let private populateHeld
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified, ReleaseError>> =
    async {
        // The offline re-check comes before the exclusive lease: a competing process may have
        // published while this one waited for the guard, and a concurrent consumer holding the
        // shared lease must not be reported as an install conflict.
        match usable pin storage with
        | Some current -> return Ok current
        | None ->
            match! ResolverLock.exclusive storage.Lease with
            | Error error -> return Error error
            | Ok held ->
                // The exclusive lease covers the entire mutation, so no consumer can observe
                // or use an installation that is being published.
                use lease = held
                return! populate transport pin storage
    }

// The guard is still held while the shared lease is taken, so no window exists in which
// another process could replace the revision between publication and use.
let private installHeld
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified * ResolverLock.Lock, ReleaseError>> =
    async {
        match! ResolverLock.exclusive storage.Guard with
        | Error error -> return Error error
        | Ok guard ->
            try
                match! populateHeld transport pin storage with
                | Error error -> return Error error
                | Ok verified ->
                    match! ResolverLock.shared storage.Lease with
                    | Error error -> return Error error
                    | Ok lease -> return Ok(verified, lease)
            finally
                guard.Release()
    }

let private outcome
    (storage: ResolverStorage.Storage)
    (verified: ResolverPayload.Verified)
    (lease: ResolverLock.Lock)
    : Result<Resolution, ReleaseError> =
    match ResolverLaunch.entrypoint storage.Install verified.Manifest with
    | Error error ->
        lease.Release()
        Error error
    | Ok entrypoint ->
        Ok
            { Key = storage.Key
              Root = storage.Root
              Install = storage.Install
              Entrypoint = entrypoint
              Manifest = verified.Manifest
              Runtime = verified.Runtime
              Lease = lease }

// The shared lease is taken before any verification and travels with the result, so this consumer's
// claim exists before the verified state it observed can be republished by anything else.
let resolve
    (transport: ResolverTransport.Transport)
    (root: string)
    (pin: DistributionPin.Pin)
    : Async<Result<Resolution, ReleaseError>> =
    async {
        match ResolverStorage.layout root pin with
        | Error error -> return Error error
        | Ok storage ->
            match prepare storage with
            | Error error -> return Error error
            | Ok () ->
                match! ResolverLock.shared storage.Lease with
                | Error error -> return Error error
                | Ok lease ->
                    match usable pin storage with
                    | Some verified -> return outcome storage verified lease
                    | None ->
                        lease.Release()

                        match! installHeld transport pin storage with
                        | Error error -> return Error error
                        | Ok(verified, held) -> return outcome storage verified held
    }
