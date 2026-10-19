// Filesystem safety on the real Windows tree: link-free external chains, immutable-root
// containment, kind classification without opening, and bounded staging recovery.
module Resolver.Tests.Tree

open System
open System.IO
open Expecto
open SharedFailure
open Resolver.Tests.Fixture

let private unwrap (work: Result<'a, ReleaseError>) : 'a =
    match work with
    | Ok value -> value
    | Error error -> failtestf "%s" (ReleaseError.message error)

let private installOnce (base': string) (pinValue: DistributionPin.Pin) (archive: byte[]) : Async<unit> =
    async {
        let downloads = ref 0

        match! ResolverInstall.resolve (serving archive downloads) base' pinValue with
        | Error error -> return failtestf "the initial install failed: %s" (ReleaseError.message error)
        | Ok resolution ->
            resolution.Lease.Release()
            return ()
    }

let private resolveOffline (base': string) (pinValue: DistributionPin.Pin) : Async<unit> =
    async {
        let downloads = ref 0

        match! ResolverInstall.resolve (offline downloads) base' pinValue with
        | Error error -> return failtestf "the offline resolve failed: %s" (ReleaseError.message error)
        | Ok resolution ->
            resolution.Lease.Release()
            return ()
    }

let private tryOpenExclusively (path: string) : bool =
    try
        use probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        false
    with :? IOException ->
        true

let private tryOpenShared (path: string) : bool =
    try
        use probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
        true
    with :? IOException ->
        false

let tests =
    testList
        "tree"
        [ testCaseAsync "a junction on an external path chain is refused"
          <| async {
              let base' = root "chain"

              try
                  let target = Path.Combine(base', "target")
                  let link = Path.Combine(base', "link")
                  Directory.CreateDirectory target |> ignore
                  File.WriteAllText(Path.Combine(target, "data.txt"), "payload")
                  let! created = junctionAsync link target
                  Expect.isTrue created "the fixture creates a junction"

                  match ResolverDisk.chain (Path.Combine(link, "data.txt")) with
                  | Ok () -> failtest "a junction in the chain must be refused"
                  | Error (FileFailure (_, path, _)) ->
                      Expect.equal (Path.GetFullPath link) (Path.GetFullPath path) "the junction component is named"
                  | Error error -> failtestf "expected a link failure, got %s" (ReleaseError.message error)
              finally
                  discard base'
          }

          testCase "a mutable input inside the immutable storage root is refused"
          <| fun () ->
              let base' = root "mutable"

              try
                  let file = Path.Combine(base', "state.json")
                  File.WriteAllText(file, "{}")

                  match ResolverInput.file base' "host" file with
                  | Ok () -> failtest "mutable state inside the immutable root must be refused"
                  | Error (LaunchInputRejected (_, detail)) ->
                      Expect.isTrue
                          (detail.Contains("immutable storage root", StringComparison.Ordinal))
                          "the refusal names the immutable root"
                  | Error error -> failtestf "expected a launch-input failure, got %s" (ReleaseError.message error)
              finally
                  discard base'

          testCase "a directory where a file is required is refused"
          <| fun () ->
              let base' = root "kind"

              try
                  let directory = Path.Combine(base', "workspace")
                  Directory.CreateDirectory directory |> ignore

                  match ResolverInput.file base' "host" directory with
                  | Ok () -> failtest "a directory must not be accepted where a file is declared"
                  | Error (LaunchInputRejected (_, detail)) ->
                      Expect.isTrue (detail.Contains("directory", StringComparison.Ordinal)) "the wrong kind is identified"
                  | Error error -> failtestf "expected a launch-input failure, got %s" (ReleaseError.message error)
              finally
                  discard base'

          testCaseAsync "a linked installation tree and a linked scan entry fail closed"
          <| async {
              let base' = root "linked"

              try
                  let pinValue, _ = fixture dotnetSpec
                  let storage = unwrap (ResolverStorage.layout base' pinValue)
                  let target = Path.Combine(base', "target")
                  Directory.CreateDirectory target |> ignore
                  unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install))
                  let! installed = junctionAsync storage.Install target
                  Expect.isTrue installed "the fixture creates the installation junction"
                  Expect.isTrue (ResolverDisk.linked storage.Install) "the installation path is reported as a link"

                  match ResolverDisk.inventory storage.Install with
                  | Ok _ -> failtest "a linked installation tree must not be scanned"
                  | Error (FileFailure _) -> ()
                  | Error error -> failtestf "expected a scan failure, got %s" (ReleaseError.message error)

                  match ResolverDisk.entries storage.Install with
                  | Ok _ -> failtest "a linked directory must not be listed"
                  | Error (FileFailure _) -> ()
                  | Error error -> failtestf "expected an entries failure, got %s" (ReleaseError.message error)

                  let tree = Path.Combine(base', "tree")
                  Directory.CreateDirectory tree |> ignore
                  let! nested = junctionAsync (Path.Combine(tree, "nested")) target
                  Expect.isTrue nested "the fixture creates a nested junction"

                  match ResolverDisk.inventory tree with
                  | Ok _ -> failtest "a tree containing a link must not be scanned"
                  | Error (FileFailure _) -> ()
                  | Error error -> failtestf "expected a scan failure, got %s" (ReleaseError.message error)
              finally
                  discard base'
          }

          testCaseAsync "staging cleanup deletes only proven resolver-owned content"
          <| async {
              do!
                  scopedAsync
                      "staging"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! _ = installOnce base' pinValue archive
                              unwrap (ResolverDisk.remove storage.Root storage.Install)

                              // A byte-identical partial tree is reproducible resolver output.
                              let staged = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                              Directory.CreateDirectory staged |> ignore

                              for name, payload in bodies dotnetSpec |> List.truncate 2 do
                                  File.WriteAllBytes(Path.Combine(staged, name), payload)

                              // An unrecognized name is never treated as resolver-owned.
                              let operatorDirectory = Path.Combine(storage.Staging, "operator-notes")
                              Directory.CreateDirectory operatorDirectory |> ignore
                              File.WriteAllText(Path.Combine(operatorDirectory, "notes.txt"), "keep me")

                              // A GUID name is insufficient without byte-identical content.
                              let foreign = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                              Directory.CreateDirectory foreign |> ignore
                              File.WriteAllText(Path.Combine(foreign, "payload.bin"), "not resolver output")

                              let! _ = resolveOffline base' pinValue
                              Expect.isFalse (Directory.Exists staged) "a proven byte-identical staging entry is removed"
                              Expect.isTrue (Directory.Exists operatorDirectory) "an unrecognized staging entry is preserved"
                              Expect.isTrue (Directory.Exists foreign) "staging content that is not resolver output is preserved"
                              Expect.isTrue (File.Exists(Path.Combine(foreign, "payload.bin"))) "preserved content keeps its bytes"
                          })
          }

          testCaseAsync "a linked staging entry is preserved rather than followed"
          <| async {
              do!
                  scopedAsync
                      "staging-link"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! _ = installOnce base' pinValue archive
                              unwrap (ResolverDisk.remove storage.Root storage.Install)

                              let outside = Path.Combine(base', "outside")
                              Directory.CreateDirectory outside |> ignore
                              File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside content")

                              let staged = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                              Directory.CreateDirectory staged |> ignore
                              let! linked = junctionAsync (Path.Combine(staged, "link")) outside
                              Expect.isTrue linked "the fixture creates a junction"

                              let! _ = resolveOffline base' pinValue
                              Expect.isTrue (Directory.Exists(Path.Combine(staged, "link"))) "the linked staging entry survives"
                              Expect.isTrue (File.Exists(Path.Combine(outside, "keep.txt"))) "content behind the link is untouched"
                          })
          }

          testCaseAsync "the guard denies a second exclusive holder and the lease is shared"
          <| async {
              do!
                  scopedAsync
                      "locks"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Lease))

                              match! ResolverLock.exclusive storage.Guard with
                              | Error error -> return failtestf "the exclusive guard was not acquired: %s" (ReleaseError.message error)
                              | Ok guard ->
                                  use held = guard
                                  Expect.isTrue (tryOpenExclusively storage.Guard) "the exclusive guard is enforced by the filesystem"

                                  match! ResolverLock.shared storage.Lease with
                                  | Error error -> return failtestf "the shared lease was not acquired: %s" (ReleaseError.message error)
                                  | Ok lease ->
                                      use sharedLease = lease
                                      Expect.isTrue (tryOpenShared storage.Lease) "a second consumer shares the lease"
                                      Expect.isTrue (tryOpenExclusively storage.Lease) "an exclusive holder is denied while a consumer holds the lease"
                                      return ()
                          })
          }
        ]
