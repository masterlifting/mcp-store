// role: reusable module/helper
// Publication uses verified staging so an incomplete install is never visible.
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

// An invalid retained archive is replaced only by bytes verified against the trusted pin.
let private retained
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<ResolverPayload.Verified option> = async {
        if not (ResolverDisk.exists storage.Retained) then return None
        else
            match! ResolverDisk.read storage.Root storage.Retained DistributionArchive.maxArchiveBytes with
            | Error _ -> return None
            | Ok bytes ->
                match ResolverPayload.verify pin bytes with
                | Ok verified -> return Some verified
                | Error _ -> return None
    }

let private usable
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<ResolverPayload.Verified option> = async {
    match! retained pin storage with
    | None -> return None
    | Some verified ->
        match! ResolverDisk.inventory storage.Install with
        | Error _ -> return None
        | Ok present -> return if (ResolverPayload.difference verified present).IsNone then Some verified else None
    }

let private stateOf (verified: ResolverPayload.Verified) (storage: ResolverStorage.Storage) : Async<State> = async {
    if ResolverDisk.linked storage.Install then return Blocked "the installation path is a link or reparse point"
    elif not (ResolverDisk.exists storage.Install) then return Absent
    else
        match! ResolverDisk.inventory storage.Install with
        | Error error -> return Blocked(ReleaseError.message error)
        | Ok present ->
            match ResolverPayload.difference verified present with
            | None -> return Usable
            | Some detail when ResolverPayload.sameNames verified present ->
                return Invalid($"the installed tree carries the trusted member names but different bytes; no installation is replaced while it may be in use, so move '{storage.Install}' aside offline and resolve again, which reinstalls it from the retained archive '{storage.Retained}' ({detail})")
            | Some detail -> return Blocked detail
}

// A generated name alone does not prove staging content is resolver-owned.
let private stale (verified: ResolverPayload.Verified) (entry: string) : Async<bool> = async {
    match! ResolverDisk.inventory entry with
    | Ok present -> return ResolverPayload.subset verified present
    | Error _ -> return false
}

let private recover
    (verified: ResolverPayload.Verified)
    (storage: ResolverStorage.Storage)
    : Async<Result<unit, ReleaseError>> = async {
    if ResolverDisk.linked storage.Staging then
        return Error(FileFailure("entries", storage.Staging, "the directory is a link or reparse point"))
    elif not (Directory.Exists storage.Staging) then
        return Error(FileFailure("entries", storage.Staging, "the directory does not exist"))
    else
        try
            let candidates =
                Directory.EnumerateFileSystemEntries storage.Staging
                |> Seq.filter (fun entry ->
                    match Guid.TryParseExact(Path.GetFileName entry, "N") with
                    | true, _ -> true
                    | _ -> false)
                |> Seq.truncate recoveryLimit

            let mutable failure = None

            for entry in candidates do
                if failure.IsNone then
                    let! isStale = stale verified entry
                    if isStale then
                        match! ResolverDisk.remove storage.Root entry with
                        | Error error -> failure <- Some error
                        | Ok () -> ()

            return failure |> Option.fold (fun _ error -> Error error) (Ok ())
        with
        | :? IOException as error -> return Error(FileFailure("entries", storage.Staging, error.Message))
        | :? UnauthorizedAccessException as error -> return Error(FileFailure("entries", storage.Staging, error.Message))
}

let private extract
    (staging: string)
    (memberFile: ResolverPayload.Member)
    : Async<Result<unit, ReleaseError>> = async {
    match DistributionContract.resolveUnderRoot staging memberFile.Path with
    | Error error -> return Error error
    | Ok target ->
        match ResolverDisk.ensureDirectory staging (Path.GetDirectoryName target) with
        | Error error -> return Error error
        | Ok () -> return! ResolverDisk.write staging target memberFile.Bytes
}

// Same-volume staging makes publication an atomic rename.
let private stage
    (verified: ResolverPayload.Verified)
    (storage: ResolverStorage.Storage)
    : Async<Result<string, ReleaseError>> = async {
    let staging = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")

    match ResolverDisk.ensureDirectory storage.Staging staging with
    | Error error -> return Error error
    | Ok () ->
        let mutable failure: ReleaseError option = None

        for memberFile in verified.Members do
            if failure.IsNone then
                match! extract staging memberFile with
                | Ok () -> ()
                | Error error -> failure <- Some error

        match failure with
        | Some error -> return Error error
        | None ->
            match! ResolverDisk.inventory staging with
            | Error error -> return Error error
            | Ok present ->
                match ResolverPayload.difference verified present with
                | None -> return Ok staging
                | Some detail -> return Error(MalformedArtifact(staging, detail))
}

let private publish (staging: string) (storage: ResolverStorage.Storage) : Result<unit, ReleaseError> =
    match ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install) with
    | Error error -> Error error
    | Ok () -> ResolverDisk.move storage.Root staging storage.Install

// Verify fetched bytes before retaining or staging them.
let private retain
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified, ReleaseError>> =
    async {
        match! retained pin storage with
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
                        match! ResolverDisk.write storage.Root storage.Retained bytes with
                        | Error error -> return Error error
                        | Ok () -> return Ok verified
    }

let private populate
    (transport: ResolverTransport.Transport)
    (pin: DistributionPin.Pin)
    (storage: ResolverStorage.Storage)
    : Async<Result<ResolverPayload.Verified, ReleaseError>> =
    async {
        match! retain transport pin storage with
        | Error error -> return Error error
        | Ok verified ->
            match! stateOf verified storage with
            | Usable -> return Ok verified
            | Invalid detail -> return Error(CorruptInstall(storage.Install, detail))
            | Blocked detail -> return Error(CorruptInstall(storage.Install, detail))
            | Absent ->
                match! recover verified storage with
                | Error error -> return Error error
                | Ok () ->
                    match! stage verified storage with
                    | Error error -> return Error error
                    | Ok staging ->
                        match publish staging storage with
                        | Error error -> return Error error
                        | Ok () ->
                            // Validate publication before reporting a cache hit.
                            match! usable pin storage with
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
        // Recheck after the guard wait to avoid conflicting with a consumer of a newly published install.
        match! usable pin storage with
        | Some current -> return Ok current
        | None ->
            match! ResolverLock.exclusive storage.Lease with
            | Error error -> return Error error
            | Ok held ->
                // No consumer may observe a tree during publication.
                use lease = held
                return! populate transport pin storage
    }

// Holding the guard through lease acquisition closes the publish-to-use race.
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
                    match! usable pin storage with
                    | Some verified -> return outcome storage verified lease
                    | None ->
                        lease.Release()

                        match! installHeld transport pin storage with
                        | Error error -> return Error error
                        | Ok(verified, held) -> return outcome storage verified held
    }
