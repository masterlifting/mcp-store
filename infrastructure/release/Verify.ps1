# role: reusable module/helper
# One purpose: verify a downloaded artifact directory against an independently declared file set.
# `-InventoryJson` requires exactly the declared relative paths, each hashing to its declared SHA-256; `-MembersJson`
# requires exactly the declared relative members. Both inputs are JSON, so a caller never depends on how a host binds
# multi-value script arguments. Deterministic, read-only, and it touches no network, token, receipt or provenance
# state, so a caller can run it before executing anything from the verified set.

[CmdletBinding(DefaultParameterSetName = "Inventory")]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true, ParameterSetName = "Inventory")][string]$InventoryJson,
    [Parameter(Mandatory = $true, ParameterSetName = "Members")][string]$MembersJson
)

$ErrorActionPreference = "Stop"

function Assert-RelativeMember {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { throw "$Source declares an empty member path" }
    if ($Value.Contains('\')) { throw "$Source declares '$Value' with a backslash separator; members use '/' only" }
    if ($Value.StartsWith('/') -or $Value.EndsWith('/')) { throw "$Source declares '$Value' with a leading or trailing separator" }

    $segments = @($Value.Split('/'))
    if (($segments -contains '') -or ($segments -contains '.') -or ($segments -contains '..')) {
        throw "$Source declares '$Value' with an empty, current or parent path segment"
    }
}

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    throw "the verification root is not a directory: $Root"
}

$rootFull = (Resolve-Path -LiteralPath $Root).Path

$observed = [ordered]@{}
foreach ($file in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force -File | Sort-Object FullName)) {
    $relative = [System.IO.Path]::GetRelativePath($rootFull, $file.FullName).Replace('\', '/')
    Assert-RelativeMember -Value $relative -Source "the downloaded artifact"

    # A case-only duplicate cannot exist twice on a case-insensitive host but can inside a transported archive.
    foreach ($existing in $observed.Keys) {
        if ([string]::Equals($existing, $relative, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "the downloaded artifact carries case-colliding members '$existing' and '$relative'"
        }
    }

    $observed[$relative] = $file.FullName
}

$expected = [ordered]@{}
if ($PSCmdlet.ParameterSetName -eq "Inventory") {
    $declared = @($InventoryJson | ConvertFrom-Json)
    if ($declared.Count -eq 0) { throw "the declared inventory is empty; an empty inventory is not a pass" }

    foreach ($entry in $declared) {
        $path = "$($entry.path)"
        $sha = "$($entry.sha256)"
        Assert-RelativeMember -Value $path -Source "the declared inventory"
        if ($sha -cnotmatch '^[0-9a-f]{64}$') { throw "the declared inventory entry '$path' carries no bare lowercase SHA-256" }
        if ($expected.Contains($path)) { throw "the declared inventory repeats '$path'" }
        $expected[$path] = $sha
    }
} else {
    $declaredMembers = @($MembersJson | ConvertFrom-Json)
    if ($declaredMembers.Count -eq 0) { throw "the declared member set is empty; an empty expectation is not a pass" }

    foreach ($member in $declaredMembers) {
        $path = "$member"
        Assert-RelativeMember -Value $path -Source "the declared member set"
        if ($expected.Contains($path)) { throw "the declared member set repeats '$path'" }
        $expected[$path] = $null
    }
}

foreach ($path in $expected.Keys) {
    if (-not $observed.Contains($path)) { throw "the downloaded artifact is missing the declared member '$path'" }
}
foreach ($path in $observed.Keys) {
    if (-not $expected.Contains($path)) { throw "the downloaded artifact carries the undeclared member '$path'" }
}

# Membership is proven above; the inventory mode additionally proves the exact bytes of every member.
if ($PSCmdlet.ParameterSetName -eq "Inventory") {
    foreach ($path in $expected.Keys) {
        $declaredSha = $expected[$path]
        $observedSha = (Get-FileHash -LiteralPath $observed[$path] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($observedSha -cne $declaredSha) { throw "member '$path' hashed to $observedSha, not the declared $declaredSha" }
        Write-Host "verified $path $observedSha"
    }
    Write-Host "inventory verification passed: $($observed.Count) member(s) match the declared path and SHA-256 set"
} else {
    foreach ($path in $expected.Keys) {
        Write-Host "verified $path"
    }
    Write-Host "member verification passed: $($observed.Count) member(s) match the declared relative set exactly"
}

exit 0
