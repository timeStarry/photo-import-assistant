#Requires -Version 5.1
<#
.SYNOPSIS
Builds PhotoImport with the C# 5 compiler included in Windows .NET Framework.
.PARAMETER OutputPath
Executable file path; relative paths are resolved from the repository root.
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath = 'bin\PhotoImport.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($env:WINDIR)) {
    throw 'This build requires Windows and .NET Framework 4.8.'
}

$frameworkFolder = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkFolder 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    $frameworkFolder = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compilerPath = Join-Path $frameworkFolder 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found. Install or enable .NET Framework 4.8.'
}

if (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $projectRoot $OutputPath
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($OutputPath) -ine '.exe') {
    throw 'OutputPath must be an executable file path ending in .exe.'
}
$outputFolder = Split-Path -Parent $OutputPath
[IO.Directory]::CreateDirectory($outputFolder) | Out-Null

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/main:PhotoImportV2.Program',
    '/optimize+',
    '/define:TRACE',
    '/codepage:65001',
    '/utf8output',
    '/langversion:5',
    '/warnaserror+',
    '/warn:4',
    ('/out:{0}' -f $OutputPath)
)

$referenceNames = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Web.Extensions.dll',
    'System.Xml.dll',
    'System.Xaml.dll',
    'WindowsBase.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll',
    'WindowsFormsIntegration.dll',
    'UIAutomationClient.dll',
    'UIAutomationTypes.dll'
)
foreach ($referenceName in $referenceNames) {
    $referencePath = Join-Path $frameworkFolder $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        $referencePath = Join-Path (Join-Path $frameworkFolder 'WPF') $referenceName
    }
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw ('Required .NET Framework assembly not found: {0}' -f $referenceName)
    }
    $compilerArguments += '/reference:{0}' -f $referencePath
}

foreach ($resourceName in @('FluentStyles.xaml', 'FluentView.xaml')) {
    $resourcePath = Join-Path (Join-Path $projectRoot 'src') $resourceName
    if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
        throw ('Missing embedded XAML resource: {0}' -f $resourcePath)
    }
    $compilerArguments += '/resource:{0},PhotoImportV2.{1}' -f $resourcePath, $resourceName
}
$iconPath = Join-Path $projectRoot 'assets\app.ico'
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw ('Missing application icon: {0}' -f $iconPath)
}
$compilerArguments += '/win32icon:{0}' -f $iconPath

$sourceFiles = @(
    foreach ($sourceFolder in @('src', 'tests')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceFolder) -Filter '*.cs' -File -Recurse
    }
)
if ($sourceFiles.Count -eq 0) {
    throw 'No C# source files were found in src or tests.'
}
$compilerArguments += @($sourceFiles | Sort-Object FullName | ForEach-Object { $_.FullName })

Write-Host ('Compiling {0} C# files with {1}' -f $sourceFiles.Count, $compilerPath)
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw ('C# compilation failed with exit code {0}.' -f $LASTEXITCODE)
}
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw 'The compiler completed without producing the expected executable.'
}
Write-Output ('Built: {0}' -f $OutputPath)
