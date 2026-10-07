[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\PhotoImport.exe'),
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$taskName = 'PhotoImport-Agent'
$exe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build PhotoImport.exe before installing.' }
$dataRoot = Join-Path $env:LOCALAPPDATA 'PhotoImport'
$backups = Join-Path $dataRoot 'deployment-backups'
New-Item -ItemType Directory -Path $backups -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$old = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($old) {
    if ($old.State -eq 'Running') { throw 'Exit the running assistant from its tray menu before updating the installation.' }
    Export-ScheduledTask -TaskName $taskName | Set-Content -LiteralPath (Join-Path $backups "PhotoImport-Agent-$stamp.xml") -Encoding Unicode
}
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -Argument '--startup' -WorkingDirectory (Split-Path $exe -Parent)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$trigger.Delay = 'PT10S'
$stateFile = Join-Path $dataRoot 'state.json'
if (Test-Path -LiteralPath $stateFile) {
    $savedSettings = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $savedSettings.StartAtLogin) { $trigger.Enabled = [bool]$savedSettings.StartAtLogin }
}
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Photo Import Assistant: interactive-user tray app, registered cards, verified import.' -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
    $shortcutPath = Join-Path $folder '相机导入助手.lnk'
    if (Test-Path -LiteralPath $shortcutPath) {
        Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backups ("shortcut-$stamp-" + [Guid]::NewGuid().ToString('N') + '.lnk'))
    }
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe -Parent
    $shortcut.Description = 'Open Photo Import Assistant.'
    $shortcut.IconLocation = $exe + ',0'
    $shortcut.Save()
}
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }
Get-ScheduledTask -TaskName $taskName | Select-Object TaskName,State,@{n='Executable';e={$_.Actions.Execute}}
