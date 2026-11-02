param(
  [Parameter(Mandatory = $true)]
  [string] $Script
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("workflow-test-" + [guid]::NewGuid().ToString("N"))
$appData = Join-Path $runRoot "appdata"
$homeDir = Join-Path $runRoot "home"
$tempDir = Join-Path $runRoot "temp"
$cliHome = Join-Path $runRoot "cli"
foreach ($d in @($runRoot, $appData, $homeDir, $tempDir, $cliHome)) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}

function Format-Bounded {
  param([string] $Text)
  $limit = 65536
  if ([string]::IsNullOrEmpty($Text)) { return $Text }
  if ($Text.Length -le $limit) { return $Text }
  return $Text.Substring(0, $limit) + "`n... [truncated, $($Text.Length - $limit) more characters]"
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "dotnet"
$psi.ArgumentList.Add("fsi")
$psi.ArgumentList.Add("--exec")
$psi.ArgumentList.Add("--quiet")
$psi.ArgumentList.Add((Join-Path $repoRoot $Script))
$psi.WorkingDirectory = $repoRoot
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.Environment["LOCALAPPDATA"] = $appData
$psi.Environment["APPDATA"] = $appData
$psi.Environment["ProgramData"] = $appData
$psi.Environment["USERPROFILE"] = $homeDir
$psi.Environment["HOME"] = $homeDir
$psi.Environment["TEMP"] = $tempDir
$psi.Environment["TMP"] = $tempDir
$psi.Environment["DOTNET_CLI_HOME"] = $cliHome
$psi.Environment["MCP_WORKFLOW_TEST_EXPECTED_APP_DATA"] = $appData
$psi.Environment["MCP_WORKFLOW_TEST_REAL_APP_DATA"] = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)

$exit = 1
try {
  $process = [System.Diagnostics.Process]::Start($psi)
  # Both drains start before the wait, so a full pipe buffer cannot block the child.
  $stdout = $process.StandardOutput.ReadToEndAsync()
  $stderr = $process.StandardError.ReadToEndAsync()
  $process.WaitForExit()
  $exit = $process.ExitCode
  $stdoutText = $stdout.GetAwaiter().GetResult()
  $stderrText = $stderr.GetAwaiter().GetResult()
  [Console]::Out.WriteLine((Format-Bounded $stdoutText))
  [Console]::Error.WriteLine((Format-Bounded $stderrText))
} finally {
  # Only the run root this recipe created is removed, and the captured exit code survives it.
  Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
}
exit $exit
