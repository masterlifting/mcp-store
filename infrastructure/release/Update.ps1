# role: entrypoint/command
# Pre-SDK projection and post-install verification of the canonical toolchain from each producer's policy JSON.
# `Participants.json` is the only enrollment authority: the projection, the component list, the workflow loops and the
# CLI paths derive from it, and `*/release/Config.json` stays the only value source. Nothing here evaluates F#, so the
# projection runs before any SDK exists and the verification needs no separate bootstrap script.

[CmdletBinding(DefaultParameterSetName = "Project")]
param(
    [Parameter(ParameterSetName = "Verify")]
    [string]$Component = "",

    [Parameter(Mandatory = $true, ParameterSetName = "Project")]
    [switch]$Project,

    [Parameter(Mandatory = $true, ParameterSetName = "Verify")]
    [switch]$Verify,

    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Roster.ps1")

$rosterPath = Join-Path $Root "infrastructure/release/Participants.json"

# The exact policy field set the compiled reader also accepts; a drift here fails before any SDK install.
$script:PolicyFields = @(
    "componentId"; "version"; "entryDll"; "sdkVersion"; "runtimeVersion"; "platform"; "archiveName"; "assetUri"; "publishedFiles"; "launchArguments")
$script:ToolchainFields = @("sdkVersion"; "runtimeVersion"; "platform")
# The launch descriptors are authored policy with the same closed shape the compiled reader accepts: an ordered
# non-empty list of flag/input/type triples, a closed input-type set, and unique flags and input names.
$script:LaunchArgumentFields = @("flag"; "input"; "type")
$script:LaunchArgumentTypes = @("absolute-file"; "absolute-directory"; "sha256")

function Read-Policy {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "canonical policy not found: $ConfigPath"
    }

    $document = $null
    try {
        $document = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    } catch {
        throw "the canonical policy is not valid JSON: $ConfigPath"
    }
    if ($null -eq $document) { throw "the canonical policy is empty: $ConfigPath" }

    $names = @($document.PSObject.Properties.Name)
    $missing = @($script:PolicyFields | Where-Object { $names -notcontains $_ })
    if ($missing.Count -gt 0) { throw "policy '$ConfigPath' is missing field(s): $($missing -join ', ')" }
    $unknown = @($names | Where-Object { $script:PolicyFields -notcontains $_ })
    if ($unknown.Count -gt 0) { throw "policy '$ConfigPath' declares unknown field(s): $($unknown -join ', ')" }

    foreach ($name in @("componentId", "version", "entryDll") + $script:ToolchainFields) {
        if ([string]::IsNullOrWhiteSpace("$($document.$name)")) { throw "policy '$ConfigPath' has an empty '$name'" }
    }
    $publishedFiles = @($document.publishedFiles)
    if ($publishedFiles.Count -eq 0 -or @($publishedFiles | Where-Object { [string]::IsNullOrWhiteSpace("$_") }).Count -gt 0) {
        throw "policy '$ConfigPath' must declare at least one non-empty publishedFiles entry"
    }

    # Reject descriptors before SDK setup so bootstrap and compiled policy validation cannot disagree.
    $launchArguments = $document.launchArguments
    if ($null -eq $launchArguments -or $launchArguments -isnot [System.Array] -or $launchArguments.Count -eq 0) {
        throw "policy '$ConfigPath' must declare at least one launchArguments entry"
    }
    $argumentFlags = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $argumentInputs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $launchArguments.Count; $index++) {
        $argument = $launchArguments[$index]

        if ($null -eq $argument -or $argument -isnot [System.Management.Automation.PSCustomObject]) {
            throw "policy '$ConfigPath' launchArguments[$index] must be an object"
        }

        $names = @($argument.PSObject.Properties.Name)
        $unknown = @($names | Where-Object { $script:LaunchArgumentFields -cnotcontains $_ })
        if ($unknown.Count -gt 0) {
            throw "policy '$ConfigPath' launchArguments[$index] declares unknown field(s): $($unknown -join ', ')"
        }

        foreach ($name in $script:LaunchArgumentFields) {
            if ($names -cnotcontains $name) { throw "policy '$ConfigPath' launchArguments[$index] is missing field '$name'" }
            if ([string]::IsNullOrWhiteSpace("$($argument.$name)")) { throw "policy '$ConfigPath' launchArguments[$index] has an empty '$name'" }
        }
        if ($script:LaunchArgumentTypes -cnotcontains "$($argument.type)") {
            throw "policy '$ConfigPath' launchArguments[$index] type '$($argument.type)' must be one of $($script:LaunchArgumentTypes -join ', ')"
        }
        if (-not $argumentFlags.Add("$($argument.flag)")) { throw "policy '$ConfigPath' launchArguments[$index] repeats the flag '$($argument.flag)'" }
        if (-not $argumentInputs.Add("$($argument.input)")) { throw "policy '$ConfigPath' launchArguments[$index] repeats the input '$($argument.input)'" }
    }

    return $document
}

