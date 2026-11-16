param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
# A parent PowerShell 7 can leak its module path into Windows PowerShell; use the system one.
$env:PSModulePath=[Environment]::GetEnvironmentVariable('PSModulePath','Machine')
try {
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
