[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$module = 'github.com/github/github-mcp-server/cmd/github-mcp-server@v1.12.2'
$wingetArguments = 'install --id GoLang.Go --exact --silent --disable-interactivity --accept-source-agreements --accept-package-agreements'
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
        [string] $Module,

        [Parameter(Mandatory)]
        [string] $TokenMarkerPath
    )

    $gobinOutput = if ([string]::IsNullOrWhiteSpace($GoBin)) {
        ''
    } else {
        "  echo $GoBin`r`n"
    }

    $content = @"
@echo off
if defined GITHUB_PERSONAL_ACCESS_TOKEN (
  >"$TokenMarkerPath" echo present
) else (
  >"$TokenMarkerPath" echo absent
)
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

    [IO.File]::WriteAllText(
        $Path,
        $content,
        [Text.UTF8Encoding]::new($false)
    )
}

function New-FakeWinget {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ProvisionedGoPath,

        [Parameter(Mandatory)]
        [string] $GoDestinationPath,

        [Parameter(Mandatory)]
        [string] $InvocationPath,

        [Parameter(Mandatory)]
        [string] $TokenMarkerPath,

        [Parameter(Mandatory)]
        [bool] $Succeeds
    )

    $exitCode = if ($Succeeds) { 0 } else { 17 }
    $provision = if ($Succeeds) {
        "copy /Y `"$ProvisionedGoPath`" `"$GoDestinationPath`" >nul"
    } else {
        ''
    }
    $content = @"
@echo off
>"$InvocationPath" echo %*
if defined GITHUB_PERSONAL_ACCESS_TOKEN (
  >"$TokenMarkerPath" echo present
) else (
  >"$TokenMarkerPath" echo absent
)
$provision
exit /b $exitCode
"@

    [IO.File]::WriteAllText(
        $Path,
        $content,
        [Text.UTF8Encoding]::new($false)
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
    $goBin = Join-Path $goPath 'bin'
    $installArgumentPath = Join-Path $caseRoot 'install-argument.txt'
    $serverPath = Join-Path $goBin 'github-mcp-server.exe'
    $fakeGoPath = Join-Path $fakeBin 'go.cmd'
    $provisionedGoPath = Join-Path $caseRoot 'provisioned-go.cmd'
    $wingetInvocationPath = Join-Path $caseRoot 'winget-arguments.txt'
    $goTokenMarkerPath = Join-Path $caseRoot 'go-token.txt'
    $wingetTokenMarkerPath = Join-Path $caseRoot 'winget-token.txt'

    New-Item -ItemType Directory -Path $fakeBin, $goPath, $goBin -Force > $null
    if ($Case.HasGo) {
        New-FakeGo -Path $fakeGoPath -GoPath $goPath -GoBin $Case.Gobin `
            -InstallArgumentPath $installArgumentPath -ServerPath $serverPath -Module $module `
            -TokenMarkerPath $goTokenMarkerPath
    }
    if ($Case.ProvisionGo) {
        New-FakeGo -Path $provisionedGoPath -GoPath $goPath -GoBin $Case.Gobin `
            -InstallArgumentPath $installArgumentPath -ServerPath $serverPath -Module $module `
            -TokenMarkerPath $goTokenMarkerPath
    }
    if ($Case.HasWinget) {
        New-FakeWinget -Path (Join-Path $fakeBin 'winget.cmd') -ProvisionedGoPath $provisionedGoPath `
            -GoDestinationPath $fakeGoPath -InvocationPath $wingetInvocationPath `
            -TokenMarkerPath $wingetTokenMarkerPath -Succeeds $Case.WingetSucceeds
    }

    if ($Case.HasConflictingServer) {
        [IO.File]::WriteAllText((Join-Path $fakeBin 'github-mcp-server.exe'), '')
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwsh
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($installer)
    $null = $startInfo.Environment.Remove('GITHUB_PERSONAL_ACCESS_TOKEN')
    if ($Case.TokenPresent) {
        $startInfo.Environment['GITHUB_PERSONAL_ACCESS_TOKEN'] = 'fixture-token'
    }

    # The test-only PATH is isolated to the installer child process.
    $pathEntries = @($fakeBin)
    if ($Case.IncludeGoBinInPath) {
        $pathEntries += $goBin
    }
    if ($Case.IncludeParentPath -and -not [string]::IsNullOrWhiteSpace($ParentPath)) {
        $pathEntries += $ParentPath
    }
    $startInfo.Environment['Path'] = $pathEntries -join [IO.Path]::PathSeparator

    $process = [Diagnostics.Process]::new()
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
    } elseif ($exitCode -ne 0) {
        throw "Installer case '$($Case.Name)' failed with exit code $exitCode.`n$output"
    }

    $tokenPattern = if ($Case.TokenPresent) {
        'GITHUB_PERSONAL_ACCESS_TOKEN is present \(value not inspected\)'
    } else {
        'GITHUB_PERSONAL_ACCESS_TOKEN is not present'
    }
    if ($output -notmatch $tokenPattern) {
        throw "Installer case '$($Case.Name)' did not report token presence without exposing its value. Output: $output"
    }
    if ($output -match 'fixture-token') {
        throw "Installer case '$($Case.Name)' exposed the token value."
    }

    if (Test-Path -LiteralPath $goTokenMarkerPath) {
        if ((Get-Content -LiteralPath $goTokenMarkerPath -Raw).Trim() -ne 'absent') {
            throw "Installer case '$($Case.Name)' passed the token to the fake Go command."
        }
    }
    if (Test-Path -LiteralPath $wingetTokenMarkerPath) {
        if ((Get-Content -LiteralPath $wingetTokenMarkerPath -Raw).Trim() -ne 'absent') {
            throw "Installer case '$($Case.Name)' passed the token to the fake winget command."
        }
    }

    if (-not $Case.ExpectSuccess) {
        return
    }

    $expectedDirectory = "Go binary directory: $goBin"
    if ($stdout -notmatch [regex]::Escape($expectedDirectory)) {
        throw "Installer case '$($Case.Name)' did not report the expected Go binary directory '$goBin'. Output: $stdout"
    }
    if ($stdout -notmatch 'github-mcp-server resolves through PATH') {
        throw "Installer case '$($Case.Name)' did not report PATH resolution. Output: $stdout"
    }
    if ($stdout -notmatch 'Restart consuming applications') {
        throw "Installer case '$($Case.Name)' did not report the consuming-application restart requirement. Output: $stdout"
    }
    if (-not (Test-Path -LiteralPath $installArgumentPath)) {
        throw "Installer case '$($Case.Name)' did not invoke the mocked go install command."
    }
    if ((Get-Content -LiteralPath $installArgumentPath -Raw).Trim() -ne $module) {
        throw "Installer case '$($Case.Name)' used an unexpected install argument."
    }
    if (-not (Test-Path -LiteralPath $serverPath)) {
        throw "Installer case '$($Case.Name)' did not create the mocked PATH-visible server command."
    }
    if ($Case.ExpectWinget -and -not (Test-Path -LiteralPath $wingetInvocationPath)) {
        throw "Installer case '$($Case.Name)' did not invoke the mocked winget command."
    }
    if ($Case.ExpectWinget) {
        $actualWingetArguments = (Get-Content -LiteralPath $wingetInvocationPath -Raw).Trim()
        if ($actualWingetArguments -ne $wingetArguments) {
            throw "Installer case '$($Case.Name)' used unexpected winget arguments '$actualWingetArguments'."
        }
    }
    if ($Case.ExpectProcessPathRefresh -and $stdout -notmatch 'this process PATH only') {
        throw "Installer case '$($Case.Name)' did not report its process-only PATH refresh."
    }
}

$parentPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "github-mcp-install-test-$([Guid]::NewGuid().ToString('N'))"
$cases = @(
    [pscustomobject]@{
        Name = 'missing-go-provision-success'; Gobin = ''; HasGo = $false; HasWinget = $true
        ProvisionGo = $true; WingetSucceeds = $true; IncludeGoBinInPath = $true; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $true; ExpectWinget = $true; TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'missing-winget'; Gobin = ''; HasGo = $false; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $false; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $false; ExpectedErrorPattern = 'winget was not found through PATH'
        TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'winget-provision-failure'; Gobin = ''; HasGo = $false; HasWinget = $true
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $false; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $false; ExpectWinget = $true
        ExpectedErrorPattern = 'winget failed to install Go package GoLang.Go'; TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'existing-go-no-provision'; Gobin = ''; HasGo = $true; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $true; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $true; ExpectWinget = $false; TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'token-present'; Gobin = ''; HasGo = $true; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $true; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $true; ExpectWinget = $false; TokenPresent = $true
    },
    [pscustomobject]@{
        Name = 'token-missing'; Gobin = ''; HasGo = $true; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $true; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $true; ExpectWinget = $false; TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'process-only-go-bin-path'; Gobin = ''; HasGo = $true; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $false; IncludeParentPath = $false
        HasConflictingServer = $false; ExpectSuccess = $true; ExpectWinget = $false
        ExpectProcessPathRefresh = $true; TokenPresent = $false
    },
    [pscustomobject]@{
        Name = 'conflicting-earlier-command'; Gobin = ''; HasGo = $true; HasWinget = $false
        ProvisionGo = $false; WingetSucceeds = $false; IncludeGoBinInPath = $false; IncludeParentPath = $false
        HasConflictingServer = $true; ExpectSuccess = $false
        ExpectedErrorPattern = 'instead of the expected installed executable'; TokenPresent = $false
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

    'PASS: Go provisioning, winget failures, existing-Go, token presence, PATH resolution, and conflict cases passed without real installs or harness PATH changes.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
