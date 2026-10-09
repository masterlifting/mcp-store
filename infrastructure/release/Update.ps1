# role: entrypoint/command
# Minimal toolchain bootstrap for the root release workflow.
#
# The script has two modes:
#
#   -Project  Reads the canonical SDK/runtime/platform literals directly from
#             each producer's `release/Config.fsx` with a strict, deterministic
#             text projection (no F# evaluation, no installed SDK required) and
#             publishes them through `$GITHUB_OUTPUT`. The workflow uses the
#             projected SDK to install the exact toolchain before any F# code
#             runs; the canonical Config.fsx remains the only source of truth,
#             so no SDK/version table is duplicated in workflow YAML or a root
#             global.json.
#
#   -Verify   Runs the F# `Bootstrap.fsx`, which re-reads the same canonical
#             Config.fsx through an isolated `dotnet fsi` child and checks the
#             executing SDK/runtime/platform against the canonical values and
#             against the projection recomputed here. The verified summary is
#             written to `$GITHUB_ENV`.
#
# The script never implements the release transaction, comparison, or upload;
# those live in the F# modules under infrastructure/release/ and are exercised
# by the per-component Release.fsx entrypoints.

[CmdletBinding(DefaultParameterSetName = "Project")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Verify")]
    [ValidateSet("dotnet", "workflow")]
    [string]$Component,

    [Parameter(Mandatory = $true, ParameterSetName = "Project")]
    [switch]$Project,

    [Parameter(Mandatory = $true, ParameterSetName = "Verify")]
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

$repoRoot = if ($PSScriptRoot) { Resolve-Path (Join-Path $PSScriptRoot "..\..") } else { (Get-Location).Path }
$bootstrapScript = Join-Path $repoRoot "infrastructure\release\Bootstrap.fsx"
$producers = @(
    @{ Key = "dotnet"; Config = Join-Path $repoRoot "dotnet\release\Config.fsx" },
    @{ Key = "workflow"; Config = Join-Path $repoRoot "workflow\release\Config.fsx" }
)

# Strict text projection: exactly one `let <name> = "<literal>"` per file.
# The projection never evaluates F# and never maintains a second value table;
# Bootstrap.fsx cross-checks this projection against the evaluated config.
function Read-CanonicalLiteral {
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        throw "canonical config not found: $ConfigPath"
    }

    $pattern = '^\s*let\s+' + [regex]::Escape($Name) + '\s*=\s*"([^"]*)"\s*$'
    $hits = @(Select-String -LiteralPath $ConfigPath -Pattern $pattern)

    if ($hits.Count -ne 1) {
        throw "expected exactly one literal '$Name' in $ConfigPath; found $($hits.Count)"
    }

    $capture = $hits[0].Matches[0].Groups[1]
    if (-not $capture.Success -or [string]::IsNullOrWhiteSpace($capture.Value)) {
        throw "could not read a non-empty '$Name' literal from $ConfigPath"
    }

    return $capture.Value
}

function Get-Projection {
    $result = @{}
    $columns = @("sdkVersion", "runtimeVersion", "platform")

    foreach ($producer in $producers) {
        $fields = @{}
        # Only plain string literals are projected here. archiveName/assetUri are
        # F# interpolations derived from version/componentId; they are emitted by
        # Bootstrap.fsx after install rather than re-derived as a second source.
        foreach ($name in @("componentId", "version", "entryDll") + $columns) {
            $fields[$name] = Read-CanonicalLiteral -ConfigPath $producer.Config -Name $name
        }

        if ($fields["componentId"] -ne $producer.Key) {
            throw "componentId '$($fields['componentId'])' in $($producer.Config) does not match producer key '$($producer.Key)'"
        }

        $result[$producer.Key] = $fields
    }

    foreach ($name in $columns) {
        $distinct = @($producers | ForEach-Object { $result[$_.Key][$name] } | Sort-Object -Unique)
        if ($distinct.Count -ne 1) {
            throw "producers disagree on canonical '$name': $($distinct -join ', ')"
        }
        $result[$name] = $distinct[0]
    }

    return $result
}

function Write-KeyValue {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Pairs,
        [string]$Target
    )

    foreach ($key in $Pairs.Keys) {
        if ($Target) {
            Add-Content -LiteralPath $Target -Value "$key=$($Pairs[$key])"
        }
        Write-Host "$key=$($Pairs[$key])"
    }
}

$projection = Get-Projection
$sdk = $projection["sdkVersion"]
$runtime = $projection["runtimeVersion"]
$platform = $projection["platform"]

$summary = [ordered]@{
    "RELEASE_SDK"     = $sdk
    "RELEASE_RUNTIME" = $runtime
    "RELEASE_PLATFORM" = $platform
}
foreach ($producer in $producers) {
    $fields = $projection[$producer.Key]
    $upper = $producer.Key.ToUpperInvariant()
    $summary["RELEASE_${upper}_VERSION"] = $fields["version"]
    $summary["RELEASE_${upper}_ENTRY"] = $fields["entryDll"]
}

if ($Project) {
    Write-Host "# projected canonical toolchain (no F# evaluation)"
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sdk=$sdk"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "runtime=$runtime"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "platform=$platform"
    } else {
        Write-Host "GITHUB_OUTPUT is not set; projecting to stdout only (local validation mode)"
    }
    Write-KeyValue -Pairs $summary
    exit 0
}

# -Verify: invoke the F# bootstrap with the projection exported so Bootstrap.fsx
# can assert projection == evaluated config == executing toolchain.
if (-not (Test-Path -LiteralPath $bootstrapScript)) {
    throw "bootstrap script not found: $bootstrapScript"
}

$env:RELEASE_EXPECTED_SDK = $sdk
$env:RELEASE_EXPECTED_RUNTIME = $runtime
$env:RELEASE_EXPECTED_PLATFORM = $platform

Write-Host "running toolchain verification: $bootstrapScript (component=$Component)"
$bootstrapOutput = & dotnet fsi $bootstrapScript 2>&1
$bootstrapExit = $LASTEXITCODE

if ($bootstrapExit -ne 0) {
    Write-Host "bootstrap failed with exit $bootstrapExit"
    Write-Host ($bootstrapOutput -join "`n")
    exit $bootstrapExit
}

Write-Host "bootstrap output:"
Write-Host ($bootstrapOutput -join "`n")

$envLines = @{}
foreach ($line in $bootstrapOutput) {
    $trimmed = "$line".Trim()
    if ($trimmed.StartsWith("#") -or [string]::IsNullOrWhiteSpace($trimmed)) {
        continue
    }
    $eq = $trimmed.IndexOf("=")
    if ($eq -le 0) { continue }
    $key = $trimmed.Substring(0, $eq)
    $value = $trimmed.Substring($eq + 1)
    $envLines[$key] = $value
}

if ($env:GITHUB_ENV) {
    Write-KeyValue -Pairs $envLines -Target $env:GITHUB_ENV
} else {
    Write-Host "GITHUB_ENV is not set; skipping environment write (local validation mode)"
}

Write-Host "toolchain verification complete for component=$Component"
exit 0
