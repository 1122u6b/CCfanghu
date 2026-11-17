param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
# A parent PowerShell 7 can leak its module path into Windows PowerShell; use the system one.
$env:PSModulePath=[Environment]::GetEnvironmentVariable('PSModulePath','Machine')
try {
 # Windows 10 / 11 normally ship WebView2 already; only install it when it is missing.
 $id='{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
 foreach ($key in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$id","HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id","HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id")) {
  $pv=(Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv
  if ($pv -and $pv -ne '0.0.0.0') {Write-Output "WebView2 $pv already installed";exit 0}
 }
 Import-Module Microsoft.PowerShell.Security
 $stream=[IO.File]::OpenRead($Installer)
 try {$hash=-join ([Security.Cryptography.SHA256]::Create().ComputeHash($stream) | ForEach-Object {$_.ToString('x2')})} finally {$stream.Dispose()}
 if ($hash -ne $ExpectedSha256.ToLowerInvariant()) {throw 'Runtime checksum mismatch'}
 $signature=Get-AuthenticodeSignature -LiteralPath $Installer
 if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {throw 'Runtime must have a valid Microsoft signature'}
 $process=Start-Process -FilePath $Installer -ArgumentList '/silent','/install' -Wait -PassThru
 if ($process.ExitCode -notin @(0,3010,-2147219198)) {throw "WebView2 installation failed: $($process.ExitCode)"}
 exit 0
} catch {Write-Error $_;exit 1}
