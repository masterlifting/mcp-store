// Verification is tested through the resolver's own result errors, never through message or snapshot trivia: archive digest, manifest digest, identity, membership, and member hashes.
// The archive writer regressions pin the repair boundary: only the creator byte is repaired, and the strict reader keeps refusing every other shape.
module Resolver.Tests.Payload

open System
open System.IO.Compression
open Expecto
open SharedFailure
open DistributionContract
open Resolver.Tests.Fixture

let private expectError (result: Result<'a, ReleaseError>) : ReleaseError =
    match result with
    | Ok _ -> failtest "the resolver accepted content that must fail closed"
    | Error error -> error

// The creator metadata is located through the central directory the archive itself declares, so no payload byte is scanned for a signature.
let private centralEntryOffsets (bytes: byte[]) : int list =
    let u16 (offset: int) = int bytes.[offset] ||| (int bytes.[offset + 1] <<< 8)

    let u32 (offset: int) =
        int bytes.[offset]
        ||| (int bytes.[offset + 1] <<< 8)
        ||| (int bytes.[offset + 2] <<< 16)
        ||| (int bytes.[offset + 3] <<< 24)

    let mutable eocd = -1
    let mutable probe = bytes.Length - 22
    let floor = max 0 (bytes.Length - 22 - 65535)

    while probe >= floor && eocd < 0 do
        if bytes.[probe] = 0x50uy
           && bytes.[probe + 1] = 0x4Buy
           && bytes.[probe + 2] = 0x05uy
           && bytes.[probe + 3] = 0x06uy then
            eocd <- probe

        probe <- probe - 1

    if eocd < 0 then
        failtest "the fixture archive carries no end-of-central-directory record"

    let mutable cursor = u32 (eocd + 16)

    [ for _ in 1 .. u16 (eocd + 10) do
        let offset = cursor
        cursor <- cursor + 46 + u16 (cursor + 28) + u16 (cursor + 30) + u16 (cursor + 32)
        yield offset ]

// The writer records its own platform in the creator host byte, so a case emulates another writer platform by rewriting that byte alone.
let private withVersionMadeBy (bytes: byte[]) (version: byte) (host: byte) : byte[] =
    let copy = Array.copy bytes

    for offset in centralEntryOffsets bytes do
        copy.[offset + 4] <- version
        copy.[offset + 5] <- host

    copy

let private withVersionNeeded (bytes: byte[]) (low: byte) (high: byte) : byte[] =
    let copy = Array.copy bytes

    for offset in centralEntryOffsets bytes do
        copy.[offset + 6] <- low
        copy.[offset + 7] <- high

    copy

let private propertyPin = fst (fixture dotnetSpec)

let private writerTests =
    testList
        "archive writer"
        [ testCase "the canonical archive passes through the writer canonicalizer unchanged"
          <| fun () ->
              let _, canonical = fixture dotnetSpec
              let input = Array.copy canonical

              match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName canonical with
              | Error error -> failtestf "the canonical fixture archive was refused: %s" (ReleaseError.message error)
              | Ok bytes ->
                  Expect.equal (sha256 bytes) (sha256 canonical) "the canonical archive is returned byte for byte"
                  Expect.equal (sha256 canonical) (sha256 input) "the canonicalizer leaves its input buffer unmutated"

          testCase "a Unix creator archive canonicalizes to the canonical bytes and verifies against the same pin"
          <| fun () ->
              let pinValue, canonical = fixture dotnetSpec
              let unix = withVersionMadeBy canonical 20uy 3uy
              let input = Array.copy unix

              Expect.notEqual (sha256 unix) (sha256 canonical) "the Unix creator archive differs from the canonical archive"

              match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName unix with
              | Error error -> failtestf "the Unix creator archive was refused: %s" (ReleaseError.message error)
              | Ok repaired ->
                  Expect.equal (sha256 repaired) (sha256 canonical) "the canonicalized archive is the canonical archive byte for byte"
                  Expect.equal (sha256 unix) (sha256 input) "the canonicalizer leaves its input buffer unmutated"
                  Expect.equal (sha256 repaired) pinValue.ArchiveSha256 "the canonicalized archive keeps the pinned archive digest"

                  match ResolverPayload.verify pinValue repaired with
                  | Error error -> failtestf "the canonicalized archive did not verify: %s" (ReleaseError.message error)
                  | Ok verified ->
                      Expect.equal verified.Members.Length 4 "the canonicalized archive carries the same members"
                      Expect.equal (ResolverIdentity.pinKey pinValue) verified.Key "the canonicalized archive keeps the same installation identity"

          testCase "the strict reader still refuses raw Unix creator metadata"
          <| fun () ->
              let members = bodies dotnetSpec
              let content = manifestBytes dotnetSpec dotnetSpec.Revision members
              let _, canonical = fixture dotnetSpec
              let unix = withVersionMadeBy canonical 20uy 3uy

              match DistributionArchive.parse dotnetSpec.ArchiveName unix with
              | Ok _ -> failtest "the strict reader accepted raw Unix creator metadata"
              | Error (MalformedArtifact (_, detail)) ->
                  Expect.stringContains detail "version metadata" "the strict reader names the creator metadata it refuses"
              | Error error -> failtestf "expected a malformed-archive failure, got %s" (ReleaseError.message error)

              match expectError (ResolverPayload.verify (pin dotnetSpec content unix) unix) with
              | MalformedArtifact (_, detail) ->
                  Expect.stringContains detail "version metadata" "the resolver refuses raw Unix creator metadata"
              | error -> failtestf "expected a malformed-archive failure, got %s" (ReleaseError.message error)

          testCase "an unsupported creator host or version is refused rather than repaired"
          <| fun () ->
              let _, canonical = fixture dotnetSpec

              let unsupported =
                  [ (20uy, 2uy, "an unsupported creator host")
                    (20uy, 19uy, "an unknown creator host")
                    (21uy, 3uy, "an unsupported creator version") ]

              for version, host, label in unsupported do
                  let mutated = withVersionMadeBy canonical version host

                  match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName mutated with
                  | Ok _ -> failtestf "%s was repaired instead of refused" label
                  | Error _ -> ()

          testCase "an unsupported version-needed value is refused rather than repaired"
          <| fun () ->
              let _, canonical = fixture dotnetSpec
              let mutated = withVersionNeeded canonical 21uy 0uy

              match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName mutated with
              | Ok _ -> failtest "an unsupported version-needed value was repaired instead of refused"
              | Error _ -> ()

          testCase "a malformed or truncated archive is refused rather than repaired"
          <| fun () ->
              let _, canonical = fixture dotnetSpec
              let truncated = canonical[0 .. canonical.Length - 30]
              let firstCentral = (centralEntryOffsets canonical) |> List.head
              let malformed = Array.copy canonical
              malformed.[firstCentral] <- 0x51uy

              match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName truncated with
              | Ok _ -> failtest "a truncated archive was repaired instead of refused"
              | Error (MalformedArtifact (_, detail)) ->
                  Expect.stringContains detail "end-of-central-directory" "the truncated archive is refused for its missing record"
              | Error error -> failtestf "expected a malformed-archive failure, got %s" (ReleaseError.message error)

              match DistributionArchive.canonicalizeWriterBytes dotnetSpec.ArchiveName malformed with
              | Ok _ -> failtest "a malformed archive was repaired instead of refused"
              | Error (MalformedArtifact (_, detail)) ->
                  Expect.stringContains detail "central directory header" "the malformed archive is refused for its broken header"
              | Error error -> failtestf "expected a malformed-archive failure, got %s" (ReleaseError.message error) ]

let tests =
    testList
        "payload"
        [ testCase "the canonical fixture verifies against its derived pin"
          <| fun () ->
              let pinValue, archive = fixture dotnetSpec

              match ResolverPayload.verify pinValue archive with
              | Error error -> failtestf "the canonical fixture did not verify: %s" (ReleaseError.message error)
              | Ok verified ->
                  Expect.equal verified.Key (ResolverIdentity.pinKey pinValue) "the key is the pin hash"
                  Expect.equal verified.Manifest.Id "dotnet" "manifest identity"
                  Expect.equal verified.Members.Length 4 "every declared member is verified"
                  Expect.equal verified.Runtime.Framework "Microsoft.NETCore.App" "runtime is derived from the packaged config"

          testCase "a tampered archive byte fails the archive digest first"
          <| fun () ->
              let pinValue, archive = fixture dotnetSpec

              match expectError (ResolverPayload.verify pinValue (tamper archive)) with
              | IdentityMismatch (field, _, _) ->
                  Expect.equal field "archiveSha256" "the archive digest is checked before decompression"
              | error -> failtestf "expected an archive digest mismatch, got %s" (ReleaseError.message error)

          testCase "manifest bytes that differ from the pin digest fail closed"
          <| fun () ->
              let members = bodies dotnetSpec
              let original = manifestBytes dotnetSpec dotnetSpec.Revision members
              let altered = manifestBytes dotnetSpec "1111111111111111111111111111111111111111" members
              let archive = archiveBytes altered members
              let pinValue = { pin dotnetSpec altered archive with ManifestSha256 = sha256 original }

              match expectError (ResolverPayload.verify pinValue archive) with
              | IdentityMismatch (field, _, _) ->
                  Expect.equal field "manifestSha256" "the manifest digest is verified independently of the archive digest"
              | error -> failtestf "expected a manifest digest mismatch, got %s" (ReleaseError.message error)

          testCase "a manifest identity that disagrees with the pin fails closed"
          <| fun () ->
              let pinValue, archive = fixture dotnetSpec
              let wrong = { pinValue with Version = "9.9.9" }

              match expectError (ResolverPayload.verify wrong archive) with
              | MalformedArtifact (path, _) ->
                  Expect.equal path manifestEntryName "the identity failure names the manifest"
              | error -> failtestf "expected an identity failure, got %s" (ReleaseError.message error)

          testCase "a member whose payload disagrees with the manifest fails closed"
          <| fun () ->
              let members = bodies dotnetSpec
              let content = manifestBytes dotnetSpec dotnetSpec.Revision members

              let archive =
                  archiveBytes
                      content
                      (members
                       |> List.map (fun (path, payload) ->
                           if path = dotnetSpec.Entrypoint then path, utf8 "tampered-entrypoint" else path, payload))

              match expectError (ResolverPayload.verify (pin dotnetSpec content archive) archive) with
              | IdentityMismatch (field, _, _) ->
                  Expect.equal field dotnetSpec.Entrypoint "the mismatching member is named"
              | error -> failtestf "expected a member hash mismatch, got %s" (ReleaseError.message error)

          testCase "an unexpected archive entry fails membership"
          <| fun () ->
              let members = bodies dotnetSpec
              let content = manifestBytes dotnetSpec dotnetSpec.Revision members
              let archive = archiveBytes content (members @ [ "extra.txt", utf8 "extra" ])

              match expectError (ResolverPayload.verify (pin dotnetSpec content archive) archive) with
              | MalformedArtifact (path, detail) ->
                  Expect.equal path manifestEntryName "the membership failure names the manifest"
                  Expect.isTrue (detail.Contains("extra.txt", StringComparison.Ordinal)) "the unexpected entry is identified"
              | error -> failtestf "expected a membership failure, got %s" (ReleaseError.message error)

          testCase "a missing declared member fails membership"
          <| fun () ->
              let members = bodies dotnetSpec
              let content = manifestBytes dotnetSpec dotnetSpec.Revision members

              let archive =
                  archiveBytes content (members |> List.filter (fun (path, _) -> path <> noticeEntryName))

              match expectError (ResolverPayload.verify (pin dotnetSpec content archive) archive) with
              | MalformedArtifact (_, detail) ->
                  Expect.isTrue (detail.Contains("missing", StringComparison.Ordinal)) "the missing member is identified"
              | error -> failtestf "expected a membership failure, got %s" (ReleaseError.message error)

          testCase "a case-variant duplicate entry does not stand in for the declared member"
          <| fun () ->
              let members = bodies dotnetSpec
              let content = manifestBytes dotnetSpec dotnetSpec.Revision members
              let archive = archiveBytes content (members @ [ "NOTICE.TXT", utf8 "case variant" ])

              match expectError (ResolverPayload.verify (pin dotnetSpec content archive) archive) with
              | MalformedArtifact (_, detail) ->
                  Expect.isTrue (detail.Contains("NOTICE.TXT", StringComparison.Ordinal)) "the case-variant entry is rejected"
              | error -> failtestf "expected a membership failure, got %s" (ReleaseError.message error)

          testCase "a decompression-ratio-abusing archive is rejected by the resource bound"
          <| fun () ->
              // The canonicalizer refuses a ratio-abusing archive, so the case reads the raw writer output with the canonical creator byte.
              // Only the producer's emitted entry shape reaches the ratio bound; any other writer shape is refused by the stricter entry gate first, which would hide the bound.
              let archive =
                  rawArchiveBytesWith CompressionLevel.Optimal (utf8 "{}") [ "big.txt", Array.zeroCreate<byte> (8 * 1024 * 1024) ]

              match DistributionArchive.parse dotnetSpec.ArchiveName (withVersionMadeBy archive 20uy 0uy) with
              | Ok _ -> failtest "a ratio-abusing archive must not parse"
              | Error (MalformedArtifact (_, detail)) ->
                  Expect.isTrue
                      (detail.Contains("compression-ratio", StringComparison.Ordinal))
                      $"the compression-ratio bound rejects it; detail: {detail}"
              | Error error -> failtestf "expected a malformed-archive failure, got %s" (ReleaseError.message error)

          testProperty "distinct version strings yield distinct installation keys"
          <| fun (left: int, right: int) ->
              if left = right then
                  true
              else
                  ResolverIdentity.pinKey { propertyPin with Version = string left }
                  <> ResolverIdentity.pinKey { propertyPin with Version = string right }

          writerTests ]
