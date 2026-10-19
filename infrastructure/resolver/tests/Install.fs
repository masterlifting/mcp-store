// Install is transactional, offline-capable, cross-process safe, and immutable: these cases assert those invariants through the resolver result and the real published tree bytes.
module Resolver.Tests.Install

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open SharedFailure
open Resolver.Tests.Fixture
open Resolver.Tests.Probe

let private unwrap (work: Result<'a, ReleaseError>) : 'a =
    match work with
    | Ok value -> value
    | Error error -> failtestf "%s" (ReleaseError.message error)

let private anyPartial (directory: string) : bool =
    Directory.Exists directory
    && Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories).Length > 0

let tests =
    testList
        "install"
        [ testCaseAsync "an empty root installs the exact pinned revision"
          <| async {
              do!
                  scopedAsync
                      "install"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let downloads = ref 0
                              let! resolution = okAsync (ResolverInstall.resolve (serving archive downloads) base' pinValue)

                              Expect.equal downloads.Value 1 "an absent revision is downloaded once"
                              Expect.equal resolution.Key (ResolverIdentity.pinKey pinValue) "the resolution key is the pin hash"
                              Expect.equal resolution.Install storage.Install "the resolution names the pinned installation"
                              Expect.equal resolution.Entrypoint (Path.Combine(storage.Install, dotnetSpec.Entrypoint)) "the entrypoint is resolved inside the install"
                              Expect.isTrue (File.Exists resolution.Entrypoint) "the resolved entrypoint exists"
                              Expect.isTrue (File.Exists storage.Retained) "the verified archive is retained"
                              Expect.isTrue (File.Exists storage.Guard) "the coordination guard file is retained"
                              Expect.isTrue (File.Exists storage.Lease) "the coordination lease file is retained"

                              let verified = unwrap (ResolverPayload.verify pinValue (File.ReadAllBytes storage.Retained))
                              let! present = okAsync (ResolverDisk.inventory storage.Install)
                              Expect.equal (ResolverPayload.difference verified present) None "the installed tree equals the verified inventory"
                              resolution.Lease.Release()
                          })
          }

          testCaseAsync "a valid cache resolves without any download"
          <| async {
              do!
                  scopedAsync
                      "cache"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let first = ref 0
                              let! installed = okAsync (ResolverInstall.resolve (serving archive first) base' pinValue)
                              Expect.equal first.Value 1 "the first resolve downloads once"
                              let installedBytes = File.ReadAllBytes installed.Entrypoint
                              installed.Lease.Release()

                              let offlineDownloads = ref 0
                              let! cached = okAsync (ResolverInstall.resolve (offline offlineDownloads) base' pinValue)
                              Expect.equal offlineDownloads.Value 0 "a valid cache hit performs no download"
                              Expect.equal (File.ReadAllBytes cached.Entrypoint) installedBytes "the installed bytes are unchanged"
                              cached.Lease.Release()
                          })
          }

          testCaseAsync "a retained archive alone resolves offline when the install is absent"
          <| async {
              do!
                  scopedAsync
                      "retained"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! first = okAsync (ResolverInstall.resolve (serving archive (ref 0)) base' pinValue)
                              first.Lease.Release()
                              let! _ = okAsync (ResolverDisk.remove storage.Root storage.Install)

                              let offlineDownloads = ref 0
                              let! again = okAsync (ResolverInstall.resolve (offline offlineDownloads) base' pinValue)
                              Expect.equal offlineDownloads.Value 0 "the retained archive is reused instead of downloading"
                              Expect.isTrue (File.Exists again.Entrypoint) "the revision is reinstalled from the retained archive"
                              again.Lease.Release()
                          })
          }

          testCaseAsync "an install whose trusted names carry different bytes fails closed and is never replaced"
          <| async {
              do!
                  scopedAsync
                      "corrupt"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! first = okAsync (ResolverInstall.resolve (serving archive (ref 0)) base' pinValue)
                              first.Lease.Release()

                              let entry = Path.Combine(storage.Install, dotnetSpec.Entrypoint)
                              File.WriteAllBytes(entry, utf8 "operator-substituted-bytes")

                              let offlineDownloads = ref 0

                              match! ResolverInstall.resolve (offline offlineDownloads) base' pinValue with
                              | Ok _ -> return failtest "a corrupt install must never resolve"
                              | Error (CorruptInstall _) ->
                                  Expect.equal offlineDownloads.Value 0 "a corrupt install is not repaired by a download"
                                  Expect.equal (File.ReadAllBytes entry) (utf8 "operator-substituted-bytes") "the installed bytes are never replaced"
                                  return ()
                              | Error error -> return failtestf "expected a corrupt-install failure, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "a retained archive that no longer matches the pin is never trusted"
          <| async {
              do!
                  scopedAsync
                      "invalid-cache"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! first = okAsync (ResolverInstall.resolve (serving archive (ref 0)) base' pinValue)
                              first.Lease.Release()
                              File.WriteAllBytes(storage.Retained, tamper (File.ReadAllBytes storage.Retained))

                              let offlineDownloads = ref 0

                              match! ResolverInstall.resolve (offline offlineDownloads) base' pinValue with
                              | Ok _ -> return failtest "an unusable cache must never resolve"
                              | Error (TransportRejected _) ->
                                  Expect.equal offlineDownloads.Value 1 "an invalid cache falls back to a download, never to a launch"
                                  return ()
                              | Error error -> return failtestf "expected a transport refusal, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "content that fails verification leaves no installation or retained archive"
          <| async {
              do!
                  scopedAsync
                      "rejected"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let downloads = ref 0

                              match! ResolverInstall.resolve (serving (tamper archive) downloads) base' pinValue with
                              | Ok _ -> return failtest "unverified bytes must never install"
                              | Error (IdentityMismatch (field, _, _)) ->
                                  Expect.equal field "archiveSha256" "the fetched bytes are checked against the trusted pin"
                                  Expect.equal downloads.Value 1 "the rejected download is the only attempt"
                                  Expect.isFalse (Directory.Exists storage.Install) "no installation is published"
                                  Expect.isFalse (File.Exists storage.Retained) "unverified bytes are never retained"
                                  return ()
                              | Error error -> return failtestf "expected an identity mismatch, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "two immutable revisions coexist in one root"
          <| async {
              do!
                  scopedAsync
                      "coexist"
                      (fun base' ->
                          async {
                              let dotnetPin, dotnetArchive = fixture dotnetSpec
                              let workflowPin, workflowArchive = fixture workflowSpec
                              let! first = okAsync (ResolverInstall.resolve (serving dotnetArchive (ref 0)) base' dotnetPin)
                              let firstBytes = File.ReadAllBytes first.Entrypoint
                              first.Lease.Release()
                              let! second = okAsync (ResolverInstall.resolve (serving workflowArchive (ref 0)) base' workflowPin)
                              second.Lease.Release()

                              Expect.equal first.Manifest.Id "dotnet" "the first revision identity"
                              Expect.equal second.Manifest.Id "workflow" "the second revision identity"
                              Expect.notEqual first.Install second.Install "revisions install into distinct directories"
                              Expect.isTrue (Directory.Exists first.Install) "the first revision remains installed"
                              Expect.isTrue (Directory.Exists second.Install) "the second revision is installed"
                              Expect.equal (File.ReadAllBytes first.Entrypoint) firstBytes "installing a second revision does not touch the first"

                              let! againFirst = okAsync (ResolverInstall.resolve (offline (ref 0)) base' dotnetPin)
                              againFirst.Lease.Release()
                              let! againSecond = okAsync (ResolverInstall.resolve (offline (ref 0)) base' workflowPin)
                              againSecond.Lease.Release()
                              Expect.equal againFirst.Key first.Key "the first revision still resolves offline"
                              Expect.equal againSecond.Key second.Key "the second revision still resolves offline"
                          })
          }

          testCaseAsync "the resolution snapshot is versioned and carries no build provenance"
          <| async {
              do!
                  scopedAsync
                      "snapshot"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let! resolution = okAsync (ResolverInstall.resolve (serving archive (ref 0)) base' pinValue)
                              let json = (JsonNode.Parse(ResolverReport.render pinValue resolution)).AsObject()

                              Expect.equal (json["schemaVersion"].GetValue<int>()) 1 "the snapshot schema version"
                              Expect.equal (json["componentId"].GetValue<string>()) "dotnet" "the component identity"
                              Expect.equal (json["install"].GetValue<string>()) resolution.Install "the install path"
                              Expect.isTrue (Path.IsPathFullyQualified(json["entrypoint"].GetValue<string>())) "the entrypoint is absolute"
                              Expect.isFalse (json.ContainsKey "producerSdk") "consumer metadata carries no build provenance"
                              resolution.Lease.Release()
                          })
          }

          testCaseAsync "independent processes install the same pin with a single publication"
          <| async {
              do!
                  scopedAsync
                      "parallel"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let archivePath = writeArchive (Path.Combine(base', "served.zip")) archive
                              let pinPath = writePin base' pinValue
                              let storage = unwrap (ResolverStorage.layout base' pinValue)

                              let children = [ for _ in 1 .. 3 -> startProbe "install" [ base'; pinPath; archivePath; "400" ] ]
                              let! outcomes = children |> List.map awaitProbe |> Async.Parallel

                              for code, out, err in outcomes do
                                  Expect.equal code 0 $"each process succeeds (stderr: {err})"
                                  Expect.isTrue (out.Contains("ok ", StringComparison.Ordinal)) "each process reports a resolution"

                              let published = Directory.GetDirectories(Path.GetDirectoryName storage.Install)
                              Expect.equal published.Length 1 "only one revision directory is published"
                              Expect.equal (Path.GetFullPath published[0]) (Path.GetFullPath storage.Install) "the published directory is the pin key"
                              Expect.equal (Directory.GetFileSystemEntries storage.Staging).Length 0 "no staging tree is left behind"
                              Expect.isTrue (File.Exists storage.Retained) "the verified archive is retained once"
                              Expect.isTrue (File.Exists storage.Guard) "the coordination files are never unlinked"
                              Expect.isTrue (File.Exists storage.Lease) "the lease file is never unlinked"
                              Expect.isFalse (anyPartial base') "no partial write survives publication"
                          })
          }

          testCaseAsync "a killed guard holder does not block the next install"
          <| async {
              do!
                  scopedAsync
                      "crash"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              Directory.CreateDirectory(Path.GetDirectoryName storage.Retained) |> ignore
                              File.WriteAllBytes(storage.Retained, archive)
                              let pinPath = writePin base' pinValue
                              let marker = Path.Combine(base', "guard-held")
                              let child = startProbe "guard" [ base'; pinPath; marker ]

                              let! held =
                                  async {
                                      let mutable attempts = 0

                                      while not (File.Exists marker) && attempts < 200 do
                                          do! Async.Sleep 50
                                          attempts <- attempts + 1

                                      return File.Exists marker
                                  }

                              Expect.isTrue held "the child acquired the exclusive guard"

                              try
                                  child.Kill true
                              with :? InvalidOperationException ->
                                  ()

                              let! _ = awaitProbe child
                              let offlineDownloads = ref 0
                              let! resolution = okAsync (ResolverInstall.resolve (offline offlineDownloads) base' pinValue)
                              Expect.equal offlineDownloads.Value 0 "the retained archive is reused after the crash"
                              Expect.isTrue (File.Exists resolution.Entrypoint) "the next install publishes the revision"
                              Expect.isTrue (File.Exists storage.Guard) "the crashed holder's coordination file remains"
                              resolution.Lease.Release()
                          })
          }
        ]
