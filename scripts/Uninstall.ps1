$ErrorActionPreference = 'Stop'
$taskName = 'LockScreenNotificationBridge'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\LockScreenNotificationBridge'
$logRoot = Join-Path $env:LOCALAPPDATA 'LockScreenNotificationBridge'
$installedExe = Join-Path $installRoot 'LockScreenNotificationBridge.exe'
Get-CimInstance Win32_Process -Filter "Name='LockScreenNotificationBridge.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $installedExe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
if (Test-Path -LiteralPath $logRoot) { Remove-Item -LiteralPath $logRoot -Recurse -Force }
Write-Host 'LockScreen Notification Bridge has been removed for the current Windows user.'
