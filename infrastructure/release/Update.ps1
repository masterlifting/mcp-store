# role: entrypoint/command
# Pre-SDK projection and post-install verification of the canonical toolchain.
# `Participants.json` is the only enrollment authority: the projection, the component list, the workflow loops and the
# CLI paths derive from it, and `*/release/Config.json` stays the only value source for the per-component policy. The
# shared toolchain comes from its owners: the SDK version from the root `global.json`, and the framework, language and
# runtime facts from the one F# evaluator (`Toolchain.fsx`), so `-Project` (which runs before any SDK exists) emits none of
# them and evaluates no F#, and `-Verify` hands over the verified values after the install.

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
. (Join-Path $PSScriptRoot "Toolchain.ps1")

$rosterPath = Join-Path $Root "infrastructure/release/Participants.json"

# The exact policy field set the compiled reader also accepts; a drift here fails before any SDK install.
$script:PolicyFields = @(
    "componentId"; "version"; "entryDll"; "platform"; "archiveName"; "assetUri"; "publishedFiles"; "launchKind"; "launchArguments")
# The only toolchain-related field left in a component policy is the platform label: a RID the artifact is qualified for,
# not a version and not byte-derivable. The SDK and runtime versions are not policy and are rejected as unknown fields.
$script:ToolchainFields = @("platform")
# The launch descriptors are authored policy with the same closed shape the compiled reader accepts: an ordered
# non-empty list of flag/input/type triples, a closed input-type set, and unique flags and input names.
$script:LaunchArgumentFields = @("flag"; "input"; "type")
$script:LaunchArgumentTypes = @("absolute-file"; "absolute-directory"; "sha256")
# The closed launch-kind set, with a kind-specific argument rule: dotnet-exec needs at least one required argument,
# dotnet-cli declares none. An unknown kind is rejected, matching the compiled reader.
$script:LaunchKinds = @("dotnet-exec"; "dotnet-cli")

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
    $launchKind = $document.launchKind
    if ($launchKind -isnot [string] -or $script:LaunchKinds -cnotcontains $launchKind) {
        throw "policy '$ConfigPath' launchKind '$launchKind' must be one of $($script:LaunchKinds -join ', ')"
    }
    $launchArguments = $document.launchArguments
    if ($null -eq $launchArguments -or $launchArguments -isnot [System.Array]) {
        throw "policy '$ConfigPath' launchArguments must be an array"
    }
    if ($launchKind -ceq "dotnet-exec" -and $launchArguments.Count -eq 0) {
        throw "policy '$ConfigPath' must declare at least one launchArguments entry for 'dotnet-exec'"
    }
    if ($launchKind -ceq "dotnet-cli" -and $launchArguments.Count -ne 0) {
        throw "policy '$ConfigPath' launchArguments must be empty for 'dotnet-cli'"
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
# The one authored SDK version, read by the same strict rules as the compiled reader before any SDK exists.
$sdk = Get-OwnedSdkVersion -Root $Root
$platform = $projection["platform"]

# `$summary` is shared by both modes; the verified toolchain values belong to -Verify alone.
$summary = [ordered]@{
    "RELEASE_SDK"      = $sdk
    "RELEASE_PLATFORM" = $platform
}
foreach ($componentId in $componentIds) {
    $fields = $projection[$componentId]
    $upper = $componentId.ToUpperInvariant()
    $summary["RELEASE_${upper}_VERSION"] = $fields["version"]
    $summary["RELEASE_${upper}_ENTRY"] = $fields["entryDll"]
}

if ($Project) {
    Write-Host "# projected canonical toolchain and roster participants from global.json and the policy JSON (no SDK, no F# evaluation)"
    # The matrix is projected as JSON so the workflow carries no participation list of its own.
    $componentsJson = ConvertTo-Json -Compress -InputObject $componentIds
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sdk=$sdk"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "platform=$platform"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "components=$componentsJson"
    } else {
        Write-Host "GITHUB_OUTPUT is not set; projecting to stdout only (local validation mode)"
    }
    # Later steps in the same job read the projection from the environment, so no step restates a participant list.
    $summary["RELEASE_COMPONENTS"] = $componentsJson
    # The roster-ordered componentId -> canonical configPath map, so downstream CI never derives `<componentId>/release/Config.json`.
    $configPaths = [ordered]@{}
    foreach ($entry in $roster) { $configPaths[$entry.componentId] = "$($entry.configPath)" }
    $configPathsJson = ConvertTo-Json -Compress -InputObject $configPaths
    $summary["RELEASE_CONFIG_PATHS"] = $configPathsJson
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "configPaths=$configPathsJson"
    }
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

