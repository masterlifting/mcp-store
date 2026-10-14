# role: entrypoint/command
# Strict, read-only FCS typecheck of the release-path scripts that no compiled project build covers; targets derive from the enrollment roster, and the compiled modules and compiled regression project are typed by their own builds.
# The generated probe (with its SDK #r reference) is written under RUNNER_TEMP, outside the repository, so the in-repo analyzer's #r constraint is never violated; targets are read and type-checked as text only and are never evaluated.

[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$SdkVersion = "",
    [string[]]$Targets = @()
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Roster.ps1")

# Targets derive from the enrollment roster so no participation list is restated here; an explicit -Targets run still wins.
function Get-RosterTargets {
    $derived = New-Object System.Collections.Generic.List[string]

    foreach ($entry in (Get-ReleaseRoster -Root $Root)) {
        # Producer entrypoints sit beside the roster's canonical config; nothing else is assumed about them.
        $configDirectory = [System.IO.Path]::GetDirectoryName($entry.configPath).Replace('\', '/')
        $componentRoot = [System.IO.Path]::GetDirectoryName($configDirectory).Replace('\', '/')
        $derived.Add("$configDirectory/Build.fsx")
        $derived.Add("$configDirectory/Pins.fsx")
        $derived.Add("$componentRoot/tests/DistributionTests.fsx")
    }

    return $derived
}

if (-not $Targets -or $Targets.Count -eq 0) {
    # The producer provenance contract is roster-independent and still needs the strict gate; the retired pre-SDK
    # `Bootstrap.fsx` is no longer a target because the canonical projection is plain PowerShell over the policy JSON.
    $fixed = @("dotnet/tests/ProvenanceTests.fsx")
    $Targets = @(@($fixed) + @(Get-RosterTargets) | Sort-Object -Unique)
}

if (-not $Targets -or $Targets.Count -eq 0) {
    throw "no typecheck targets supplied; a zero-target run is not a pass"
}

# Resolve the SDK root/version from the installed SDKs; no SDK version table or dev path is embedded.
function Get-Sdk([string]$version) {
    $lines = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks failed ($LASTEXITCODE)" }

    $installed = foreach ($line in $lines) {
        if ($line -match '^(?<ver>\S+)\s+\[(?<root>.+)\]\s*$') {
            [pscustomobject]@{ Version = $Matches['ver']; Root = $Matches['root'] }
        }
    }

    if ($version) {
        $match = $installed | Where-Object { $_.Version -eq $version } | Select-Object -First 1
        if (-not $match) { throw "configured SDK '$version' is not installed (dotnet --list-sdks)" }
        return $match
    }

    $latest = $installed |
        Sort-Object { [version](($_.Version -split '-')[0]) } |
        Select-Object -Last 1
    if (-not $latest) { throw "no installed .NET SDK found" }
    return $latest
}

$sdk = Get-Sdk $SdkVersion
$sdkDirectory = Join-Path $sdk.Root $sdk.Version
$fsiDll = Join-Path $sdkDirectory "FSharp\fsi.dll"
$fcsDll = Join-Path $sdkDirectory "FSharp\FSharp.Compiler.Service.dll"

if (-not (Test-Path -LiteralPath $fsiDll)) { throw "fsi.dll not found for SDK '$($sdk.Version)': $fsiDll" }
if (-not (Test-Path -LiteralPath $fcsDll)) { throw "FSharp.Compiler.Service.dll not found: $fcsDll" }

$workDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } elseif ($env:TEMP) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$probePath = Join-Path $workDirectory "ghi24-entrypoint-typecheck.fsx"

# The #r reference lives only in the generated RUNNER_TEMP probe; the repo tree carries no #r.
$probeHeader = '#r "' + $fcsDll + '"'
$probeBody = @'
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

let checker = FSharpChecker.Create()
let root = System.Environment.GetEnvironmentVariable "GHI24_TC_ROOT"
let targets =
    (System.Environment.GetEnvironmentVariable "GHI24_TC_TARGETS").Split(';')
    |> Array.filter (fun value -> value.Length > 0)

if targets.Length = 0 then
    printfn "typecheck: no targets supplied"
    exit 1

let mutable errors = 0
let mutable missing = 0

for target in targets do
    let path = if Path.IsPathRooted target then target else Path.Combine(root, target)

    if not (File.Exists path) then
        printfn "MISSING %s" path
        missing <- missing + 1
    else
        let source = SourceText.ofString (File.ReadAllText path)

        let opts, _ =
            checker.GetProjectOptionsFromScript(
                path,
                source,
                ?useSdkRefs = Some true,
                ?useFsiAuxLib = Some true,
                ?assumeDotNetFramework = Some false)
            |> Async.RunSynchronously

        let result = checker.ParseAndCheckProject(opts) |> Async.RunSynchronously

        let found =
            result.Diagnostics
            |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

        printfn "%s referencedSources=%d errors=%d" target opts.SourceFiles.Length found.Length

        for diagnostic in found do
            printfn "  E%d L%dC%d %s"
                diagnostic.ErrorNumber
                (int diagnostic.StartLine + 1)
                (int diagnostic.StartColumn + 1)
                (diagnostic.Message.Replace("\n", " "))

        errors <- errors + found.Length

printfn "typecheck total_errors=%d missing_targets=%d targets=%d" errors missing targets.Length

if errors > 0 || missing > 0 then exit 1 else exit 0
'@

Set-Content -LiteralPath $probePath -Value ($probeHeader + "`n" + $probeBody) -Encoding utf8

$env:GHI24_TC_ROOT = (Resolve-Path -LiteralPath $Root).Path
$env:GHI24_TC_TARGETS = ($Targets -join ';')

Write-Host "typecheck sdk=$($sdk.Version) fcs=$fcsDll probe=$probePath"

# Run the exact canonical SDK's FSI so the probe host and its #r reference are the same FCS.
& dotnet $fsiDll --nologo $probePath
exit $LASTEXITCODE
