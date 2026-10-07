#Requires -Version 5.1
<#
.SYNOPSIS
Runs only offline self-tests and synthetic WPF preview rendering.
.PARAMETER ExePath
Executable file path; relative paths are resolved from the repository root.
.PARAMETER ArtifactFolder
Parent folder for a fresh synthetic run directory containing reports and PNGs.
.PARAMETER TimeoutSeconds
Maximum runtime for each child process, from 1 to 60 seconds.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ExePath = 'bin\PhotoImport.exe',
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactFolder = 'artifacts',
    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if (-not [IO.Path]::IsPathRooted($ExePath)) {
    $ExePath = Join-Path $projectRoot $ExePath
}
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw ('Executable not found. Run scripts/build.ps1 first: {0}' -f $ExePath)
}
if (-not [IO.Path]::IsPathRooted($ArtifactFolder)) {
    $ArtifactFolder = Join-Path $projectRoot $ArtifactFolder
}
$ArtifactFolder = [IO.Path]::GetFullPath($ArtifactFolder)
# A new directory prevents reports or previews from a previous run passing checks.
$runFolder = Join-Path $ArtifactFolder ('synthetic-' + [Guid]::NewGuid().ToString('N'))
$previewFolder = Join-Path $runFolder 'previews'
[IO.Directory]::CreateDirectory($previewFolder) | Out-Null
Write-Host ('Synthetic test artifacts: {0}' -f $runFolder)

function Invoke-SyntheticCheck {
    param(
        [ValidateSet('--self-test', '--render-previews')]
        [string]$Switch,
        [string]$TargetPath,
        [string]$ReportPath,
        [int]$ExpectedPassLines
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ExePath
    $startInfo.Arguments = '{0} "{1}"' -f $Switch, $TargetPath
    $startInfo.WorkingDirectory = $runFolder
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) {
            throw ('Could not start {0}.' -f $Switch)
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw ('{0} timed out after {1} seconds.' -f $Switch, $TimeoutSeconds)
        }
        if ($process.ExitCode -ne 0) {
            $failureReport = ''
            if (Test-Path -LiteralPath $ReportPath -PathType Leaf) {
                $failureReport = [IO.File]::ReadAllText($ReportPath)
            }
            throw ('{0} failed with exit code {1}.{2}{3}' -f $Switch, $process.ExitCode, [Environment]::NewLine, $failureReport)
        }
        if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
            throw ('{0} did not produce its report: {1}' -f $Switch, $ReportPath)
        }
        $reportLines = @([IO.File]::ReadAllLines($ReportPath) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $failedLines = @($reportLines | Where-Object { $_ -notmatch '^PASS:' })
        if ($reportLines.Count -ne $ExpectedPassLines -or $failedLines.Count -ne 0) {
            throw ('{0} did not report PASS for every expected suite.{1}{2}' -f $Switch, [Environment]::NewLine, [IO.File]::ReadAllText($ReportPath))
        }
        foreach ($reportLine in $reportLines) {
            Write-Output $reportLine
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill()
            if (-not $process.WaitForExit(5000)) {
                Write-Warning ('Timed-out child process {0} has not exited after termination.' -f $process.Id)
            }
        }
        $process.Dispose()
    }
}

$selfTestReport = Join-Path $runFolder 'self-test.txt'
Invoke-SyntheticCheck -Switch '--self-test' -TargetPath $selfTestReport -ReportPath $selfTestReport -ExpectedPassLines 2
$renderReport = Join-Path $previewFolder 'render-check.txt'
Invoke-SyntheticCheck -Switch '--render-previews' -TargetPath $previewFolder -ReportPath $renderReport -ExpectedPassLines 1
$renderedFiles = @(Get-ChildItem -LiteralPath $previewFolder -Filter '*.png' -File)
if ($renderedFiles.Count -eq 0) {
    throw 'Preview rendering produced no PNG files.'
}
Write-Output ('PASS: {0} synthetic preview PNGs. Artifacts: {1}' -f $renderedFiles.Count, $runFolder)
