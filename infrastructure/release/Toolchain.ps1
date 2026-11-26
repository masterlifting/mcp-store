# role: reusable module/helper
# The pre-SDK owner read: only the exact stable SDK version in the root `global.json` is needed before any SDK exists.
# The framework, language and runtime facts come from the F# evaluator that `Update.ps1 -Verify` runs after the install.

$script:ToolchainMaxOwnerBytes = 16 * 1024
$script:ToolchainStableVersion = '\A[0-9]+\.[0-9]+\.[0-9]+\z'

# `rollForward: disable` and `allowPrerelease: false` keep the pinned stable version the only SDK the host may select.
function Get-OwnedSdkVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $path = Join-Path $Root "global.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "required toolchain owner not found: $path" }
    $length = (Get-Item -LiteralPath $path).Length
    if ($length -gt $script:ToolchainMaxOwnerBytes) { throw "toolchain file '$path' exceeds the bounded maximum of $($script:ToolchainMaxOwnerBytes) bytes (got $length)" }

    try {
        $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "toolchain file '$path' is not valid JSON: $($_.Exception.Message)"
    }
    $sdk = $document.sdk
    if ($null -eq $sdk) { throw "toolchain file '$path' has no 'sdk' object" }
    if ($sdk.version -isnot [string] -or $sdk.version -cnotmatch $script:ToolchainStableVersion) {
        throw "toolchain file '$path' 'sdk.version' must be a stable three-part version"
    }
    if ($sdk.rollForward -isnot [string] -or $sdk.rollForward -cne "disable") { throw "toolchain file '$path' 'sdk.rollForward' must be exactly `"disable`"" }
    if ($sdk.allowPrerelease -isnot [bool] -or $sdk.allowPrerelease) { throw "toolchain file '$path' 'sdk.allowPrerelease' must be exactly false" }
    return $sdk.version
}
