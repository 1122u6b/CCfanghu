$ErrorActionPreference='Stop'
try {
 $target=Join-Path $env:ProgramW6432 'LEYRAConnect'
 $stack=@($target)
 while ($stack.Count) {
  $current=$stack[0];$stack=@($stack | Select-Object -Skip 1)
  if (-not (Test-Path -LiteralPath $current)) {continue}
  $item=Get-Item -LiteralPath $current -Force
  if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Linked install path rejected'}
  if ($item.PSIsContainer) {$stack+=@(Get-ChildItem -LiteralPath $current -Force | ForEach-Object {$_.FullName})}
 }
 exit 0
} catch {Write-Error $_;exit 1}
