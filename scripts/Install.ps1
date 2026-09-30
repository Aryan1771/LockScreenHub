param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$exe = Join-Path $source 'LockScreenNotificationBridge.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Published executable not found: $exe" }

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\LockScreenNotificationBridge'
$taskName = 'LockScreenNotificationBridge'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $installRoot -Recurse -Force
$installedExe = Join-Path $installRoot 'LockScreenNotificationBridge.exe'
$action = New-ScheduledTaskAction -Execute $installedExe -WorkingDirectory $installRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'Runs the per-user LockScreen Notification Bridge in the background.' -RunLevel Limited | Out-Null
Write-Host 'Installed for the current Windows user. It starts at the next sign-in.'