function Get-Projection {
    param([Parameter(Mandatory = $true)][object[]]$Participants)

    $result = @{}

    foreach ($entry in $Participants) {
        $document = Read-Policy -ConfigPath (Join-Path $Root $entry.configPath)

        # A policy whose identity contradicts the roster entry for the same path is drift, not policy.
        if ("$($document.componentId)" -ne $entry.componentId) {
            throw "componentId '$($document.componentId)' in $($entry.configPath) does not match roster participant '$($entry.componentId)'"
        }

        $result[$entry.componentId] = @{
            version        = "$($document.version)"
            entryDll       = "$($document.entryDll)"
            sdkVersion     = "$($document.sdkVersion)"
            runtimeVersion = "$($document.runtimeVersion)"
            platform       = "$($document.platform)"
        }
    }

    foreach ($name in $script:ToolchainFields) {
        $distinct = @($Participants | ForEach-Object { $result[$_.componentId][$name] } | Sort-Object -Unique)
        if ($distinct.Count -ne 1) {
            throw "participants disagree on canonical '$name': $($distinct -join ', ')"
        }
        $result[$name] = $distinct[0]
    }

    return $result
}

function Get-InstalledSdk {
    $lines = & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks failed ($LASTEXITCODE)" }

    foreach ($line in $lines) {
        if ($line -match '^(?<version>\S+)\s+\[(?<root>.+)\]\s*$') {
            [pscustomobject]@{ Version = $Matches['version']; Root = $Matches['root'] }
        }
    }
}

function Get-InstalledRuntime {
    $lines = & dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-runtimes failed ($LASTEXITCODE)" }

    foreach ($line in $lines) {
        if ($line -match '^(?<framework>\S+)\s+(?<version>\S+)\s+\[(?<root>.+)\]\s*$') {
            [pscustomobject]@{ Framework = $Matches['framework']; Version = $Matches['version']; Root = $Matches['root'] }
        }
    }
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
$projection = Get-Projection -Participants $roster
$sdk = $projection["sdkVersion"]
$runtime = $projection["runtimeVersion"]
$platform = $projection["platform"]

$summary = [ordered]@{
    "RELEASE_SDK"      = $sdk
    "RELEASE_RUNTIME"  = $runtime
    "RELEASE_PLATFORM" = $platform
}
foreach ($componentId in $componentIds) {
    $fields = $projection[$componentId]
    $upper = $componentId.ToUpperInvariant()
    $summary["RELEASE_${upper}_VERSION"] = $fields["version"]
    $summary["RELEASE_${upper}_ENTRY"] = $fields["entryDll"]
}

if ($Project) {
    Write-Host "# projected canonical toolchain and roster participants from the policy JSON (no F# evaluation)"
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
    # Later steps in the same job read the projection from the environment, so no step restates a participant list.
    $summary["RELEASE_COMPONENTS"] = $componentsJson
    if ($env:GITHUB_ENV) {
        Write-KeyValue -Pairs $summary -Target $env:GITHUB_ENV
    } else {
        Write-Host "GITHUB_ENV is not set; skipping environment write (local validation mode)"
    }
    Write-KeyValue -Pairs $summary
    exit 0
}

# Reject an unlisted target before verification so an inventory mismatch fails the job, not the toolchain check.
if ($Component -and ($componentIds -notcontains $Component)) {
    throw "component '$Component' is not enrolled in $rosterPath; refusing to verify an unlisted participant"
}

$componentLabel = if ($Component) { $Component } else { "all roster participants" }

$installedSdks = @(Get-InstalledSdk)
$observedSdks = @($installedSdks | ForEach-Object { $_.Version })
if ($observedSdks -notcontains $sdk) {
    throw "the canonical SDK '$sdk' is not installed; observed [$($observedSdks -join ', ')]"
}

$installedRuntimes = @(Get-InstalledRuntime)
$observedRuntimes = @($installedRuntimes | Where-Object { $_.Framework -eq "Microsoft.NETCore.App" } | ForEach-Object { $_.Version })
if ($observedRuntimes -notcontains $runtime) {
    throw "the canonical runtime 'Microsoft.NETCore.App $runtime' is not installed; observed [$($observedRuntimes -join ', ')]"
}

$runningPlatform = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
if ($runningPlatform -ne $platform) {
    throw "this host runs '$runningPlatform' where the canonical platform is '$platform'"
}

Write-Host "verified executing toolchain sdk=$sdk runtime=$runtime platform=$platform for $componentLabel"

if ($env:GITHUB_ENV) {
    Write-KeyValue -Pairs $summary -Target $env:GITHUB_ENV
} else {
    Write-Host "GITHUB_ENV is not set; skipping environment write (local validation mode)"
}

exit 0
