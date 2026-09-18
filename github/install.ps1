[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$module = 'github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2'
$goPackageId = 'GoLang.Go'

$tokenPresent = Test-Path -LiteralPath 'Env:GITHUB_PERSONAL_ACCESS_TOKEN'
if ($tokenPresent) {
    Write-Host 'GITHUB_PERSONAL_ACCESS_TOKEN is present (value not inspected).'
} else {
    Write-Warning 'GITHUB_PERSONAL_ACCESS_TOKEN is not present; configure authentication separately if the server requires it.'
}

# Do not let installer subprocesses inherit the credential. Only the boolean
# above is retained, and removing the process-local variable does not change
# the caller's environment.
Remove-Item -LiteralPath 'Env:GITHUB_PERSONAL_ACCESS_TOKEN' -ErrorAction SilentlyContinue

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

function Refresh-ProcessPath {
    $pathEntries = @(
        [Environment]::GetEnvironmentVariable('Path', 'Process'),
        [Environment]::GetEnvironmentVariable('Path', 'User'),
        [Environment]::GetEnvironmentVariable('Path', 'Machine')
    ) | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace($_)) {
            $_ -split [IO.Path]::PathSeparator
        }
    } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $uniqueEntries = foreach ($entry in $pathEntries) {
        if ($seen.Add($entry)) {
            $entry
        }
    }

    $env:Path = $uniqueEntries -join [IO.Path]::PathSeparator
}

function Add-ProcessPathEntry {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $currentEntries = @($env:Path -split [IO.Path]::PathSeparator | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    $normalizedPath = Get-NormalizedPath -Path $Path
    $alreadyPresent = $currentEntries | Where-Object {
        try {
            [StringComparer]::OrdinalIgnoreCase.Equals((Get-NormalizedPath -Path $_), $normalizedPath)
        } catch {
            $false
        }
    }

    if ($null -eq $alreadyPresent) {
        $env:Path = ($currentEntries + $Path) -join [IO.Path]::PathSeparator
        Write-Host "Added Go binary directory to this process PATH only: $Path"
    }
}

$goCommand = Get-Command -Name 'go' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $goCommand) {
    $wingetCommand = Get-Command -Name 'winget' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $wingetCommand) {
        throw "Go is unavailable and winget was not found through PATH. Install Go with winget ($goPackageId) or make go available in PATH, then rerun this script."
    }

    Write-Host "Go was not found through PATH. Installing $goPackageId with winget."
    $wingetArguments = @(
        'install',
        '--id', $goPackageId,
        '--exact',
        '--silent',
        '--disable-interactivity',
        '--accept-source-agreements',
        '--accept-package-agreements'
    )
    & $wingetCommand.Source @wingetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install Go package $goPackageId (exit code $LASTEXITCODE)."
    }

    Refresh-ProcessPath
    $goCommand = Get-Command -Name 'go' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $goCommand) {
        throw "winget completed for $goPackageId, but Go remains inaccessible through PATH. Restart PowerShell so PATH changes can be loaded, then rerun this script."
    }

    Write-Host "Go is available after winget provisioning: $($goCommand.Source)"
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

Add-ProcessPathEntry -Path $goBin

$serverCommand = Get-Command -Name 'github-mcp-server' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
$expectedServerPath = Join-Path -Path $goBin -ChildPath 'github-mcp-server.exe'
if ($null -eq $serverCommand) {
    throw "Installation completed, but github-mcp-server is not resolvable through PATH. Add Go's binary directory ('$goBin') to your user or system PATH manually, open a new PowerShell session, and run this script again. This installer does not modify persistent PATH."
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
    throw "Installation completed, but github-mcp-server resolved to '$resolvedServerPath' instead of the expected installed executable '$expectedServerPath'. Ensure Go's binary directory is earlier than any conflicting command on PATH, then run this script again. This installer does not modify persistent PATH."
}

Write-Host "github-mcp-server resolves through PATH: $resolvedServerPath"
Write-Host 'Restart consuming applications so they can see the installed server; persistent PATH was not changed.'
Write-Host 'GitHub MCP server installation completed.'
