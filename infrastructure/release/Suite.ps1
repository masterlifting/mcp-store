# role: entrypoint/command
# Runs the complete non-publishing release regression gate: the compiled Expecto release suite plus the retained producer provenance entrypoint.
# The compiled suite is a console Expecto entrypoint, so `dotnet test` reports no counts for it; the native Expecto exit code and the executed-case count are the gate.
# The historical integration is a required case, so a missing pinned historical fixture fails the gate closed instead of excluding or rewriting any case.

[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Configuration = "Release",
    [string]$HistoricalRoot = $env:MCP_RELEASE_HISTORICAL_ROOT
)

$ErrorActionPreference = "Stop"

# Read every child exit code explicitly so a native-command error preference cannot abort the loop early.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# Capture the workflow output target and its bytes so a child cannot redirect or append them.
function Get-OutputState([string]$Name) {
    $target = [System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($target)) { return "<unset>" }
    if (-not (Test-Path -LiteralPath $target)) { return "target=$target;absent" }
    return "target=$target;content=$(Get-Content -LiteralPath $target -Raw -ErrorAction SilentlyContinue)"
}

$suiteDll = Join-Path $Root "infrastructure\release\tests\bin\$Configuration\net11.0\Release.Tests.dll"
if (-not (Test-Path -LiteralPath $suiteDll)) {
    throw "the compiled release regression project is not built: $suiteDll (build infrastructure/release/tests/Tests.fsproj -c $Configuration first)"
}

# Producer scripts run by this gate; the packaging tests need a clean tree, so the `prepare` tests phase runs them per component instead.
. (Join-Path $PSScriptRoot "Roster.ps1")
$roster = Get-ReleaseRoster -Root $Root

# The provenance contract is producer-owned and roster-independent; packaging entrypoints follow each participant's config directory.
$scriptEntrypoints = @("dotnet/tests/ProvenanceTests.fsx")
$orchestratedEntrypoints = @($roster | ForEach-Object {
    $configDirectory = [System.IO.Path]::GetDirectoryName($_.configPath).Replace('\', '/')
    $componentRoot = [System.IO.Path]::GetDirectoryName($configDirectory).Replace('\', '/')
    "$componentRoot/tests/DistributionTests.fsx"
})

# The `prepare` tests phase runs this package-dependent suite against the clean built distribution, so the gate asserts it exists without executing it.
$packagedEntrypoints = @("workflow/tests/PackagedRuntimeCoordinationTests.fsx")

foreach ($relative in $scriptEntrypoints + $orchestratedEntrypoints + $packagedEntrypoints) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $relative))) {
        throw "release-path script entrypoint is missing: $relative"
    }
}

# The pinned fixture identity mirrors the compiled Historical case, which owns the fixture names and re-verifies each archive's SHA-256 and size itself.

# The complete suite cannot run without the historical archives, and no case may be excluded to work around that.
if ([string]::IsNullOrWhiteSpace($HistoricalRoot)) {
    Write-Host "::error::MCP_RELEASE_HISTORICAL_ROOT is not set; the complete compiled release suite requires the pinned historical fixtures and this gate does not exclude cases"
    exit 1
}
if (-not (Test-Path -LiteralPath $HistoricalRoot -PathType Container)) {
    Write-Host "::error::MCP_RELEASE_HISTORICAL_ROOT does not name a directory: $HistoricalRoot"
    exit 1
}

$historicalRootResolved = (Resolve-Path -LiteralPath $HistoricalRoot).Path

$outputBefore = @{}
foreach ($name in @("GITHUB_ENV", "GITHUB_OUTPUT")) {
    $outputBefore[$name] = Get-OutputState $name
}

$ansi = [regex]"\x1b\[[0-9;]*m"

function Read-Lines([object[]]$Output) {
    return @($Output | ForEach-Object { $ansi.Replace("$_", "").TrimEnd() })
}

function Get-ExpectoCount([string[]]$Lines) {
    $match = $Lines | Select-String -Pattern '(\d+) tests run' | Select-Object -Last 1
    if (-not $match) { return -1 }
    return [int]([regex]::Match($match.Line, '(\d+) tests run').Groups[1].Value)
}

# The compiled suite reports its own case inventory, so neither a removed case nor an unrun case can pass unnoticed.
$inventoryLines = Read-Lines (& dotnet $suiteDll --list-tests 2>&1)
$inventory = @($inventoryLines | Where-Object { $_ -match '^compiled release regressions\.' })
if ($inventory.Count -eq 0) {
    throw "could not enumerate the compiled release regression suite in $suiteDll"
}

$historicalCases = @($inventory | Where-Object { $_ -match '\.Historical ' })
if ($historicalCases.Count -eq 0) {
    throw "the compiled suite does not report its historical integration case; refusing to run an unverified inventory"
}

$failed = New-Object System.Collections.Generic.List[string]
Push-Location $Root
try {
    $env:MCP_RELEASE_HISTORICAL_ROOT = $historicalRootResolved

    Write-Host "::group::Release.Tests (complete compiled suite)"
    $lines = Read-Lines (& dotnet $suiteDll --sequenced --no-spinner 2>&1)
    $code = $LASTEXITCODE
    foreach ($line in $lines) { Write-Host $line }
    Write-Host "::endgroup::"

    $count = Get-ExpectoCount $lines
    if ($count -ne $inventory.Count) {
        $observed = if ($count -lt 0) { "no Expecto summary line" } else { "$count" }
        $failed.Add("expected $($inventory.Count) compiled case(s) but observed $observed")
    } elseif ($code -ne 0) {
        $failed.Add("compiled release suite (exit $code)")
    }

    foreach ($relative in $scriptEntrypoints) {
        Write-Host "::group::release regression $relative"
        & dotnet fsi --nologo (Join-Path $Root $relative)
        $scriptCode = $LASTEXITCODE
        Write-Host "::endgroup::"
        if ($scriptCode -ne 0) { $failed.Add("$relative (exit $scriptCode)") }
    }
} finally {
    Pop-Location
}

foreach ($name in @("GITHUB_ENV", "GITHUB_OUTPUT")) {
    if ((Get-OutputState $name) -ne $outputBefore[$name]) {
        $failed.Add("$name was redirected or written during the suite")
    }
}

if ($failed.Count -gt 0) {
    Write-Host "::error::release regression gate failed: $($failed -join '; ')"
    exit 1
}

Write-Host "release regression gate passed: $($inventory.Count) compiled case(s) (including $($historicalCases.Count) historical) plus $($scriptEntrypoints.Count) producer script(s)"
exit 0
