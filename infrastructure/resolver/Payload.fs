// role: reusable module/helper
// Digests are checked before decompression or parsing so untrusted archive bytes stay inert.
module ResolverPayload

open System
open System.Collections.Generic
open SharedFailure

type Member =
    { Path: string
      Sha256: string
      Bytes: byte[] }

type Verified =
    { Key: string
      Manifest: DistributionContract.DistributionManifest
      ManifestBytes: byte[]
      Runtime: DistributionContract.RuntimeRequirement
      Members: Member list }

let private describe (names: string list) =
    let shown = names |> List.truncate 5 |> String.concat ", "

    if names.Length > 5 then $"{shown} (+{names.Length - 5} more)" else shown

let private entriesNamed (name: string) (archive: DistributionArchive.Archive) =
    archive.Entries |> List.filter (fun entry -> entry.Name = name)

let private verifiedManifest
    (pin: DistributionPin.Pin)
    (archive: DistributionArchive.Archive)
    : Result<DistributionContract.DistributionManifest * byte[], ReleaseError> =
    match entriesNamed DistributionContract.manifestEntryName archive with
    | [] ->
        Error(MalformedArtifact(DistributionContract.manifestEntryName, "the archive does not contain the distribution manifest"))
    | [ entry ] ->
        match DistributionArchive.decompress entry with
        | Error error -> Error error
        | Ok manifestBytes ->
            let actual = ResolverDigest.sha256 manifestBytes

            if actual <> pin.ManifestSha256 then
                Error(IdentityMismatch("manifestSha256", pin.ManifestSha256, actual))
            else
                match DistributionContract.parseBytes manifestBytes with
                | Error error -> Error error
                | Ok manifest -> Ok(manifest, manifestBytes)
    | _ ->
        Error(
            MalformedArtifact(
                DistributionContract.manifestEntryName,
                "the archive contains more than one distribution manifest"
            )
        )

// Exact membership rejects extra or missing entries before any member payload is trusted.
let private verifiedMembership
    (manifest: DistributionContract.DistributionManifest)
    (archive: DistributionArchive.Archive)
    : Result<unit, ReleaseError> =
    let actual = archive.Entries |> List.map (fun entry -> entry.Name) |> DistributionContract.sortOrdinal
    let expected = DistributionContract.archiveMemberNames manifest

    if actual = expected then
        ResolverPath.portableSet expected
    else
        let actualNames = HashSet<string>(actual, StringComparer.Ordinal)
        let expectedNames = HashSet<string>(expected, StringComparer.Ordinal)
        let missing = expected |> List.filter (fun name -> not (actualNames.Contains name))
        let unexpected = actual |> List.filter (fun name -> not (expectedNames.Contains name))

        Error(
            MalformedArtifact(
                DistributionContract.manifestEntryName,
                $"the archive members are not exactly the declared members; missing: {describe missing}; unexpected: {describe unexpected}"
            )
        )

let private verifiedMembers
    (manifest: DistributionContract.DistributionManifest)
    (archive: DistributionArchive.Archive)
    : Result<Member list, ReleaseError> =
    let members = ResizeArray<Member>()
    let mutable failure: ReleaseError option = None

    for file in manifest.Files do
        if failure.IsNone then
            match entriesNamed file.Path archive with
            | [ entry ] ->
                match DistributionArchive.decompress entry with
                | Error error -> failure <- Some error
                | Ok bytes ->
                    let actual = ResolverDigest.sha256 bytes

                    if actual <> file.Sha256 then
                        failure <- Some(IdentityMismatch(file.Path, file.Sha256, actual))
                    else
                        members.Add(
                            { Path = file.Path
                              Sha256 = file.Sha256
                              Bytes = bytes }
                        )
            | _ ->
                failure <-
                    Some(MalformedArtifact(file.Path, "the archive does not contain exactly this declared member"))

    match failure with
    | Some error -> Error error
    | None -> Ok(List.ofSeq members)

let private verifiedPayload (pin: DistributionPin.Pin) (archive: DistributionArchive.Archive) =
    match verifiedManifest pin archive with
    | Error error -> Error error
    | Ok(manifest, manifestBytes) ->
        match ResolverIdentity.compare pin manifest with
        | Error error -> Error error
        | Ok () ->
            match verifiedMembership manifest archive with
            | Error error -> Error error
            | Ok () ->
                match verifiedMembers manifest archive with
                | Error error -> Error error
                | Ok members -> Ok(manifest, manifestBytes, members)

// The retained archive makes this same verification reusable for a cached install.
let verify (pin: DistributionPin.Pin) (archiveBytes: byte[]) : Result<Verified, ReleaseError> =
    let actual = ResolverDigest.sha256 archiveBytes

    if actual <> pin.ArchiveSha256 then
        Error(IdentityMismatch("archiveSha256", pin.ArchiveSha256, actual))
    else
        match DistributionArchive.parse pin.ArchiveName archiveBytes with
        | Error error -> Error error
        | Ok archive ->
            match verifiedPayload pin archive with
            | Error error -> Error error
            | Ok(manifest, manifestBytes, members) ->
                match members |> List.tryFind (fun memberFile -> memberFile.Path = manifest.Runtime.Config) with
                | None ->
                    Error(
                        MalformedArtifact(
                            manifest.Runtime.Config,
                            "the declared runtime configuration is not a verified member"
                        )
                    )
                | Some config ->
                    match ResolverRuntime.requirement manifest config.Bytes with
                    | Error error -> Error error
                    | Ok runtime ->
                        Ok
                            { Key = ResolverIdentity.pinKey pin
                              Manifest = manifest
                              ManifestBytes = manifestBytes
                              Runtime = runtime
                              Members = members }

// The first differing path or digest identifies why the installed inventory is not verified.
let difference (verified: Verified) (present: (string * string) list) : string option =
    let onDisk = Dictionary<string, string>(StringComparer.Ordinal)

    for path, digest in present do
        onDisk[path] <- digest

    let mismatch (memberFile: Member) =
        match onDisk.TryGetValue memberFile.Path with
        | false, _ -> Some $"the member '{memberFile.Path}' is missing"
        | true, digest ->
            if digest = memberFile.Sha256 then
                None
            else
                Some $"the member '{memberFile.Path}' does not match the trusted pin"

    let approved = HashSet<string>(verified.Members |> List.map (fun memberFile -> memberFile.Path), StringComparer.Ordinal)

    match verified.Members |> List.tryPick mismatch with
    | Some detail -> Some detail
    | None ->
        match present |> List.tryFind (fun (path, _) -> not (approved.Contains path)) with
        | Some(path, _) -> Some $"the content '{path}' is not part of the trusted distribution"
        | None -> None

// Only verified member names prove resolver ownership; unknown content may belong to someone else.
let sameNames (verified: Verified) (present: (string * string) list) : bool =
    let names (paths: string list) = paths |> List.map (fun path -> path.ToLowerInvariant()) |> Set.ofList

    names (verified.Members |> List.map (fun memberFile -> memberFile.Path)) = names (present |> List.map fst)

// Byte-identical verified entries prove a partial staging tree is reproducible resolver output.
let subset (verified: Verified) (present: (string * string) list) : bool =
    let known = Dictionary<string, string>(StringComparer.Ordinal)

    for memberFile in verified.Members do
        known[memberFile.Path] <- memberFile.Sha256

    present
    |> List.forall (fun (path, digest) ->
        match known.TryGetValue path with
        | true, sha256 -> sha256 = digest
        | false, _ -> false)
