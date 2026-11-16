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
 # The service runs as SYSTEM from here: refuse a folder whose parent lets ordinary users
 # delete or replace its children (desktop, downloads, user profile folders and the like).
 $parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Target).TrimEnd('\'))
 while ($parent -and -not (Test-Path -LiteralPath $parent)) {$parent=[IO.Path]::GetDirectoryName($parent)}
 if ($parent) {
  $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464','S-1-3-0')
  $danger=[Security.AccessControl.FileSystemRights]'DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
  foreach ($rule in (Get-Acl -LiteralPath $parent).Access) {
   if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) {continue}
   try {$sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value} catch {continue}
   if ($trusted -contains $sid) {continue}
   if ($rule.FileSystemRights -band $danger) {exit 2}
  }
 }
 exit 0
} catch {Write-Error $_;exit 1}
