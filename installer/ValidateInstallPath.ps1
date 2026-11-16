param([Parameter(Mandatory=$true)][string]$Target)
$ErrorActionPreference='Stop'
$env:PSModulePath=[Environment]::GetEnvironmentVariable('PSModulePath','Machine')
try {
 $ancestor=[IO.DirectoryInfo]::new($Target)
 while ($null -ne $ancestor) {
  if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Linked install ancestor rejected'}
  $ancestor=$ancestor.Parent
 }
 $stack=@($Target)
 while ($stack.Count) {
  $current=$stack[0];$stack=@($stack | Select-Object -Skip 1)
  if (-not (Test-Path -LiteralPath $current)) {continue}
  $item=Get-Item -LiteralPath $current -Force
  if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Linked install path rejected'}
  if ($item.PSIsContainer) {$stack+=@(Get-ChildItem -LiteralPath $current -Force | ForEach-Object {$_.FullName})}
 }
 exit 0
} catch {Write-Error $_;exit 1}
