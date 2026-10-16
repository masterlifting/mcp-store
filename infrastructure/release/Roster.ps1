# role: reusable module/helper
# Pre-SDK reader for the enrollment roster; it mirrors the compiled `ReleaseEnrollment` acceptance rules so every gate rejects the same drift.
# Only structural and participation policy is mirrored here; versions, SDKs, allowlists and URIs stay in each producer's canonical `Config.json`.

$script:ReleaseSupportedComponentIds = @("dotnet", "workflow")
$script:ReleaseForbiddenRosterFields = @(
    "version"; "sdkVersion"; "runtimeVersion"; "platform"; "archiveName"; "assetUri"; "entryAllowlist"; "publishedFiles"; "entryDll"; "launchArguments")

function Get-ReleaseRoster {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Root)

    $rosterPath = Join-Path $Root "infrastructure\release\Participants.json"

    if (-not (Test-Path -LiteralPath $rosterPath)) {
        throw "enrollment roster not found: $rosterPath"
    }

    try {
        $document = Get-Content -LiteralPath $rosterPath -Raw | ConvertFrom-Json
    } catch {
        throw "the enrollment roster is not valid JSON: $rosterPath"
    }

    if ($null -eq $document -or $null -eq $document.participants) {
        throw "the enrollment roster is missing its 'participants' array: $rosterPath"
    }

    $entries = @($document.participants)
    if ($entries.Count -eq 0) {
        throw "the enrollment roster is empty: $rosterPath"
    }

    $seen = New-Object System.Collections.Generic.List[string]
    $index = 0

    foreach ($entry in $entries) {
        $index = $index + 1

        foreach ($field in $script:ReleaseForbiddenRosterFields) {
            if ($entry.PSObject.Properties.Name -contains $field) {
                throw "the enrollment roster entry at index $($index - 1) has forbidden field '$field'; versions, SDKs, allowlists, URIs, and launch descriptors must stay in each producer's Config.json"
            }
        }

        if ([string]::IsNullOrWhiteSpace($entry.componentId)) {
            throw "the enrollment roster entry at index $($index - 1) has an empty or missing componentId"
        }
        if ([string]::IsNullOrWhiteSpace($entry.configPath)) {
            throw "the enrollment roster entry at index $($index - 1) has an empty or missing configPath"
        }
        # The policy path is the exact CLI argument shape the release runner receives, so it must stay a contained relative JSON path.
        if ([System.IO.Path]::IsPathRooted($entry.configPath) -or $entry.configPath.Contains("..") -or $entry.configPath -notmatch '\.json$') {
            throw "the enrollment roster entry at index $($index - 1) has a configPath '$($entry.configPath)' that is not a contained relative JSON policy path"
        }
        if ($seen -contains $entry.componentId) {
            throw "the enrollment roster has a duplicate componentId '$($entry.componentId)'"
        }
        if ($script:ReleaseSupportedComponentIds -notcontains $entry.componentId) {
            throw "the enrollment roster entry at index $($index - 1) has unsupported componentId '$($entry.componentId)'; the mandatory supported set is [$($script:ReleaseSupportedComponentIds -join ', ')]"
        }

        $seen.Add($entry.componentId)
    }

    # A roster that omits a supported participant is drift even though every listed entry is valid.
    $missing = @($script:ReleaseSupportedComponentIds | Where-Object { $seen -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "the enrollment roster componentId set does not match the mandatory supported set [$($script:ReleaseSupportedComponentIds -join ', ')]; missing required componentId(s): $($missing -join ', ')"
    }

    return $entries
}
