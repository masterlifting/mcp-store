// Filesystem safety on the real host tree: link-free external chains, immutable-root containment, kind classification without opening, and bounded staging recovery.
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

// An admission refusal names the refused component and its kind, so a case asserts that typed decision instead of the generic failure an open of a planted entry raises.
let private admission (error: ReleaseError) (operation: string) (path: string) (detail: string) : unit =
    match error with
    | FileFailure (reported, name, reason) ->
        Expect.equal reported operation "the refusal is the admission decision, not an open failure"
        Expect.equal (Path.GetFullPath name) (Path.GetFullPath path) "the refusal names the refused entry"
        Expect.stringContains reason detail $"the refusal states '{detail}'"
    | InstallInUse _ -> failtest "the refused entry was retried as coordination contention instead of refused"
    | other -> failtestf "expected an admission refusal, got %s" (ReleaseError.message other)

// One physical entry answers to one name on a case-folding host and two on a case-sensitive one, so the case-differing refusals run only where the host is known to fold case rather than merely configured to.
let private caseTests =
    if OperatingSystem.IsWindows() then
        [ testCaseAsync "the run path refuses in-store launch inputs that differ from the root only by case"
          <| async {
              do!
                  scopedAsync
                      "case"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let store = Path.Combine(base', "immutable store")
                              let storage = unwrap (ResolverStorage.layout store pinValue)
                              let stored = Path.Combine(store, "Consumer State")
                              Directory.CreateDirectory stored |> ignore
                              let hostFile = Path.Combine(stored, "host.exe")
                              File.WriteAllBytes(hostFile, utf8 "not a host")
                              let workspace = Path.Combine(base', "hostile workspace")
                              Directory.CreateDirectory workspace |> ignore
                              let pinPath = writePin base' pinValue
                              let downloads = ref 0

                              let run: string -> string -> ResolverCli.Run =
                                  fun workspacePath hostPath ->
                                      { Pin = pinPath
                                        Root = Some store
                                        Workspace = workspacePath
                                        Host = hostPath
                                        Inputs = [] }

                              match! ResolverRun.run (offline downloads) (run (stored.ToUpperInvariant()) (hostFile.ToUpperInvariant())) with
                              | Ok _ -> return failtest "a workspace inside the immutable store must never launch"
                              | Error(LaunchInputRejected (input, detail)) ->
                                  Expect.equal input "workspace" "the in-store workspace names its input"
                                  Expect.stringContains detail "immutable storage root" "the workspace refusal names the immutable root"
                              | Error error -> return failtestf "expected a workspace refusal, got %s" (ReleaseError.message error)

                              match! ResolverRun.run (offline downloads) (run workspace (hostFile.ToUpperInvariant())) with
                              | Ok _ -> return failtest "a host inside the immutable store must never launch"
                              | Error(LaunchInputRejected (input, detail)) ->
                                  Expect.equal input "host" "the in-store host names its input"
                                  Expect.stringContains detail "immutable storage root" "the host refusal names the immutable root"
                              | Error error -> return failtestf "expected a host refusal, got %s" (ReleaseError.message error)

                              Expect.equal downloads.Value 0 "neither refusal reaches the transport"
                          })
          }

          testCaseAsync "a declared typed input naming an in-store path is refused"
          <| async {
              let base' = root "case-input"

              try
                  let pinValue, _ = fixture dotnetSpec
                  let store = Path.Combine(base', "immutable store")
                  let stored = Path.Combine(store, "consumer state")
                  Directory.CreateDirectory stored |> ignore
                  let hostFile = Path.Combine(base', "dotnet.exe")
                  File.WriteAllBytes(hostFile, utf8 "not a host")
                  let descriptor = (manifest dotnetSpec dotnetSpec.Revision (bodies dotnetSpec)).Launch

                  let supplied =
                      [ "dotnetHost", hostFile
                        "artifactRoot", stored.ToUpperInvariant() ]

                  match! ResolverInput.inputs store descriptor supplied with
                  | Ok () -> return failtest "mutable state inside the immutable store must be refused"
                  | Error(LaunchInputRejected (input, detail)) ->
                      Expect.equal input "artifactRoot" "the in-store input names itself"
                      Expect.stringContains detail "immutable storage root" "the refusal names the immutable root"
                  | Error error -> return failtestf "expected a launch-input refusal, got %s" (ReleaseError.message error)
              finally
                  discard base'
          } ]
    else
        []

let tests =
    testList
        "tree"
        [ testCaseAsync "a directory link on an external path chain is refused"
          <| async {
              let base' = root "chain"

              try
                  let target = Path.Combine(base', "target")
                  let link = Path.Combine(base', "link")
                  Directory.CreateDirectory target |> ignore
                  File.WriteAllText(Path.Combine(target, "data.txt"), "payload")
                  let! created = directoryLinkAsync link target
                  Expect.isTrue created "the fixture creates a directory link"

                  match ResolverDisk.chain (Path.Combine(link, "data.txt")) with
                  | Ok () -> failtest "a link in the chain must be refused"
                  | Error (FileFailure (_, path, _)) ->
                      Expect.equal (Path.GetFullPath link) (Path.GetFullPath path) "the link component is named"
                  | Error error -> failtestf "expected a link failure, got %s" (ReleaseError.message error)
              finally
                  discard base'
          }

          testCaseAsync "a mutable input inside the immutable storage root is refused"
          <| async {
              let base' = root "mutable"

              try
                  let file = Path.Combine(base', "state.json")
                  File.WriteAllText(file, "{}")

                  match! ResolverInput.file base' "host" file with
                  | Ok () -> return failtest "mutable state inside the immutable root must be refused"
                  | Error (LaunchInputRejected (_, detail)) ->
                      Expect.isTrue
                          (detail.Contains("immutable storage root", StringComparison.Ordinal))
                          "the refusal names the immutable root"
                  | Error error -> return failtestf "expected a launch-input failure, got %s" (ReleaseError.message error)
              finally
                  discard base'
          }

          testCaseAsync "a directory where a file is required is refused"
          <| async {
              let base' = root "kind"

              try
                  let store = Path.Combine(base', "immutable store")
                  let directory = Path.Combine(base', "workspace")
                  Directory.CreateDirectory store |> ignore
                  Directory.CreateDirectory directory |> ignore

                  // Containment is decided before the kind is, so only a directory outside the store reaches the wrong-kind refusal.
                  match! ResolverInput.file store "host" directory with
                  | Ok () -> return failtest "a directory must not be accepted where a file is declared"
                  | Error (LaunchInputRejected (_, detail)) ->
                      Expect.isTrue (detail.Contains("directory", StringComparison.Ordinal)) "the wrong kind is identified"
                  | Error error -> return failtestf "expected a launch-input failure, got %s" (ReleaseError.message error)
              finally
                  discard base'
          }

          testCaseAsync "a linked installation tree and a linked scan entry fail closed"
          <| async {
              let base' = root "linked"

              try
                  let pinValue, _ = fixture dotnetSpec
                  let storage = unwrap (ResolverStorage.layout base' pinValue)
                  let target = Path.Combine(base', "target")
                  Directory.CreateDirectory target |> ignore
                  unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install))
                  let! installed = directoryLinkAsync storage.Install target
                  Expect.isTrue installed "the fixture creates the installation link"
                  Expect.isTrue (ResolverDisk.linked storage.Install) "the installation path is reported as a link"

                  match! ResolverDisk.inventory storage.Install with
                  | Ok _ -> return failtest "a linked installation tree must not be scanned"
                  | Error (FileFailure _) -> ()
                  | Error error -> return failtestf "expected a scan failure, got %s" (ReleaseError.message error)

                  match ResolverDisk.entries storage.Install with
                  | Ok _ -> return failtest "a linked directory must not be listed"
                  | Error (FileFailure _) -> ()
                  | Error error -> return failtestf "expected an entries failure, got %s" (ReleaseError.message error)

                  let tree = Path.Combine(base', "tree")
                  Directory.CreateDirectory tree |> ignore
                  let! nested = directoryLinkAsync (Path.Combine(tree, "nested")) target
                  Expect.isTrue nested "the fixture creates a nested directory link"

                  match! ResolverDisk.inventory tree with
                  | Ok _ -> return failtest "a tree containing a link must not be scanned"
                  | Error (FileFailure _) -> ()
                  | Error error -> return failtestf "expected a scan failure, got %s" (ReleaseError.message error)
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
                              let! _ = okAsync (ResolverDisk.remove storage.Root storage.Install)

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
                              let! _ = okAsync (ResolverDisk.remove storage.Root storage.Install)

                              let outside = Path.Combine(base', "outside")
                              Directory.CreateDirectory outside |> ignore
                              File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside content")

                              let staged = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                              Directory.CreateDirectory staged |> ignore
                              let! linked = directoryLinkAsync (Path.Combine(staged, "link")) outside
                              Expect.isTrue linked "the fixture creates a directory link"

                              let! _ = resolveOffline base' pinValue
                              Expect.isTrue (Directory.Exists(Path.Combine(staged, "link"))) "the linked staging entry survives"
                              Expect.isTrue (File.Exists(Path.Combine(outside, "keep.txt"))) "content behind the link is untouched"
                          })
          }

          testCase "containment follows the platform's own path comparison"
          <| fun () ->
              let base' = root "containment"

              try
                  let store = Path.Combine(base', "store")
                  Directory.CreateDirectory store |> ignore
                  let descendant = Path.Combine(store, "Consumer State", "profile.json")

                  match ResolverDisk.under store descendant with
                  | Ok contained -> Expect.equal contained (Path.GetFullPath descendant) "a descendant is inside the root"
                  | Error error -> failtestf "a descendant must be inside the root, got %s" (ReleaseError.message error)

                  match ResolverDisk.under store (store + "-sibling") with
                  | Ok _ -> failtest "a sibling that shares the root's name prefix is not inside the root"
                  | Error(FileFailure ("containment", _, _)) -> ()
                  | Error error -> failtestf "expected a containment failure, got %s" (ReleaseError.message error)

                  // macOS volumes may fold case as Windows ones do, so the compared policy follows that pair rather than the host that runs the suite.
                  let folding = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                  let cased = descendant.ToUpperInvariant()

                  match ResolverDisk.under store cased, folding with
                  | Ok contained, true -> Expect.equal contained (Path.GetFullPath cased) "a case-folded host contains the case-differing name"
                  | Error _, false -> ()
                  | Ok _, false -> failtest "a case-sensitive host keeps the case-differing name outside the root"
                  | Error error, true -> failtestf "a case-folded host must contain the case-differing name, got %s" (ReleaseError.message error)
              finally
                  discard base'

          testCaseAsync "the bounded recovery pass evaluates no more than sixteen staging candidates"
          <| async {
              do!
                  scopedAsync
                      "recovery-bound"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! _ = installOnce base' pinValue archive
                              let! _ = okAsync (ResolverDisk.remove storage.Root storage.Install)

                              // Every candidate is a byte-identical copy of verified members, so the number removed is the number the pass evaluated, in any order it sees.
                              let candidates =
                                  [ for _ in 1 .. 32 do
                                      let entry = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                                      Directory.CreateDirectory entry |> ignore

                                      for name, payload in bodies dotnetSpec |> List.truncate 2 do
                                          File.WriteAllBytes(Path.Combine(entry, name), payload)

                                      entry ]

                              let! _ = resolveOffline base' pinValue

                              Expect.equal candidates.Length 32 "the staging area holds more candidates than the pass may evaluate"
                              Expect.equal (Directory.GetDirectories storage.Staging).Length 16 "the pass evaluates and removes exactly its bounded candidate count"
                              Expect.isTrue (File.Exists(Path.Combine(storage.Install, dotnetSpec.Entrypoint))) "the bounded pass still publishes the revision"
                          })
          }

          testCaseAsync "a staging candidate that cannot be inventoried is preserved and never fails the pass"
          <| async {
              do!
                  scopedAsync
                      "recovery-preserve"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let! _ = installOnce base' pinValue archive
                              let! _ = okAsync (ResolverDisk.remove storage.Root storage.Install)

                              // A link fails every inventory of the entry, so no evaluation can decide it is resolver output and the bounded pass must leave it for the operator.
                              let outside = Path.Combine(base', "operator")
                              Directory.CreateDirectory outside |> ignore
                              File.WriteAllText(Path.Combine(outside, "keep.txt"), "operator content")

                              let guarded = ResizeArray<string>()

                              for _ in 1 .. 20 do
                                  let entry = Path.Combine(storage.Staging, Guid.NewGuid().ToString "N")
                                  Directory.CreateDirectory entry |> ignore
                                  let! linked = directoryLinkAsync (Path.Combine(entry, "link")) outside
                                  Expect.isTrue linked "the fixture guards a staging entry behind a link"
                                  guarded.Add entry

                              let unrecognized = Path.Combine(storage.Staging, "operator-notes")
                              Directory.CreateDirectory unrecognized |> ignore

                              let! _ = resolveOffline base' pinValue

                              for entry in guarded do
                                  Expect.isTrue (Directory.Exists(Path.Combine(entry, "link"))) "an entry that cannot be inventoried is preserved"

                              Expect.isTrue (Directory.Exists unrecognized) "an unrecognized staging entry is preserved"
                              Expect.equal (File.ReadAllText(Path.Combine(outside, "keep.txt"))) "operator content" "content outside the store is untouched"
                              Expect.isTrue (File.Exists(Path.Combine(storage.Install, dotnetSpec.Entrypoint))) "an unprovable candidate does not fail the install"
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

          testCaseAsync "a missing coordination entry is created and an existing regular one is reused"
          <| async {
              do!
                  scopedAsync
                      "reuse-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Guard))

                              match! ResolverLock.exclusive storage.Guard with
                              | Error error -> return failtestf "a missing guard was not created: %s" (ReleaseError.message error)
                              | Ok guard ->
                                  use held = guard
                                  Expect.isTrue (File.Exists storage.Guard) "a missing guard is created on first acquisition"
                                  Expect.equal (new FileInfo(storage.Guard)).Length 0L "a created guard starts empty"
                                  Expect.isTrue (tryOpenExclusively storage.Guard) "a created guard is exclusive"

                              match! ResolverLock.shared storage.Lease with
                              | Error error -> return failtestf "a missing lease was not created: %s" (ReleaseError.message error)
                              | Ok lease ->
                                  use held = lease
                                  Expect.isTrue (File.Exists storage.Lease) "a missing lease is created on first acquisition"
                                  Expect.isTrue (tryOpenShared storage.Lease) "a created lease is shared"
                                  Expect.isTrue (tryOpenExclusively storage.Lease) "an exclusive holder is denied while a consumer holds a created lease"

                              Expect.isTrue (File.Exists storage.Guard) "a released guard is never unlinked"
                              Expect.isTrue (File.Exists storage.Lease) "a released lease is never unlinked"

                              // Coordination never writes to its own entry, so an existing regular file is taken as it stands instead of being recreated or emptied.
                              let marker = utf8 "operator coordination bytes"
                              File.WriteAllBytes(storage.Guard, marker)
                              File.WriteAllBytes(storage.Lease, marker)

                              match! ResolverLock.exclusive storage.Guard with
                              | Error error -> return failtestf "an existing regular guard was refused: %s" (ReleaseError.message error)
                              | Ok guard ->
                                  use held = guard
                                  Expect.isTrue (tryOpenExclusively storage.Guard) "a reused guard is still exclusive"

                                  match! ResolverLock.shared storage.Lease with
                                  | Error error -> return failtestf "an existing regular lease was refused: %s" (ReleaseError.message error)
                                  | Ok lease ->
                                      use held = lease
                                      Expect.isTrue (tryOpenShared storage.Lease) "a reused lease is shared"

                              // Bytes are read only once no holder is left, because reading a coordination entry another holder has open is itself the contention this suite asserts.
                              Expect.equal (File.ReadAllBytes storage.Guard) marker "an existing guard is reused without truncation"
                              Expect.equal (File.ReadAllBytes storage.Lease) marker "an existing lease is reused without truncation"
                              Expect.isTrue (File.Exists storage.Guard) "a reused guard is still never unlinked"
                              Expect.isTrue (File.Exists storage.Lease) "a reused lease is still never unlinked"
                          })
          }

          testCaseAsync "a linked coordination entry is refused before any open"
          <| async {
              do!
                  scopedAsync
                      "linked-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Guard))
                              let locks = Path.GetDirectoryName storage.Guard
                              let outside = Path.Combine(base', "outside")
                              Directory.CreateDirectory outside |> ignore
                              File.WriteAllText(Path.Combine(outside, "operator.txt"), "operator content")

                              // An unguarded open of a link leaf creates the coordination entry inside a tree the store never owns, so the refusal is asserted with that tree still holding exactly the operator content it started with.
                              for planted, acquire in [ Path.Combine(locks, "planted.guard"), ResolverLock.exclusive;
                                                         Path.Combine(locks, "planted.lease"), ResolverLock.shared ] do
                                  let! created = directoryLinkAsync planted outside
                                  Expect.isTrue created "the fixture plants a directory link where the coordination entry belongs"

                                  match! acquire planted with
                                  | Ok held ->
                                      held.Release()
                                      return failtestf "the coordination entry '%s' was opened through a link" planted
                                  | Error error -> admission error "link" planted "the path is a link or reparse point"

                              Expect.equal (Directory.GetFileSystemEntries outside).Length 1 "no coordination entry was created behind the link"
                              Expect.equal (File.ReadAllText(Path.Combine(outside, "operator.txt"))) "operator content" "content outside the store is untouched"
                              Expect.isFalse (tryOpenExclusively (Path.Combine(outside, "operator.txt"))) "a refused admission leaves no handle on the tree it refused"
                          })
          }

          testCaseAsync "a file-symlink coordination entry never creates or truncates its target"
          <| async {
              do!
                  scopedAsync
                      "symlink-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Guard))
                              let locks = Path.GetDirectoryName storage.Guard
                              let outside = Path.Combine(base', "outside")
                              Directory.CreateDirectory outside |> ignore
                              let absent = Path.Combine(outside, "absent.guard")
                              let present = Path.Combine(outside, "present.lease")
                              let marker = utf8 "operator coordination bytes"
                              File.WriteAllBytes(present, marker)
                              let absentLink = Path.Combine(locks, "absent.guard")
                              let presentLink = Path.Combine(locks, "present.lease")

                              match fileSymlink absentLink absent, fileSymlink presentLink present with
                              | Ok (), Ok () ->
                                  for planted, acquire in [ absentLink, ResolverLock.exclusive;
                                                            presentLink, ResolverLock.shared ] do
                                      let! classified = ResolverKind.classify planted

                                      Expect.equal
                                          (classified |> Result.map ResolverKind.name |> Result.defaultValue "")
                                          "link or reparse point"
                                          "the planted entry is a link before any open"

                                      match! acquire planted with
                                      | Ok held ->
                                          held.Release()
                                          return failtestf "the coordination entry '%s' was opened through a file symlink" planted
                                      | Error error -> admission error "link" planted "the path is a link or reparse point"

                                  Expect.isFalse (File.Exists absent) "an absent target is never created through a refused link"
                                  Expect.equal (File.ReadAllBytes present) marker "an existing target keeps its bytes through a refused link"
                                  Expect.isFalse (tryOpenExclusively present) "a refused link leaves no handle on its target"
                              | unavailable, _ ->
                                  // The privilege is absent here, so the file-symlink coverage is named rather than passed over, and the same leaf rule is still proven with a directory link.
                                  eprintfn "the file-symlink coordination cases did not run: %A" unavailable
                                  let! created = directoryLinkAsync absentLink outside
                                  Expect.isTrue created "the fixture plants a directory link where the file symlink was unavailable"

                                  match! ResolverLock.exclusive absentLink with
                                  | Ok held ->
                                      held.Release()
                                      return failtest "a linked coordination entry was opened"
                                  | Error error -> admission error "link" absentLink "the path is a link or reparse point"

                                  Expect.equal (Directory.GetFileSystemEntries outside).Length 1 "no coordination entry was created behind the link"
                                  Expect.equal (File.ReadAllBytes present) marker "content outside the store is untouched"
                          })
          }

          testCaseAsync "a coordination entry that is not a regular file is refused before any open"
          <| async {
              do!
                  scopedAsync
                      "kind-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Guard))

                              // Windows classifies no leaf as neither directory, regular, nor link, so a directory is the one non-regular entry this host can actually present.
                              for planted, acquire in [ storage.Guard, ResolverLock.exclusive;
                                                        storage.Lease, ResolverLock.shared ] do
                                  Directory.CreateDirectory planted |> ignore

                                  match! acquire planted with
                                  | Ok held ->
                                      held.Release()
                                      return failtestf "the coordination entry '%s' was opened as a directory" planted
                                  | Error error -> admission error "lock" planted "the coordination entry is a directory, not a regular file"

                                  Expect.isTrue (Directory.Exists planted) "the refused entry is still a directory"
                                  Expect.equal (Directory.GetFileSystemEntries planted).Length 0 "no coordination entry was created inside the refused directory"
                          })
          }

          testCaseAsync "a linked ancestor of a coordination entry is refused before any open"
          <| async {
              do!
                  scopedAsync
                      "ancestor-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              let outside = Path.Combine(base', "outside")
                              Directory.CreateDirectory outside |> ignore
                              File.WriteAllText(Path.Combine(outside, "operator.txt"), "operator content")
                              let linkedLocks = Path.Combine(base', "linked locks")
                              let! created = directoryLinkAsync linkedLocks outside
                              Expect.isTrue created "the fixture plants a directory link where the locks directory belongs"

                              for planted, acquire in [ Path.Combine(linkedLocks, Path.GetFileName storage.Guard), ResolverLock.exclusive;
                                                        Path.Combine(linkedLocks, Path.GetFileName storage.Lease), ResolverLock.shared ] do
                                  match! acquire planted with
                                  | Ok held ->
                                      held.Release()
                                      return failtestf "the coordination entry '%s' was opened through a linked ancestor" planted
                                  | Error error -> admission error "link" linkedLocks "the path is a link or reparse point"

                              Expect.equal (Directory.GetFileSystemEntries outside).Length 1 "no coordination entry was created behind the linked ancestor"
                              Expect.equal (File.ReadAllText(Path.Combine(outside, "operator.txt"))) "operator content" "content outside the store is untouched"
                          })
          }

          yield! caseTests
        ]
