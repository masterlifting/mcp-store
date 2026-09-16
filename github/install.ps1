[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$module = 'github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2'

function Get-NormalizedPath {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $root.Length) {
        $fullPath = $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }

    return $fullPath
}

$goCommand = Get-Command -Name 'go' -CommandType Application -ErrorAction SilentlyContinue
if ($null -eq $goCommand) {
    throw 'Go is required, but the go executable was not found through PATH. Install Go and make go available in PATH before running this script.'
}

$goBin = (& $goCommand.Source env GOBIN 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to determine Go's binary directory with 'go env GOBIN'."
}

if ([string]::IsNullOrWhiteSpace($goBin)) {
    $goPath = (& $goCommand.Source env GOPATH 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($goPath)) {
        throw "Unable to determine Go's binary directory with 'go env GOPATH'."
    }

    $goPathEntry = ($goPath -split [IO.Path]::PathSeparator | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    } | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($goPathEntry)) {
        throw "Go reported an empty GOPATH, so its binary directory cannot be identified."
    }

    $goBin = Join-Path -Path $goPathEntry -ChildPath 'bin'
}

Write-Host "Installing $module"
Write-Host "Go binary directory: $goBin"
& $goCommand.Source install $module
if ($LASTEXITCODE -ne 0) {
    throw "Go module installation failed for $module."
}

$serverCommand = Get-Command -Name 'github-mcp-server' -CommandType Application -ErrorAction SilentlyContinue
$expectedServerPath = Join-Path -Path $goBin -ChildPath 'github-mcp-server.exe'
if ($null -eq $serverCommand) {
    throw "Installation completed, but github-mcp-server is not resolvable through PATH. Add Go's binary directory ('$goBin') to your user or system PATH manually, open a new PowerShell session, and run this script again. This installer does not modify PATH."
}

$resolvedServerPath = if ($serverCommand.PSObject.Properties.Name -contains 'Path') {
    $serverCommand.Path
} else {
    $serverCommand.Source
}
$normalizedResolvedServerPath = if ([string]::IsNullOrWhiteSpace($resolvedServerPath)) {
    ''
} else {
    Get-NormalizedPath -Path $resolvedServerPath
}
$normalizedExpectedServerPath = Get-NormalizedPath -Path $expectedServerPath

if ([string]::IsNullOrWhiteSpace($resolvedServerPath) -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals($normalizedResolvedServerPath, $normalizedExpectedServerPath)) {
    throw "Installation completed, but github-mcp-server resolved to '$resolvedServerPath' instead of the expected installed executable '$expectedServerPath'. Ensure Go's binary directory is earlier than any conflicting command on PATH, then run this script again. This installer does not modify PATH."
}

Write-Host "github-mcp-server resolves through PATH: $resolvedServerPath"
Write-Host 'GitHub MCP server installation completed.'
