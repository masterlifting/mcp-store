[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$module = 'github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2'
$installer = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\install.ps1')).Path
$pwsh = (Get-Command -Name 'pwsh' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1).Source

if ([string]::IsNullOrWhiteSpace($pwsh)) {
    throw 'PowerShell 7 (pwsh) is required to run the local installer harness.'
}

function New-FakeGo {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $GoPath,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $GoBin,

        [Parameter(Mandatory)]
        [string] $InstallArgumentPath,

        [Parameter(Mandatory)]
        [string] $ServerPath,

        [Parameter(Mandatory)]
        [string] $Module
    )

    $gobinOutput = if ([string]::IsNullOrWhiteSpace($GoBin)) {
        ''
    } else {
        "  echo $GoBin`r`n"
    }

    $content = @"
@echo off
if /I "%1"=="env" if /I "%2"=="GOBIN" (
$gobinOutput  exit /b 0
)
if /I "%1"=="env" if /I "%2"=="GOPATH" (
  echo $GoPath
  exit /b 0
)
if /I "%1"=="install" if "%2"=="$Module" (
  >"$InstallArgumentPath" echo %2
   >"$ServerPath" echo @echo off
   >>"$ServerPath" echo exit /b 0
  exit /b 0
)
exit /b 1
"@

    [System.IO.File]::WriteAllText(
        $Path,
        $content,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Invoke-InstallerCase {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Case,

        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $ParentPath
    )

    $caseRoot = Join-Path $Root $Case.Name
    $fakeBin = Join-Path $caseRoot 'fake-bin'
    $goPath = Join-Path $caseRoot 'gopath'
    $goBin = if ([string]::IsNullOrWhiteSpace($Case.Gobin)) {
        Join-Path $goPath 'bin'
    } else {
        $Case.Gobin
    }
    $installArgumentPath = Join-Path $caseRoot 'install-argument.txt'
    $serverPath = Join-Path $goBin 'github-mcp-server.exe'
    $fakeGoPath = Join-Path $fakeBin 'go.cmd'

    New-Item -ItemType Directory -Path $fakeBin, $goPath, $goBin -Force > $null
    if ($Case.HasGo) {
        New-FakeGo -Path $fakeGoPath -GoPath $goPath -GoBin $Case.Gobin `
            -InstallArgumentPath $installArgumentPath -ServerPath $serverPath -Module $module
    }

    if ($Case.HasConflictingServer) {
        $conflictingServerPath = Join-Path $fakeBin 'github-mcp-server.exe'
        [IO.File]::WriteAllText($conflictingServerPath, '')
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwsh
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($installer)
    # The test-only PATH is isolated to the installer child process.
    $pathEntries = @($fakeBin)
    if ($Case.IncludeGoBinInPath) {
        $pathEntries += $goBin
    }
    if ($Case.IncludeParentPath -and -not [string]::IsNullOrWhiteSpace($ParentPath)) {
        $pathEntries += $ParentPath
    }
    $startInfo.Environment['Path'] = $pathEntries -join [IO.Path]::PathSeparator

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unable to start installer process for case '$($Case.Name)'."
        }

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
    }

    $output = "$stdout`n$stderr"
    if (-not $Case.ExpectSuccess) {
        if ($exitCode -eq 0) {
            throw "Installer case '$($Case.Name)' unexpectedly succeeded. Output: $output"
        }
        if ($output -notmatch $Case.ExpectedErrorPattern) {
            throw "Installer case '$($Case.Name)' returned the expected failure code but not the expected error. Output: $output"
        }
        return
    }

    if ($exitCode -ne 0) {
        throw "Installer case '$($Case.Name)' failed with exit code $exitCode.`n$output"
    }

    $expectedDirectory = "Go binary directory: $goBin"
    if ($stdout -notmatch [regex]::Escape($expectedDirectory)) {
        throw "Installer case '$($Case.Name)' did not report the expected Go binary directory '$goBin'. Output: $stdout"
    }

    if ($stdout -notmatch 'github-mcp-server resolves through PATH') {
        throw "Installer case '$($Case.Name)' did not report PATH resolution. Output: $stdout"
    }

    if (-not (Test-Path -LiteralPath $installArgumentPath)) {
        throw "Installer case '$($Case.Name)' did not invoke the mocked go install command."
    }

    $installArgument = (Get-Content -LiteralPath $installArgumentPath -Raw).Trim()
    if ($installArgument -ne $module) {
        throw "Installer case '$($Case.Name)' used unexpected install argument '$installArgument'."
    }

    if (-not (Test-Path -LiteralPath $serverPath)) {
        throw "Installer case '$($Case.Name)' did not create the mocked PATH-visible server command."
    }
}

$parentPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "github-mcp-install-test-$([Guid]::NewGuid().ToString('N'))"
$cases = @(
    [pscustomobject]@{
        Name = 'gopath-fallback'; Gobin = ''; HasGo = $true; IncludeGoBinInPath = $true
        IncludeParentPath = $true; HasConflictingServer = $false; ExpectSuccess = $true
    },
    [pscustomobject]@{
        Name = 'gobin-configured'; Gobin = (Join-Path $testRoot 'configured-bin'); HasGo = $true
        IncludeGoBinInPath = $true; IncludeParentPath = $true; HasConflictingServer = $false; ExpectSuccess = $true
    },
    [pscustomobject]@{
        Name = 'conflicting-earlier-command'; Gobin = (Join-Path $testRoot 'conflicting-go-bin'); HasGo = $true
        IncludeGoBinInPath = $false; IncludeParentPath = $false; HasConflictingServer = $true; ExpectSuccess = $false
        ExpectedErrorPattern = 'instead of the expected installed executable'
    },
    [pscustomobject]@{
        Name = 'missing-selected-go-bin'; Gobin = (Join-Path $testRoot 'missing-go-bin'); HasGo = $true
        IncludeGoBinInPath = $false; IncludeParentPath = $false; HasConflictingServer = $false; ExpectSuccess = $false
        ExpectedErrorPattern = 'not resolvable through PATH'
    },
    [pscustomobject]@{
        Name = 'missing-go'; Gobin = ''; HasGo = $false; IncludeGoBinInPath = $false
        IncludeParentPath = $false; HasConflictingServer = $false; ExpectSuccess = $false
        ExpectedErrorPattern = 'Go is required, but the go executable was not found'
    }
)

try {
    New-Item -ItemType Directory -Path $testRoot -Force > $null

    foreach ($case in $cases) {
        Invoke-InstallerCase -Case $case -Root $testRoot -ParentPath $parentPath
    }

    if ([Environment]::GetEnvironmentVariable('Path', 'Process') -ne $parentPath) {
        throw 'The local installer harness process PATH changed during validation.'
    }

    'PASS: success, missing-Go, command-resolution, and conflicting-command cases passed without changing the harness PATH.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
