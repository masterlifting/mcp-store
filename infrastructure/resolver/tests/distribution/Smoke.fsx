// role: test entrypoint
// Packaged Resolver smoke: launches the real extracted archive and exercises the compiled
// verifier against the generated pin. Only local archive bytes and a disposable extraction
// are involved: no network, credentials, injected transport, or host runtime state.

#load "../../../Testing.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open DistributionTestHelper
open SharedFailure

let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))
let resolverRoot = Path.Combine(repoRoot, "infrastructure", "resolver")
let distributionRoot = Path.Combine(resolverRoot, "dist")
let configPath = Path.Combine(resolverRoot, "release", "Config.json")

let readConfig () =
    use document = JsonDocument.Parse(File.ReadAllText configPath)
    let root = document.RootElement

    let read (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> failwithf "resolver release policy '%s' is missing the string field '%s'" configPath name

    read "componentId", read "version", read "entryDll"

let componentId, version, entryDll = readConfig ()
// The scripts load no contract module, so the pin-derived name is asserted against this value after the pin parses.
let archiveName = $"v{version}.zip"
let archivePath = Path.Combine(distributionRoot, archiveName)
let pinPath = Path.Combine(distributionRoot, "consumer-pins.json")

assertTrue "the packaged archive exists" (File.Exists archivePath)
assertTrue "the generated consumer pin exists" (File.Exists pinPath)

let archiveBytes = File.ReadAllBytes archivePath
let pinText = File.ReadAllText pinPath

let runCaptured (workingDirectory: string) (executable: string) (arguments: string list) : Async<int * string * string> =
    async {
        let info = ProcessStartInfo(executable)
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        arguments |> List.iter info.ArgumentList.Add

        use child = new Process(StartInfo = info)
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

        if not (child.Start()) then
            failwithf "%s could not be started" executable

        // Concurrent drains prevent a full pipe from blocking the child.
        let stdoutTask = child.StandardOutput.ReadToEndAsync()
        let stderrTask = child.StandardError.ReadToEndAsync()
        let drains = Task.WhenAll((stdoutTask :> Task), (stderrTask :> Task))
        let complete = Task.WhenAll(child.WaitForExitAsync timeout.Token, drains)
        let deadline = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)

        // Cleanup after a failed kill is bounded so neither a surviving process nor an
        // open pipe can turn the failure path into an unbounded wait.
        let bounded (work: Task) =
            async {
                try
                    let! finished = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds 5.0)) |> awaitOperational
                    if obj.ReferenceEquals(finished, work) then do! awaitComplete work
                with _ -> ()
            }

        try
            let! finished = Task.WhenAny(complete, deadline) |> awaitOperational

            if obj.ReferenceEquals(finished, deadline) then
                raise (OperationCanceledException timeout.Token)

            do! awaitComplete complete
        with :? OperationCanceledException ->
            try
                if not child.HasExited then child.Kill(true)
            with
            | :? InvalidOperationException -> ()
            | :? System.ComponentModel.Win32Exception -> ()
            | :? NotSupportedException -> ()

            do! bounded (child.WaitForExitAsync())
            do! bounded drains
            failwithf "%s %s did not exit within 60 seconds" executable (String.concat " " arguments)

        let! stdout = awaitOperational stdoutTask
        let! stderr = awaitOperational stderrTask
        return child.ExitCode, stdout, stderr
    }

let unionFields (value: obj) =
    let info, fields = FSharpValue.GetUnionFields(value, value.GetType())
    info.Name, fields

let expectCase (label: string) (expected: string) (value: obj) =
    let name, fields = unionFields value

    if name <> expected then
        failwithf "%s: expected union case %s, got %s" label expected name

    fields

