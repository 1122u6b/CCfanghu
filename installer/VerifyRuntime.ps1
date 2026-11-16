param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
try {
 if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne $ExpectedSha256) {throw 'Runtime checksum mismatch'}
 $signature=Get-AuthenticodeSignature -LiteralPath $Installer
 if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {throw 'Runtime must have a valid Microsoft signature'}
 $process=Start-Process -FilePath $Installer -ArgumentList '/silent','/install' -Wait -PassThru
 if ($process.ExitCode -notin @(0,3010,-2147219198)) {throw "WebView2 installation failed: $($process.ExitCode)"}
 exit 0
} catch {Write-Error $_;exit 1}
