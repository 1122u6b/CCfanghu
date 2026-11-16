$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramW6432 'LEYRAConnect'
$version=Join-Path $root 'versions/0.3.0-rc1'
$client=$null
$receipt=@{installer=$false;service=$false;desktop=$false;uninstall=$false;live_network_acceptance=$false}
try {
 if (Get-Service LeyraConnect -ErrorAction SilentlyContinue) {throw 'Unexpected pre-existing service on disposable runner'}
 $install=Start-Process -FilePath "$pwd/artifacts/LEYRAConnect-0.3.0-rc1-setup.exe" -ArgumentList '/S' -Wait -PassThru
 if ($install.ExitCode -ne 0) {throw 'Installer failed'}
 $receipt.installer=$true
 $service=Get-Service LeyraConnect
 if ($service.Status -ne 'Running') {throw 'Installed service is not running'}
 $receipt.service=$true
 if (!(Test-Path (Join-Path $version 'client-assets/win/index.html'))) {throw 'Installed UI assets missing'}
 $client=Start-Process -FilePath (Join-Path $version 'LeyraConnect.exe') -PassThru
 $deadline=[DateTime]::UtcNow.AddSeconds(25)
 while ([DateTime]::UtcNow -lt $deadline) {
  $client.Refresh()
  if ($client.HasExited) {throw 'Desktop exited during startup'}
  if ($client.MainWindowHandle -ne 0) {break}
  Start-Sleep -Milliseconds 500
 }
 if ($client.MainWindowHandle -eq 0) {throw 'Desktop did not create a window'}
 $receipt.desktop=$true
 Stop-Process -Id $client.Id -Force
 $client.WaitForExit(5000) | Out-Null
 $remove=Start-Process -FilePath (Join-Path $root 'Uninstall.exe') -ArgumentList '/S' -Wait -PassThru
 if ($remove.ExitCode -ne 0) {throw 'Uninstaller failed'}
 $deadline=[DateTime]::UtcNow.AddSeconds(20)
 while ((Get-Service LeyraConnect -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Milliseconds 500}
 if (Get-Service LeyraConnect -ErrorAction SilentlyContinue) {throw 'Service remains after uninstall'}
 $receipt.uninstall=$true
} finally {
 $receipt | ConvertTo-Json | Set-Content artifacts/windows-install-receipt.json -Encoding utf8
 if ($client -and !$client.HasExited) {Stop-Process -Id $client.Id -Force}
}