async {
    let extractionRoot = Path.Combine(Path.GetTempPath(), "resolver-smoke-" + Guid.NewGuid().ToString("N"))

    try
        Directory.CreateDirectory extractionRoot |> ignore
        ZipFile.ExtractToDirectory(archivePath, extractionRoot)
        let entryPath = Path.Combine(extractionRoot, entryDll)
        assertTrue "the extracted archive contains the declared entrypoint" (File.Exists entryPath)

        let! exitCode, stdout, stderr = runCaptured extractionRoot "dotnet" [ entryPath ]
        let expectedUsage = "the command line is invalid: no subcommand was supplied; expected 'resolve' or 'run'"
        assertEqual "the packaged resolver refuses an empty command line with exit 2" 2 exitCode
        assertEqual "the packaged resolver writes nothing to stdout for an empty command line" "" stdout

        assertEqual
            "the packaged resolver writes the exact no-args usage line to stderr"
            (expectedUsage + Environment.NewLine)
            stderr

        // The script analyzer forbids external assembly references, so the compiled verifier is loaded from the extracted bytes by reflection.
        let assembly = Assembly.Load(File.ReadAllBytes entryPath)

        let parsePin =
            assembly.GetType("DistributionPin", true).GetMethod("parse", BindingFlags.Public ||| BindingFlags.Static)

        let pinFields = expectCase "the generated pin parses" "Ok" (parsePin.Invoke(null, [| box pinText |]))
        let pin = pinFields.[0]
        let pinArchiveSha256 = pin.GetType().GetProperty("ArchiveSha256").GetValue(pin) :?> string

        assertEqual "the pin derives the packaged archive name" archiveName (pin.GetType().GetProperty("ArchiveName").GetValue(pin) :?> string)

        assertEqual
            "the pin derives the canonical asset URI"
            $"https://github.com/masterlifting/mcp-store/releases/download/{componentId}/{archiveName}"
            (pin.GetType().GetProperty("AssetUri").GetValue(pin) :?> string)

        let actualArchiveSha256 =
            SHA256.HashData archiveBytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

        assertEqual "the generated pin matches the archive bytes" pinArchiveSha256 actualArchiveSha256

        let verify =
            assembly.GetType("ResolverPayload", true).GetMethod("verify", BindingFlags.Public ||| BindingFlags.Static)

        // Reaching the launch-kind refusal proves the archive, manifest, identity, membership, and member digests authenticated.
        let honestError =
            expectCase
                "the honest archive does not verify as an MCP launch target"
                "Error"
                (verify.Invoke(null, [| pin; box archiveBytes |]))

        let honestFields =
            expectCase
                "the honest archive reaches the authenticated launch-kind refusal"
                "IdentityMismatch"
                honestError.[0]

        assertEqual "the launch-kind refusal names its field" "launch.kind" (honestFields.[0] :?> string)
        assertEqual "the launch-kind refusal expects dotnet-exec" "dotnet-exec" (honestFields.[1] :?> string)
        assertEqual "the launch-kind refusal reports dotnet-cli" "dotnet-cli" (honestFields.[2] :?> string)

        let altered = Array.copy archiveBytes
        altered.[0] <- altered.[0] ^^^ 0xFFuy
        let alteredSha256 = SHA256.HashData altered |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

        // The archive digest is checked first, so the changed copy must fail before any decompression or parsing.
        let alteredError =
            expectCase
                "the in-memory altered archive does not verify"
                "Error"
                (verify.Invoke(null, [| pin; box altered |]))

        let alteredFields =
            expectCase
                "the altered archive fails at the archive digest check"
                "IdentityMismatch"
                alteredError.[0]

        assertEqual "the altered-archive refusal names archiveSha256" "archiveSha256" (alteredFields.[0] :?> string)
        assertEqual "the altered-archive refusal keeps the pin digest as expected" pinArchiveSha256 (alteredFields.[1] :?> string)
        assertEqual "the altered-archive refusal reports the changed digest" alteredSha256 (alteredFields.[2] :?> string)
        assertTrue "the altered archive has a different digest" (alteredSha256 <> pinArchiveSha256)

        printfn
            "OK resolver packaged smoke: %s (%s, sha256 %s) launched with exit 2, refused the CLI-only launch.kind, and rejected an altered archive at archiveSha256"
            componentId
            archiveName
            actualArchiveSha256
    finally
        if Directory.Exists extractionRoot then
            try
                Directory.Delete(extractionRoot, true)
            with error ->
                eprintfn "warning: could not remove disposable extraction '%s': %s" extractionRoot error.Message
}
|> Async.RunSynchronously
