# role: entrypoint/command
# Projects canonical SDK/runtime/platform before any SDK install and verifies the executing toolchain after install.
# `Participants.json` is the only enrollment authority: the projection, the component list and the workflow matrix derive from it, and `*/release/Config.fsx` stays the only value source.
# The shared SDK/runtime/platform equality check belongs to this bootstrap guard only; the transaction and runner never require one global toolchain identity.

[CmdletBinding(DefaultParameterSetName = "Project")]
param(
    [Parameter(ParameterSetName = "Verify")]
    [string]$Component = "",

    [Parameter(Mandatory = $true, ParameterSetName = "Project")]
    [switch]$Project,

    [Parameter(Mandatory = $true, ParameterSetName = "Verify")]
    [switch]$Verify,

    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Roster.ps1")

$rosterPath = Join-Path $Root "infrastructure\release\Participants.json"
$bootstrapScript = Join-Path $Root "infrastructure\release\Bootstrap.fsx"

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
    param([Parameter(Mandatory = $true)][object[]]$Roster)

    $result = @{}
    $columns = @("sdkVersion", "runtimeVersion", "platform")

    foreach ($entry in $Roster) {
        $configPath = Join-Path $Root $entry.configPath
        $fields = @{}
        # archiveName/assetUri are F# interpolations; Bootstrap.fsx emits them after install.
        foreach ($name in @("componentId", "version", "entryDll") + $columns) {
            $fields[$name] = Read-CanonicalLiteral -ConfigPath $configPath -Name $name
        }

        # A config whose identity contradicts the roster entry for the same path is drift, not policy.
        if ($fields["componentId"] -ne $entry.componentId) {
            throw "componentId '$($fields['componentId'])' in $($entry.configPath) does not match roster participant '$($entry.componentId)'"
        }

        $result[$entry.componentId] = $fields
    }

    foreach ($name in $columns) {
        $distinct = @($Roster | ForEach-Object { $result[$_.componentId][$name] } | Sort-Object -Unique)
        if ($distinct.Count -ne 1) {
            throw "participants disagree on canonical '$name': $($distinct -join ', ')"
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

$roster = Get-ReleaseRoster -Root $Root
$componentIds = @($roster | ForEach-Object { $_.componentId })
$projection = Get-Projection -Roster $roster
$sdk = $projection["sdkVersion"]
$runtime = $projection["runtimeVersion"]
$platform = $projection["platform"]

$summary = [ordered]@{
    "RELEASE_SDK"     = $sdk
    "RELEASE_RUNTIME" = $runtime
    "RELEASE_PLATFORM" = $platform
}
foreach ($componentId in $componentIds) {
    $fields = $projection[$componentId]
    $upper = $componentId.ToUpperInvariant()
    $summary["RELEASE_${upper}_VERSION"] = $fields["version"]
    $summary["RELEASE_${upper}_ENTRY"] = $fields["entryDll"]
}

if ($Project) {
    Write-Host "# projected canonical toolchain and roster participants (no F# evaluation)"
    # The matrix is projected as JSON so the workflow carries no participation list of its own.
    $componentsJson = ConvertTo-Json -Compress -InputObject $componentIds
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sdk=$sdk"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "runtime=$runtime"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "platform=$platform"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "components=$componentsJson"
    } else {
        Write-Host "GITHUB_OUTPUT is not set; projecting to stdout only (local validation mode)"
    }
    Write-KeyValue -Pairs $summary
    exit 0
}

# Reject an unlisted target before any bootstrap work so an inventory mismatch fails the job, not the transaction.
if ($Component -and ($componentIds -notcontains $Component)) {
    throw "component '$Component' is not enrolled in $rosterPath; refusing to verify an unlisted participant"
}

$componentLabel = if ($Component) { $Component } else { "all roster participants" }

# Export the projection so Bootstrap.fsx can assert projection == evaluated config == executing toolchain.
if (-not (Test-Path -LiteralPath $bootstrapScript)) {
    throw "bootstrap script not found: $bootstrapScript"
}

$env:RELEASE_EXPECTED_SDK = $sdk
$env:RELEASE_EXPECTED_RUNTIME = $runtime
$env:RELEASE_EXPECTED_PLATFORM = $platform

Write-Host "running toolchain verification: $bootstrapScript (component=$componentLabel)"
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

Write-Host "toolchain verification complete for component=$componentLabel"
exit 0
