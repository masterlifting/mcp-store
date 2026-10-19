// role: test
// Unix entry kinds exist only on a real Unix filesystem, so these cases plant actual pipes, sockets, devices, and dangling links and assert the resolver decides by kind instead of opening them.
// A host that cannot present these entry kinds is gated out with a named marker case rather than being reported as though the Unix behavior ran.
module Resolver.Tests.Unix

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open Expecto
open SharedFailure
open Resolver.Tests.Fixture
open Resolver.Tests.Probe

let private regularName = ResolverKind.name ResolverKind.Regular
let private directoryName = ResolverKind.name ResolverKind.Directory
let private linkName = ResolverKind.name ResolverKind.Link
let private specialName = ResolverKind.name ResolverKind.Other

let private unwrap (work: Result<'a, ReleaseError>) : 'a =
    match work with
    | Ok value -> value
    | Error error -> failtestf "%s" (ReleaseError.message error)

// The classification cases are qualified only where the fixed platform utility exists, so an absent utility fails here instead of letting a case pass on an unrelated error.
let private requireUtility (path: string) : unit =
    if not (File.Exists path) then
        failtestf "the fixed platform utility '%s' is absent, so this case cannot run on this host" path

let private kindOf (path: string) : Async<string> = async {
    requireUtility "/usr/bin/stat"

    match! ResolverKind.classify path with
    | Ok kind -> return ResolverKind.name kind
    | Error error -> return failtestf "'%s' was not classified: %s" path (ReleaseError.message error)
}

// A dangling directory link reports no directory at its own path, so the fixture creates it through the public symbolic-link API and never depends on a follow that would find nothing.
// A missing parent raises an I/O exception that names only the platform, so the fixture reports the link it could not plant instead of letting an unrelated error end the case.
let private danglingDirectoryLink (link: string) (target: string) : unit =
    try
        Directory.CreateSymbolicLink(link, target) |> ignore
    with error ->
        failtestf
            "the fixture cannot plant the dangling directory link '%s' (target '%s'): %s %s"
            link
            target
            (error.GetType().Name)
            error.Message

// The fixture path is always absolute, so it can never be parsed as an option and no shell string is involved.
let private makePipe (path: string) : Async<unit> = async {
    requireUtility "/usr/bin/mkfifo"
    let info = ProcessStartInfo("/usr/bin/mkfifo")
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.CreateNoWindow <- true
    info.ArgumentList.Add path
    use child = Process.Start info
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    do! child.WaitForExitAsync() |> Async.AwaitTask
    let! out = stdout |> Async.AwaitTask
    let! err = stderr |> Async.AwaitTask

    if child.ExitCode <> 0 then
        return failtestf "/usr/bin/mkfifo exited with %d for '%s' (stdout: %s, stderr: %s)" child.ExitCode path out err
}

// sockaddr_un stores the path inline, so a temporary base too long for the platform limit fails here rather than at a bind whose error would name nothing useful.
[<Literal>]
let private socketPathBound = 100

let private socketEntry () : string =
    let token = (Guid.NewGuid().ToString "N").Substring(0, 8)
    Path.Combine(tempDirectory, "ghi29-" + token + ".sock")

// Disposal closes the descriptor but leaves the socket node behind, so the case classifies the entry the filesystem actually keeps.
let private withSocket (body: string -> Async<unit>) : Async<unit> = async {
    let path = socketEntry ()

    if path.Length > socketPathBound then
        return failtestf "the socket fixture path is %d bytes and would exceed the inline sockaddr limit" path.Length

    let socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

    try
        socket.Bind(UnixDomainSocketEndPoint path)
        socket.Listen 1
    finally
        socket.Dispose()

    try
        do! body path
    finally
        if File.Exists path then
            File.Delete path
}

let private kindTests =
    testList
        "unix kinds"
        [ testCaseAsync "a regular file and a directory are classified from their own entry kind"
          <| async {
              do!
                  scopedAsync
                      "unix-basic"
                      (fun base' ->
                          async {
                              let file = Path.Combine(base', "payload.txt")
                              File.WriteAllBytes(file, utf8 "payload")
                              let nested = Path.Combine(base', "nested")
                              Directory.CreateDirectory(nested) |> ignore

                              let! fileKind = kindOf file
                              let! nestedKind = kindOf nested

                              Expect.equal fileKind regularName "an ordinary entry is a regular file"
                              Expect.equal nestedKind directoryName "an ordinary directory is a directory"
                          })
          }

          testCaseAsync "a file link and a directory link are links rather than their targets"
          <| async {
              do!
                  scopedAsync
                      "unix-link"
                      (fun base' ->
                          async {
                              let target = Path.Combine(base', "target")
                              Directory.CreateDirectory(target) |> ignore
                              let payload = Path.Combine(target, "payload.txt")
                              File.WriteAllText(payload, "payload")
                              let fileLink = Path.Combine(base', "payload.link")
                              let directoryLink = Path.Combine(base', "directory.link")

                              match fileSymlink fileLink payload with
                              | Ok () -> ()
                              | Error detail -> return failtestf "the fixture cannot create a file link: %s" detail

                              let! created = directoryLinkAsync directoryLink target
                              Expect.isTrue created "the fixture creates a directory link"

                              let! fileKind = kindOf fileLink
                              let! directoryKind = kindOf directoryLink

                              Expect.equal fileKind linkName "a file link is a link, not the regular file it names"
                              Expect.equal directoryKind linkName "a directory link is a link, not the directory it names"
                              Expect.equal (File.ReadAllText payload) "payload" "classification reads no bytes through a link"
                          })
          }

          testCaseAsync "a dangling file link and a dangling directory link are links, not absent entries"
          <| async {
              do!
                  scopedAsync
                      "unix-dangling-kind"
                      (fun base' ->
                          async {
                              let absent = Path.Combine(base', "absent target")
                              let fileLink = Path.Combine(base', "absent.link")
                              let directoryLink = Path.Combine(base', "absent-directory.link")

                              match fileSymlink fileLink absent with
                              | Ok () -> ()
                              | Error detail -> return failtestf "the fixture cannot create a dangling file link: %s" detail

                              danglingDirectoryLink directoryLink absent

                              Expect.isFalse (File.Exists absent) "the dangling target does not exist as a file"
                              Expect.isFalse (Directory.Exists absent) "the dangling target does not exist as a directory"

                              let! fileKind = kindOf fileLink
                              let! directoryKind = kindOf directoryLink

                              Expect.equal fileKind linkName "a dangling file link is a link, not an absent entry"
                              Expect.equal directoryKind linkName "a dangling directory link is a link, not an absent entry"
                          })
          }

          testCaseAsync "a trailing separator never turns a link into its target"
          <| async {
              do!
                  scopedAsync
                      "unix-trailing"
                      (fun base' ->
                          async {
                              let target = Path.Combine(base', "target")
                              Directory.CreateDirectory(target) |> ignore
                              let link = Path.Combine(base', "directory.link")
                              let! created = directoryLinkAsync link target
                              Expect.isTrue created "the fixture creates a directory link"

                              match! ResolverKind.classify (link + string Path.DirectorySeparatorChar) with
                              | Ok ResolverKind.Link -> ()
                              | Ok kind ->
                                  return
                                      failtestf
                                          "the trailing-separator form was classified as a %s, so the link was followed"
                                          (ResolverKind.name kind)
                              | Error(FileFailure (_, path, _)) ->
                                  Expect.equal
                                      (Path.GetFullPath path)
                                      (Path.GetFullPath link)
                                      "a refusal names the link itself rather than a component behind it"
                              | Error error ->
                                  return
                                      failtestf
                                          "the trailing-separator form failed for an unrelated reason: %s"
                                          (ReleaseError.message error)
                          })
          }

          testCaseAsync "a named pipe is a special file with no peer"
          <| async {
              do!
                  scopedAsync
                      "unix-pipe-kind"
                      (fun base' ->
                          async {
                              let pipe = Path.Combine(base', "pipe")
                              do! makePipe pipe
                              Expect.isTrue (File.Exists pipe) "the pipe exists as a filesystem entry"

                              let! kind = kindOf pipe
                              Expect.equal kind specialName "a named pipe is not a regular file"
                          })
          }

          testCaseAsync "a bound unix domain socket is a special file"
          <| async {
              do!
                  withSocket (fun path ->
                      async {
                          Expect.isTrue (File.Exists path) "the closed socket leaves its entry on the filesystem"

                          let! kind = kindOf path
                          Expect.equal kind specialName "a socket entry is not a regular file"
                      })
          }

          testCaseAsync "the null character device is a special file"
          <| async {
              let device = "/dev/null"
              Expect.isTrue (File.Exists device) "the platform null character device exists"

              // The device is classified only; no case opens, reads, or removes it.
              let! kind = kindOf device
              Expect.equal kind specialName "a character device is not a regular file"
          }

          testCaseAsync "a missing or unreachable path fails closed and names the entry"
          <| async {
              do!
                  scopedAsync
                      "unix-absent"
                      (fun base' ->
                          async {
                              let missing = Path.Combine(base', "not-there.txt")
                              let occupied = Path.Combine(base', "occupied.txt")
                              File.WriteAllBytes(occupied, utf8 "payload")

                              for path, why in [ missing, "a missing leaf"; Path.Combine(occupied, "child"), "a path under a regular file" ] do
                                  match! ResolverKind.classify path with
                                  | Ok kind -> return failtestf "%s was classified as a %s" why (ResolverKind.name kind)
                                  | Error(FileFailure (operation, reported, _)) ->
                                      Expect.equal operation "kind" "the refusal is the classification decision"
                                      Expect.equal (Path.GetFullPath reported) (Path.GetFullPath path) "the refusal names the entry"
                                  | Error error -> return failtestf "expected a classification failure, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "an expired classification budget fails closed instead of running unbounded"
          <| async {
              requireUtility "/usr/bin/stat"

              do!
                  scopedAsync
                      "unix-budget"
                      (fun base' ->
                          async {
                              let file = Path.Combine(base', "payload.txt")
                              File.WriteAllBytes(file, utf8 "payload")

                              match! ResolverKind.classifyWithin TimeSpan.Zero file with
                              | Ok kind -> return failtestf "an expired budget still classified a %s" (ResolverKind.name kind)
                              | Error(FileFailure (operation, path, detail)) ->
                                  Expect.equal operation "kind" "the refusal is the classification decision"
                                  Expect.equal (Path.GetFullPath path) (Path.GetFullPath file) "the refusal names the entry"
                                  Expect.stringContains
                                      detail
                                      "budget must be positive"
                                      "a budget that is already spent is refused before a classification child exists, so no timeout is manufactured"
                              | Error error -> return failtestf "expected a classification failure, got %s" (ReleaseError.message error)

                              // The same entry classifies once a positive budget remains, so the refusal follows the budget and not the entry.
                              let! kind = kindOf file
                              Expect.equal kind regularName "the same entry classifies once its budget has time left"
                          })
          }

          testCaseAsync "hostile but legal entry names classify and read as the regular files they are"
          <| async {
              do!
                  scopedAsync
                      "unix-names"
                      (fun base' ->
                          async {
                              let directory = Path.Combine(base', "hostile names")
                              Directory.CreateDirectory(directory) |> ignore

                              // One representative per hazard class a shell string or a line-oriented read would lose.
                              let names =
                                  [ "a name with spaces.txt"
                                    "ünïcödé ☃.txt"
                                    "two\nlines.txt"
                                    "$(id);`id` *.txt" ]

                              for name in names do
                                  let path = Path.Combine(directory, name)
                                  File.WriteAllText(path, "payload")

                                  let! kind = kindOf path
                                  Expect.equal kind regularName (sprintf "the entry named '%s' is a regular file" name)

                              let! carried = okAsync (ResolverDisk.read base' (Path.Combine(directory, "two\nlines.txt")) 1024L)
                              Expect.equal carried (utf8 "payload") "a name containing a newline is carried as one argument and read back"
                          })
          }

          testCaseAsync "a unix domain socket and the null device are refused as a launch input and a pin"
          <| async {
              do!
                  scopedAsync
                      "unix-special-input"
                      (fun base' ->
                          async {
                              do!
                                  withSocket (fun socket ->
                                      async {
                                          for candidate, why in [ socket, "the socket entry"; "/dev/null", "the null device" ] do
                                              match! ResolverInput.file base' "host" candidate with
                                              | Ok () -> return failtestf "%s was admitted as a launch input file" why
                                              | Error(LaunchInputRejected ("host", detail)) ->
                                                  Expect.stringContains detail specialName (sprintf "%s is refused by kind, not by shape" why)
                                              | Error error ->
                                                  return
                                                      failtestf
                                                          "expected a launch-input refusal for %s, got %s"
                                                          why
                                                          (ReleaseError.message error)

                                              match! ResolverInput.pin candidate with
                                              | Ok _ -> return failtestf "%s was admitted as a pin" why
                                              | Error(FileFailure ("pin", _, detail)) ->
                                                  Expect.stringContains detail specialName (sprintf "%s is refused as a pin by kind" why)
                                              | Error error ->
                                                  return
                                                      failtestf "expected a pin refusal for %s, got %s" why (ReleaseError.message error)
                                      })
                          })
          }

          testCaseAsync "a link is refused as a link by every admission seam before a missing or wrong-kind reason"
          <| async {
              do!
                  scopedAsync
                      "unix-admission-link"
                      (fun base' ->
                          async {
                              let storage = Path.Combine(base', "storage")
                              unwrap (ResolverDisk.ensureRoot storage)

                              let target = Path.Combine(base', "target")
                              Directory.CreateDirectory(target) |> ignore
                              let resolvable = Path.Combine(base', "directory.link")
                              let dangling = Path.Combine(base', "dangling.link")
                              let absent = Path.Combine(base', "absent target")

                              danglingDirectoryLink resolvable target
                              danglingDirectoryLink dangling absent

                              Expect.isTrue (Directory.Exists resolvable) "the planted link reaches a real directory"
                              Expect.isFalse (Directory.Exists dangling) "the dangling link reaches nothing"

                              // Containment is decided before the chain is walked, so a link inside the storage root would be refused for the wrong reason.
                              for candidate, why in [ resolvable, "a link to a real directory"; dangling, "a dangling link" ] do
                                  let refused = Path.GetFullPath candidate

                                  match! ResolverInput.file storage "host" candidate with
                                  | Ok () -> return failtestf "%s was admitted as a launch input file" why
                                  | Error(LaunchInputRejected ("host", detail)) ->
                                      Expect.stringContains
                                          detail
                                          linkName
                                          (sprintf "%s is refused as a link by the file seam, not as a directory or a missing file" why)

                                      Expect.stringContains detail refused (sprintf "%s names the refused link itself" why)
                                  | Error error ->
                                      return
                                          failtestf
                                              "expected a launch input file refusal for %s, got %s"
                                              why
                                              (ReleaseError.message error)

                                  match! ResolverInput.directory storage "workspace" candidate with
                                  | Ok () -> return failtestf "%s was admitted as a launch input directory" why
                                  | Error(LaunchInputRejected ("workspace", detail)) ->
                                      Expect.stringContains
                                          detail
                                          linkName
                                          (sprintf "%s is refused as a link by the directory seam, not as a missing directory" why)

                                      Expect.stringContains detail refused (sprintf "%s names the refused link itself" why)
                                  | Error error ->
                                      return
                                          failtestf
                                              "expected a launch input directory refusal for %s, got %s"
                                              why
                                              (ReleaseError.message error)

                                  match! ResolverInput.pin candidate with
                                  | Ok _ -> return failtestf "%s was admitted as a pin" why
                                  | Error(FileFailure ("pin", reported, detail)) ->
                                      Expect.equal (Path.GetFullPath reported) refused "the pin refusal names the pin path itself"

                                      Expect.stringContains detail linkName (sprintf "%s is refused as a link by the pin seam" why)
                                      Expect.stringContains detail refused (sprintf "%s names the refused link itself" why)
                                  | Error error ->
                                      return failtestf "expected a pin refusal for %s, got %s" why (ReleaseError.message error)

                              Expect.equal (Directory.GetFileSystemEntries target).Length 0 "no seam created anything behind a link it refused"
                              Expect.isFalse (Directory.Exists absent) "the dangling target is never created through a refused link"
                              Expect.isTrue (ResolverDisk.linked resolvable) "the refused link is preserved for the operator"
                              Expect.isTrue (ResolverDisk.linked dangling) "the refused dangling link is preserved for the operator"
                          })
          } ]

let private callerTests =
    testList
        "unix callers"
        [ testCaseAsync "a linked installation path is blocked before publication and its target content is untouched"
          <| async {
              do!
                  scopedAsync
                      "unix-linked-install"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install))

                              let outside = Path.Combine(base', "outside revision")
                              Directory.CreateDirectory(outside) |> ignore
                              File.WriteAllText(Path.Combine(outside, "operator.txt"), "operator content")
                              let! linked = directoryLinkAsync storage.Install outside
                              Expect.isTrue linked "the fixture links the installation path to an existing directory"

                              let downloads = ref 0

                              match! ResolverInstall.resolve (serving archive downloads) base' pinValue with
                              | Ok resolution ->
                                  resolution.Lease.Release()
                                  return failtest "a linked installation path must never resolve"
                              | Error(CorruptInstall (path, detail)) ->
                                  Expect.equal (Path.GetFullPath path) (Path.GetFullPath storage.Install) "the blocked revision names the installation path"
                                  Expect.stringContains detail "link or reparse point" "the block names the link rather than the content behind it"
                                  Expect.equal downloads.Value 1 "the pinned bytes are verified once before the block"
                                  Expect.isTrue (ResolverDisk.linked storage.Install) "the installation path is still a link"
                                  Expect.equal (File.ReadAllText(Path.Combine(outside, "operator.txt"))) "operator content" "content behind the link is untouched"
                                  Expect.equal (Directory.GetFileSystemEntries outside).Length 1 "nothing was published behind the link"
                              | Error error -> return failtestf "expected a blocked installation, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "a dangling installation link is blocked and is never treated as an absent revision"
          <| async {
              do!
                  scopedAsync
                      "unix-dangling-install"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)

                              // The fixture root exists but the installs parent does not, and the symbolic-link API creates no parent of its own.
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Install))

                              danglingDirectoryLink storage.Install (Path.Combine(base', "absent revision"))
                              Expect.isFalse (Directory.Exists storage.Install) "the dangling installation path has no revision behind it"

                              let downloads = ref 0

                              match! ResolverInstall.resolve (serving archive downloads) base' pinValue with
                              | Ok resolution ->
                                  resolution.Lease.Release()
                                  return failtest "a dangling installation link must never resolve"
                              | Error error ->
                                  Expect.isTrue (ResolverDisk.linked storage.Install) "a dangling installation path is still a link"
                                  Expect.isFalse (Directory.Exists storage.Install) "no revision is published through the dangling link"
                                  Expect.stringContains (ReleaseError.message error) "link or reparse point" "the refusal names the link rather than reporting an absent revision"
                                  Expect.equal downloads.Value 1 "the pinned bytes are verified once before the block"
                          })
          }

          testCaseAsync "a dangling staging or ancestor link is refused before anything is staged or published"
          <| async {
              do!
                  scopedAsync
                      "unix-dangling-staging"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)

                              // The staging parent is the only path the fixture may create, because a link cannot be planted at a name whose parent is absent.
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Staging))

                              danglingDirectoryLink storage.Staging (Path.Combine(base', "absent staging"))

                              let downloads = ref 0

                              match! ResolverInstall.resolve (serving archive downloads) base' pinValue with
                              | Ok resolution ->
                                  resolution.Lease.Release()
                                  return failtest "a dangling staging link must never be created through"
                              | Error(FileFailure (operation, path, detail)) ->
                                  Expect.equal operation "link" "the refusal is the link-chain decision"
                                  Expect.equal (Path.GetFullPath path) (Path.GetFullPath storage.Staging) "the refusal names the staging path"
                                  Expect.stringContains detail "link or reparse point" "the refusal names the link"
                                  Expect.equal downloads.Value 0 "the staging refusal happens before any download"
                              | Error error -> return failtestf "expected a staging refusal, got %s" (ReleaseError.message error)
                          })

              do!
                  scopedAsync
                      "unix-dangling-ancestor"
                      (fun base' ->
                          async {
                              let pinValue, archive = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              let installs = Path.Combine(base', "installs")
                              danglingDirectoryLink installs (Path.Combine(base', "absent installs"))

                              let downloads = ref 0

                              match! ResolverInstall.resolve (serving archive downloads) base' pinValue with
                              | Ok resolution ->
                                  resolution.Lease.Release()
                                  return failtest "a dangling ancestor of the installation must never publish"
                              | Error(FileFailure (operation, path, detail)) ->
                                  Expect.equal operation "link" "the refusal is the link-chain decision"
                                  Expect.equal (Path.GetFullPath path) (Path.GetFullPath installs) "the refusal names the linked ancestor"
                                  Expect.stringContains detail "link or reparse point" "the refusal names the link"
                              | Error error -> return failtestf "expected a publication refusal, got %s" (ReleaseError.message error)

                              Expect.equal downloads.Value 1 "the pinned bytes are verified once before publication is refused"
                              Expect.isFalse (Directory.Exists storage.Install) "no revision is published through the dangling ancestor"
                              Expect.isTrue (ResolverDisk.linked installs) "the dangling ancestor is preserved for the operator"
                          })
          }

          testCaseAsync "a dangling coordination entry is refused and a missing regular leaf is still created"
          <| async {
              do!
                  scopedAsync
                      "unix-dangling-lock"
                      (fun base' ->
                          async {
                              let pinValue, _ = fixture dotnetSpec
                              let storage = unwrap (ResolverStorage.layout base' pinValue)
                              unwrap (ResolverDisk.ensureRoot storage.Root)
                              unwrap (ResolverDisk.ensureDirectory storage.Root (Path.GetDirectoryName storage.Guard))
                              let locks = Path.GetDirectoryName storage.Guard
                              let absent = Path.Combine(base', "absent.coordination")

                              let entries =
                                  [ storage.Guard, ResolverLock.exclusive
                                    storage.Lease, ResolverLock.shared ]

                              for planted, _ in entries do
                                  match fileSymlink planted absent with
                                  | Ok () -> ()
                                  | Error detail -> return failtestf "the fixture cannot create a dangling coordination link: %s" detail

                              for planted, acquire in entries do
                                  match! acquire planted with
                                  | Ok held ->
                                      held.Release()
                                      return failtestf "the coordination entry '%s' was opened through a dangling link" planted
                                  | Error(FileFailure (operation, path, detail)) ->
                                      Expect.equal operation "link" "the refusal is the link-chain decision"
                                      Expect.equal (Path.GetFullPath path) (Path.GetFullPath planted) "the refusal names the planted entry"
                                      Expect.stringContains detail "link or reparse point" "the refusal names the link"
                                  | Error error -> return failtestf "expected a coordination refusal, got %s" (ReleaseError.message error)

                              Expect.isFalse (File.Exists absent) "the absent target is never created through a refused entry"

                              // The refusal is specific to the linked leaf, so a missing regular sibling is still created.
                              let plain = Path.Combine(locks, "plain.guard")

                              match! ResolverLock.exclusive plain with
                              | Ok held -> held.Release()
                              | Error error -> return failtestf "a missing regular coordination entry was not created: %s" (ReleaseError.message error)

                              Expect.isTrue (File.Exists plain) "a missing regular coordination leaf is still created"
                          })
          }

          testCaseAsync "a named pipe is refused by every caller before any open"
          <| async {
              do!
                  scopedAsync
                      "unix-pipe-callers"
                      (fun base' ->
                          async {
                              let tree = Path.Combine(base', "tree")
                              Directory.CreateDirectory(tree) |> ignore
                              let pipe = Path.Combine(tree, "pipe")
                              do! makePipe pipe

                              let child = startProbe "special" [ base'; pipe; tree ]
                              let! code, out, err = awaitProbeWithin (TimeSpan.FromSeconds 60.) child

                              Expect.equal code 0 $"every caller refuses the pipe by kind (stderr: {err})"
                              Expect.stringContains out "ok 7" "the child reports each refusal it checked"
                              Expect.isTrue (File.Exists pipe) "a refused cleanup leaves the pipe in place"
                              Expect.equal (Directory.GetFileSystemEntries tree).Length 1 "a refused inventory and cleanup remove nothing"
                          })
          } ]

// The Unix behavior is unqualified wherever it has not actually run, so a Windows log names the gap instead of omitting it.
let private qualified =
    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        [ kindTests; callerTests ]
    else
        [ testCase
              "the unix special-entry cases are unqualified on this host and did not run"
              (fun () ->
                  Expect.isTrue
                      (OperatingSystem.IsWindows())
                      "the gated marker only stands for a host that cannot present unix entry kinds") ]

let tests = testList "unix" qualified