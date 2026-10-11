# role: entrypoint/command
# Projects canonical SDK/runtime/platform before any SDK install and verifies the executing toolchain after install.
# Canonical `*/release/Config.fsx` stays the only value source; no table is restated in YAML or global.json.
# The release transaction, comparison, and upload live in the F# modules, not here.

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

# Exact-one-literal match; Bootstrap.fsx cross-checks this projection against the evaluated config.
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
        # archiveName/assetUri are F# interpolations; Bootstrap.fsx emits them after install.
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

# Export the projection so Bootstrap.fsx can assert projection == evaluated config == executing toolchain.
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
