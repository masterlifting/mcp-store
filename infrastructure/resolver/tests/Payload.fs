// Verification is tested through the resolver's own result errors, never through message or
// snapshot trivia: archive digest, manifest digest, identity, membership, and member hashes.
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

let private propertyPin = fst (fixture dotnetSpec)

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
              // Only the producer's emitted entry shape reaches the ratio bound; any other writer
              // shape is refused by the stricter entry gate first, which would hide the bound.
              let archive =
                  archiveBytesWith CompressionLevel.Optimal (utf8 "{}") [ "big.txt", Array.zeroCreate<byte> (8 * 1024 * 1024) ]

              match DistributionArchive.parse dotnetSpec.ArchiveName archive with
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
                  <> ResolverIdentity.pinKey { propertyPin with Version = string right } ]
