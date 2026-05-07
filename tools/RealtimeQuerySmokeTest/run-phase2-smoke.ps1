param(
    [string]$OutputRoot = "data/architecture-validation/phase2",
    [string]$Label = "realtime-query-smoke"
)

function Get-RepoRoot {
    $dir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    while (-not [string]::IsNullOrWhiteSpace($dir)) {
        $candidate = Join-Path $dir "DH.sln"
        if (Test-Path $candidate) {
            return $dir
        }

        $parent = Split-Path -Parent $dir
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $dir) {
            break
        }

        $dir = $parent
    }

    return (Get-Location).Path
}

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path (Get-RepoRoot) $OutputRoot
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $OutputRoot $timestamp
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$summaryPath = Join-Path $runDir "summary.log"
$validationPath = Join-Path $runDir "validation.log"
$ErrorActionPreference = "Stop"
$stdoutPath = Join-Path $runDir "stdout.tmp.log"
$stderrPath = Join-Path $runDir "stderr.tmp.log"
$buildStdoutPath = Join-Path $runDir "build.stdout.tmp.log"
$buildStderrPath = Join-Path $runDir "build.stderr.tmp.log"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$toolDll = "tools\RealtimeQuerySmokeTest\bin\Debug\net6.0-windows7.0\RealtimeQuerySmokeTest.dll"
$toolProject = "tools\RealtimeQuerySmokeTest\RealtimeQuerySmokeTest.csproj"

$buildProcess = Start-Process `
    -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
    -ArgumentList @('build', $toolProject, '-c', 'Debug', '-nologo', '-v', 'minimal') `
    -NoNewWindow `
    -Wait `
    -PassThru `
    -RedirectStandardOutput $buildStdoutPath `
    -RedirectStandardError $buildStderrPath

if ($buildProcess.ExitCode -ne 0) {
    if (Test-Path $buildStdoutPath) { Get-Content -Path $buildStdoutPath -Encoding utf8 | ForEach-Object { Write-Host $_ } }
    if (Test-Path $buildStderrPath) { Get-Content -Path $buildStderrPath -Encoding utf8 | ForEach-Object { Write-Host $_ } }
    throw "Failed to build RealtimeQuerySmokeTest."
}

if (Test-Path $buildStdoutPath) { Remove-Item $buildStdoutPath -Force }
if (Test-Path $buildStderrPath) { Remove-Item $buildStderrPath -Force }

if (-not (Test-Path $toolDll)) {
    throw "Missing tool binary: $toolDll. Build RealtimeQuerySmokeTest first."
}

$process = Start-Process `
    -FilePath 'C:\Program Files\dotnet\dotnet.exe' `
    -ArgumentList @($toolDll, '--output-dir', $runDir) `
    -NoNewWindow `
    -Wait `
    -PassThru `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath

$exitCode = $process.ExitCode
$stdout = if (Test-Path $stdoutPath) { Get-Content -Path $stdoutPath -Encoding utf8 } else { @() }
$stderr = if (Test-Path $stderrPath) { Get-Content -Path $stderrPath -Encoding utf8 } else { @() }
$output = @($stdout + $stderr)

$output | Set-Content -Path $summaryPath -Encoding utf8
$output | ForEach-Object { Write-Host $_ }

if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }
if (Test-Path $stderrPath) { Remove-Item $stderrPath -Force }

if ($exitCode -eq 0) {
    "status=passed" | Out-File -FilePath $validationPath -Encoding utf8
} else {
    "status=failed" | Out-File -FilePath $validationPath -Encoding utf8
}

"label=$Label" | Add-Content -Path $validationPath
"runDir=$runDir" | Add-Content -Path $validationPath
"summary=$summaryPath" | Add-Content -Path $validationPath
