// role: test
// The classification runner is the boundary, so these cases drive the internal adapter through a controlled child process rather than a fabricated result.
// Pure record cases pin the parser and the type-bit decision; process cases pin the typed refusals, the exact forwarded arguments, and the child's reaping.
module Resolver.Tests.Stat

open System
open System.Diagnostics
open System.IO
open Expecto
open SharedFailure
open Resolver.Tests.Fixture
open Resolver.Tests.Probe

let private regularName = ResolverKind.name ResolverKind.Regular
let private directoryName = ResolverKind.name ResolverKind.Directory
let private linkName = ResolverKind.name ResolverKind.Link
let private specialName = ResolverKind.name ResolverKind.Other

let private hex = 16
let private octal = 8
let private childDeadline = TimeSpan.FromSeconds 20.

// The runner owns its own deadline, so an outer bound turns a runner that stops enforcing it into a reported failure instead of a wedged suite.
let private outerWatchdog = TimeSpan.FromSeconds 60.

let private bounded (watchdog: TimeSpan) (work: Async<'T>) : Async<'T> =
    async {
        try
            return!
                work
                |> Async.StartAsTask
                |> fun running -> running.WaitAsync watchdog
                |> Async.AwaitTask
        with :? TimeoutException ->
            return failtestf "the bounded runner did not settle within %O" watchdog
    }

let private running (pid: int) : bool =
    try
        use child = Process.GetProcessById pid
        not child.HasExited
    with
    | :? ArgumentException
    | :? InvalidOperationException -> false

// The runner returns only after its own cleanup window, so a bounded poll observes the child instead of trusting that the kill was issued.
let private stillAlive (pid: int) : Async<bool> =
    async {
        let mutable remaining = 30

        while remaining > 0 && running pid do
            do! Async.Sleep 100
            remaining <- remaining - 1

        return running pid
    }

// The child binds only the arguments dispatched after the probe selector, so the receipt expectation covers the payload rather than the muxer arguments that carried it.
let private expectedChild (mode: string) (receipt: string) (entry: string) : string list =
    [ mode; receipt; entry ]

// A hosted entry name carries a space, so an argument that survived as one value rather than as a shell string is part of every forwarding assertion.
let private hosted (baseDirectory: string) (mode: string) : string * string =
    let entry = Path.Combine(baseDirectory, "hosted " + mode + ".txt")
    File.WriteAllText(entry, "payload")
    entry, Path.Combine(baseDirectory, mode + ".receipt")

let private runnerFor (entry: string) (receipt: string) (mode: string) (radix: int) (deadline: TimeSpan) =
    bounded outerWatchdog (ResolverKind.runStat statHost (statArguments mode receipt entry) radix entry deadline)

let private classified (label: string) (outcome: Result<ResolverKind.StatResult, ReleaseError>) (expected: string) : unit =
    match outcome with
    | Ok stat -> Expect.equal (ResolverKind.name stat.Kind) expected (label + " carries its own type bits")
    | Error error -> failtestf "%s was refused: %s" label (ReleaseError.message error)

let private refused (label: string) (outcome: Result<ResolverKind.StatResult, ReleaseError>) (entry: string) : unit =
    match outcome with
    | Ok stat -> failtestf "%s classified as a %s" label (ResolverKind.name stat.Kind)
    | Error(FileFailure (operation, reported, detail)) ->
        Expect.equal operation "kind" (label + " is refused as a classification decision")
        Expect.equal (Path.GetFullPath reported) (Path.GetFullPath entry) (label + " names the hosted entry")
        Expect.isFalse (String.IsNullOrWhiteSpace detail) (label + " carries a reason")
    | Error error -> failtestf "expected a classification refusal for %s, got %s" label (ReleaseError.message error)

// A bounded reader that raises out of the runner is the defect the flood modes exist to expose, so an escape is reported as such rather than as an unrelated error.
let private floodRefusal (mode: string) (work: Async<Result<ResolverKind.StatResult, ReleaseError>>) : Async<string> =
    async {
        try
            match! work with
            | Ok stat -> return failtestf "the %s child classified as a %s" mode (ResolverKind.name stat.Kind)
            | Error(FileFailure ("kind", _, detail)) -> return detail
            | Error error -> return failtestf "expected a classification refusal for %s, got %s" mode (ReleaseError.message error)
        with error ->
            return
                failtestf
                    "the %s child made the runner raise %s (%s) instead of returning a typed classification failure"
                    mode
                    (error.GetType().Name)
                    error.Message
    }

let private recordTests =
    testList
        "stat records"
        [ testCase "every type-bit record classifies as its public kind in both stat formats"
          <| fun () ->
              let records =
                  [ hex, "81a4", regularName
                    hex, "41ed", directoryName
                    hex, "a1ff", linkName
                    hex, "11a4", specialName
                    hex, "c1a4", specialName
                    hex, "21b6", specialName
                    hex, "81A4", regularName
                    octal, "100644", regularName
                    octal, "40755", directoryName
                    octal, "120777", linkName
                    octal, "10644", specialName
                    octal, "140644", specialName
                    octal, "20666", specialName
                    octal, "170777", specialName ]

              for radix, text, expected in records do
                  let label = sprintf "radix %d record '%s'" radix text

                  match ResolverKind.parseMode radix text with
                  | Error detail -> failtestf "%s was refused as %s" label detail
                  | Ok mode ->
                      Expect.equal
                          (ResolverKind.name (ResolverKind.classifyMode mode))
                          expected
                          (label + " keeps the type bits its format reports")

          testCase "an octal record is never read as the same digits read in decimal"
          <| fun () ->
              let records =
                  [ "100644", 33188UL, regularName
                    "40755", 16877UL, directoryName
                    "120777", 41471UL, linkName ]

              for text, octalValue, expected in records do
                  match ResolverKind.parseMode octal text with
                  | Error detail -> failtestf "the octal record '%s' was refused as %s" text detail
                  | Ok mode ->
                      Expect.equal mode octalValue (sprintf "'%s' is read as octal, not as its decimal spelling" text)
                      // The decimal spelling of '100644' also masks to 0x8000, so only the parsed value can distinguish the two readings.
                      Expect.notEqual
                          octalValue
                          (uint64 (int text))
                          (sprintf "the decimal spelling of '%s' is a different mode value" text)
                      Expect.equal
                          (ResolverKind.name (ResolverKind.classifyMode mode))
                          expected
                          (sprintf "'%s' classifies as a %s" text expected)

          testCase "every stat type-bit value maps to exactly one public kind"
          <| fun () ->
              for bits in 0 .. 15 do
                  let expected =
                      if bits = 8 then
                          regularName
                      elif bits = 4 then
                          directoryName
                      elif bits = 10 then
                          linkName
                      else
                          specialName

                  Expect.equal
                      (ResolverKind.name (ResolverKind.classifyMode (uint64 bits <<< 12)))
                      expected
                      (sprintf "type bits %x classify without reading the permission bits" bits)

          testCase "a trailing platform line ending is accepted and an embedded one is not"
          <| fun () ->
              Expect.equal (ResolverKind.parseMode hex "81a4\n") (Ok 0x81A4UL) "a unix line ending is stripped"
              Expect.equal (ResolverKind.parseMode hex "81a4\r\n") (Ok 0x81A4UL) "a windows line ending is stripped"
              Expect.equal (ResolverKind.parseMode octal "40755\n") (Ok 16877UL) "an octal record keeps its value across the line ending"

              match ResolverKind.parseMode hex "81a4\n81a4\n" with
              | Ok mode -> failtestf "a second record was folded into the first as %d" mode
              | Error _ -> ()

          testCase "malformed, signed, prefixed, padded, empty, extra, and overflowing records are refused"
          <| fun () ->
              let refusedRecords =
                  [ hex, "81a4 12 345"
                    hex, "-81a4"
                    hex, "+81a4"
                    hex, "0x81a4"
                    hex, " 81a4"
                    hex, "81a4 "
                    hex, ""
                    hex, "   "
                    hex, "g1a4"
                    hex, "81a4\n81a4"
                    hex, "81a4\r81a4"
                    hex, String('f', 24)
                    octal, "8"
                    octal, "9"
                    octal, "10o644"
                    octal, "100644\n100644"
                    octal, String('7', 22)
                    10, "40755"
                    0, "81a4"
                    -1, "81a4" ]

              for radix, text in refusedRecords do
                  match ResolverKind.parseMode radix text with
                  | Ok mode -> failtestf "radix %d record '%s' parsed as %d instead of being refused" radix text mode
                  | Error detail ->
                      Expect.isFalse
                          (String.IsNullOrWhiteSpace detail)
                          (sprintf "radix %d record '%s' is refused with a reason rather than a bare failure" radix text)

              match ResolverKind.parseMode hex null with
              | Ok _ -> failtest "a null record was accepted"
              | Error _ -> () ]

let private runnerTests =
    testList
        "stat hex runner"
        [ testCaseAsync "the real bounded runner classifies every valid hex record from a controlled child"
          <| async {
              let records =
                  [ "hex-regular", regularName
                    "hex-directory", directoryName
                    "hex-link", linkName
                    "hex-fifo", specialName
                    "hex-socket", specialName
                    "hex-character", specialName ]

              do!
                  scopedAsync
                      "stat-hex"
                      (fun base' ->
                          async {
                              for mode, expected in records do
                                  let entry, receipt = hosted base' mode
                                  let! outcome = runnerFor entry receipt mode hex childDeadline
                                  classified ("the child record " + mode) outcome expected

                                  match outcome with
                                  | Ok stat ->
                                      Expect.equal
                                          stat.Arguments
                                          (statArguments mode receipt entry)
                                          "the runner reports exactly the arguments it forwarded"

                                      Expect.equal
                                          (File.ReadAllText(receipt).Split('\u001f') |> Array.toList)
                                          (expectedChild mode receipt entry)
                                          "the child received every argument, including the hosted file path"
                                  | Error _ -> ()
                          })
          }

          testCaseAsync "the real bounded runner classifies every valid octal record from a controlled child"
          <| async {
              let records = [ "octal-regular", regularName; "octal-directory", directoryName; "octal-link", linkName ]

              do!
                  scopedAsync
                      "stat-octal"
                      (fun base' ->
                          async {
                              for mode, expected in records do
                                  let entry, receipt = hosted base' mode
                                  let! outcome = runnerFor entry receipt mode octal childDeadline
                                  classified ("the child record " + mode) outcome expected

                                  match outcome with
                                  | Ok _ ->
                                      Expect.equal
                                          (File.ReadAllText(receipt).Split('\u001f') |> Array.toList)
                                          (expectedChild mode receipt entry)
                                          "the child received every argument, including the hosted file path"
                                  | Error _ -> ()
                          })
          }

          testCaseAsync "malformed, signed, prefixed, padded, empty, extra, and overflowing child output is refused"
          <| async {
              let records =
                  [ "malformed", hex
                    "signed", hex
                    "prefixed", hex
                    "padded", hex
                    "empty", hex
                    "extra-record", hex
                    "overflow-hex", hex
                    "overflow-octal", octal ]

              do!
                  scopedAsync
                      "stat-malformed"
                      (fun base' ->
                          async {
                              for mode, radix in records do
                                  let entry, receipt = hosted base' mode
                                  let! outcome = runnerFor entry receipt mode radix childDeadline
                                  refused ("the child record " + mode) outcome entry
                                  Expect.isTrue (File.Exists receipt) ("the " + mode + " refusal came from a child that actually ran")
                          })
          }

          testCaseAsync "a nonzero child exit is reported with its own code instead of a classification"
          <| async {
              do!
                  scopedAsync
                      "stat-exit"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "nonzero"
                              let! outcome = runnerFor entry receipt "nonzero" hex childDeadline

                              Expect.isTrue (File.Exists receipt) "the controlled child ran"

                              match outcome with
                              | Ok stat -> return failtestf "a nonzero child exit classified as a %s" (ResolverKind.name stat.Kind)
                              | Error(FileFailure (operation, _, detail)) ->
                                  Expect.equal operation "kind" "the refusal is the classification decision"
                                  Expect.stringContains detail "exited with code 3" "the refusal reports the child's own exit code"
                              | Error error -> return failtestf "expected a classification refusal, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "standard output beyond the bounded limit is refused instead of buffered"
          <| async {
              do!
                  scopedAsync
                      "stat-stdout"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "stdout-flood"

                              let! detail =
                                  floodRefusal
                                      "stdout-flood"
                                      (runnerFor entry receipt "stdout-flood" hex childDeadline)

                              Expect.stringContains detail "bounded limit" "the refusal names the bounded reader rather than the mode text"
                              Expect.isTrue (File.Exists receipt) "the flooding child ran before its output was refused"
                          })
          }

          testCaseAsync "standard error beyond the bounded limit is refused instead of buffered"
          <| async {
              do!
                  scopedAsync
                      "stat-stderr"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "stderr-flood"

                              let! detail =
                                  floodRefusal
                                      "stderr-flood"
                                      (runnerFor entry receipt "stderr-flood" hex childDeadline)

                              Expect.stringContains detail "bounded limit" "the refusal names the bounded reader rather than the mode text"
                              Expect.isTrue (File.Exists receipt) "the flooding child ran before its output was refused"
                          })
          }

          testCaseAsync "a stalled child is stopped at the runner deadline and is reaped"
          <| async {
              do!
                  scopedAsync
                      "stat-stall"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "stall"
                              let watch = Stopwatch.StartNew()

                              let! outcome =
                                  bounded
                                      outerWatchdog
                                      (ResolverKind.runStat
                                          statHost
                                          (statArguments "stall" receipt entry)
                                          hex
                                          entry
                                          (TimeSpan.FromSeconds 2.))

                              watch.Stop()

                              match outcome with
                              | Ok stat -> return failtestf "a stalled child classified as a %s" (ResolverKind.name stat.Kind)
                              | Error(FileFailure (operation, _, detail)) ->
                                  Expect.equal operation "kind" "the refusal is the classification decision"
                                  Expect.stringContains detail "timed out" "the refusal names the runner deadline"
                              | Error error -> return failtestf "expected a classification refusal, got %s" (ReleaseError.message error)

                              Expect.isTrue
                                  (watch.Elapsed < TimeSpan.FromSeconds 30.)
                                  "the runner settled at its own deadline instead of waiting for the child"

                              Expect.isTrue (File.Exists (receipt + ".pid")) "the stalled child reported its own process id"

                              let! alive = stillAlive (int (File.ReadAllText(receipt + ".pid")))
                              Expect.isFalse alive "the stalled child is reaped instead of being left running"
                          })
          }

          testCaseAsync "an absent program is refused by this case's own path instead of the fixed platform utility"
          <| async {
              do!
                  scopedAsync
                      "stat-absent"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "hex-regular"
                              let missing = Path.Combine(base', "no-such-stat")

                              Expect.isFalse (File.Exists missing) "the case owns a program path that cannot exist"

                              let! outcome =
                                  bounded
                                      outerWatchdog
                                      (ResolverKind.runStat missing (statArguments "hex-regular" receipt entry) hex entry childDeadline)

                              Expect.isFalse (File.Exists receipt) "no child ran against a program that does not exist"

                              match outcome with
                              | Ok stat -> return failtestf "an absent program classified as a %s" (ResolverKind.name stat.Kind)
                              | Error(FileFailure (operation, reported, detail)) ->
                                  Expect.equal operation "kind" "the refusal is the classification decision"

                                  Expect.equal
                                      (Path.GetFullPath reported)
                                      (Path.GetFullPath entry)
                                      "the refusal names the hosted entry rather than the program path"

                                  Expect.stringContains detail "stat utility" "the refusal names the classification utility it could not start"
                              | Error error -> return failtestf "expected a classification refusal, got %s" (ReleaseError.message error)
                          })
          }

          testCaseAsync "a nonpositive budget is refused before the program is ever started"
          <| async {
              do!
                  scopedAsync
                      "stat-budget"
                      (fun base' ->
                          async {
                              let entry, receipt = hosted base' "hex-regular"
                              let missing = Path.Combine(base', "no-such-stat")
                              let arguments = statArguments "hex-regular" receipt entry
                              let budgets = [ TimeSpan.Zero, "a zero budget"; TimeSpan.FromSeconds -1., "a negative budget" ]

                              for budget, why in budgets do
                                  // The program cannot exist, so a budget refusal rather than a start refusal proves the order the runner claims.
                                  let! outcome = bounded outerWatchdog (ResolverKind.runStat missing arguments hex entry budget)

                                  match outcome with
                                  | Ok stat -> return failtestf "%s still classified a %s" why (ResolverKind.name stat.Kind)
                                  | Error(FileFailure (operation, reported, detail)) ->
                                      Expect.equal operation "kind" (why + " is refused as a classification decision")
                                      Expect.equal (Path.GetFullPath reported) (Path.GetFullPath entry) (why + " names the hosted entry")

                                      Expect.stringContains
                                          detail
                                          "budget must be positive"
                                          (why + " is refused by the budget before any program is started")
                                  | Error error -> return failtestf "expected a budget refusal for %s, got %s" why (ReleaseError.message error)

                                  Expect.isFalse (File.Exists receipt) (why + " started no child")
                          })
          } ]

let tests = testList "stat" [ recordTests; runnerTests ]