# One credential-free F# evaluation owns the native MSBuild query and its decoding; it compares the selected SDK with the
# owner and prints exactly the four verified values, which are validated here before any is accepted.
$verifiedNames = @("MCP_RELEASE_VERIFIED_SDK", "MCP_RELEASE_VERIFIED_RUNTIME", "MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK", "MCP_RELEASE_VERIFIED_LANGUAGE_VERSION")
$maxVerifiedBytes = 1024
Push-Location $Root
try {
    $evaluationOutput = @(& dotnet fsi --nologo (Join-Path $PSScriptRoot "Toolchain.fsx"))
    if ($LASTEXITCODE -ne 0) { throw "the toolchain evaluation failed ($LASTEXITCODE): $($evaluationOutput -join ' ')" }
} finally {
    Pop-Location
}
if (([string]::Join("`n", $evaluationOutput)).Length -gt $maxVerifiedBytes) { throw "the toolchain evaluation output exceeds the bounded maximum of $maxVerifiedBytes characters" }

$transported = @{}
foreach ($line in $evaluationOutput) {
    $entry = [regex]::Match("$line", '\A(?<name>MCP_RELEASE_VERIFIED_[A-Z_]+)=(?<value>[0-9]+(\.[0-9]+)*(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?|net[0-9]+\.[0-9]+)\z')
    if (-not $entry.Success -or $verifiedNames -cnotcontains $entry.Groups['name'].Value -or $transported.ContainsKey($entry.Groups['name'].Value)) {
        throw "the toolchain evaluation printed an unexpected line"
    }
    $transported[$entry.Groups['name'].Value] = $entry.Groups['value'].Value
}
foreach ($name in $verifiedNames) {
    if (-not $transported.ContainsKey($name)) { throw "the toolchain evaluation did not print $name" }
}

$selected = $transported["MCP_RELEASE_VERIFIED_SDK"]
if ($selected -cne $sdk) { throw "the selected SDK is '$selected' where the owner declares '$sdk'" }
$runtime = $transported["MCP_RELEASE_VERIFIED_RUNTIME"]
$targetFramework = $transported["MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK"]
$languageVersion = $transported["MCP_RELEASE_VERIFIED_LANGUAGE_VERSION"]
if ($targetFramework -cnotmatch '\Anet[0-9]+\.[0-9]+\z') { throw "the verified TargetFramework '$targetFramework' is not a plain netX.Y moniker" }
if ($languageVersion -cnotmatch '\A[0-9]+\.[0-9]+\z') { throw "the verified LangVersion '$languageVersion' is not a plain X.Y version" }

# The payload is framework-dependent, so the host needs a COMPATIBLE runtime, not the exact derived patch: the same
# major.minor with a patch at least the derived value. A prerelease never satisfies a stable derived value, because a host
# does not roll a prerelease forward; exact equality is required only when the derived value is itself a prerelease.
function Test-RuntimeCompatible {
    param([string]$Derived, [string[]]$Installed)

    if ($Derived.Contains('-')) { return ($Installed -ccontains $Derived) }
    $stable = '\A(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)\z'
    $wanted = [regex]::Match($Derived, $stable)
    if (-not $wanted.Success) { throw "the derived runtime '$Derived' is not a three-part stable version" }
    foreach ($candidate in $Installed) {
        $found = [regex]::Match("$candidate", $stable)
        if (-not $found.Success) { continue }
        if ($found.Groups['major'].Value -eq $wanted.Groups['major'].Value -and
            $found.Groups['minor'].Value -eq $wanted.Groups['minor'].Value -and
            [int]$found.Groups['patch'].Value -ge [int]$wanted.Groups['patch'].Value) {
            return $true
        }
    }
    return $false
}

$installedRuntimes = @(Get-InstalledRuntime)
$observedRuntimes = @($installedRuntimes | Where-Object { $_.Framework -eq "Microsoft.NETCore.App" } | ForEach-Object { $_.Version })
if (-not (Test-RuntimeCompatible -Derived $runtime -Installed $observedRuntimes)) {
    throw "no installed runtime is compatible with 'Microsoft.NETCore.App $runtime' (same major.minor, patch at least the derived value, never a prerelease for a stable value); observed [$($observedRuntimes -join ', ')]"
}

$runningPlatform = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
if ($runningPlatform -ne $platform) {
    throw "this host runs '$runningPlatform' where the canonical platform is '$platform'"
}

Write-Host "verified executing toolchain sdk=$sdk runtime=$runtime platform=$platform for $componentLabel"

# The verified facts are emitted only here, after every check above has passed and before any credential step.
$verified = [ordered]@{
    "MCP_RELEASE_VERIFIED_SDK"              = $sdk
    "MCP_RELEASE_VERIFIED_RUNTIME"          = $runtime
    "MCP_RELEASE_VERIFIED_TARGET_FRAMEWORK" = $targetFramework
    "MCP_RELEASE_VERIFIED_LANGUAGE_VERSION" = $languageVersion
}

if ($env:GITHUB_ENV) {
    Write-KeyValue -Pairs $summary -Target $env:GITHUB_ENV
    Write-KeyValue -Pairs $verified -Target $env:GITHUB_ENV
} else {
    Write-Host "GITHUB_ENV is not set; skipping environment write (local validation mode)"
    Write-KeyValue -Pairs $verified
}

exit 0
