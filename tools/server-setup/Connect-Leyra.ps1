$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
try {
 $keygen=Join-Path $env:WINDIR 'System32\OpenSSH\ssh-keygen.exe'
 if (-not (Test-Path -LiteralPath $keygen)) {throw 'Windows 未安装 OpenSSH 客户端。请把这个提示发给我，我会提供适配方式。'}
 $directory=Join-Path $env:LOCALAPPDATA 'LeyraConnectSetup'
 [IO.Directory]::CreateDirectory($directory) | Out-Null
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $acl.SetOwner($sid)
 foreach ($identity in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
  $rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
 }
 Set-Acl -LiteralPath $directory -AclObject $acl
 $privateKey=Join-Path $directory 'leyra-deploy-ed25519'
 if (-not (Test-Path -LiteralPath $privateKey)) {
  $start=New-Object Diagnostics.ProcessStartInfo
  $start.FileName=$keygen
  $start.Arguments='-t ed25519 -a 64 -N "" -C "leyra-temporary-deploy" -f "'+$privateKey+'"'
  $start.UseShellExecute=$false
  $start.CreateNoWindow=$true
  $start.RedirectStandardOutput=$true
  $start.RedirectStandardError=$true
  $process=[Diagnostics.Process]::Start($start)
  $stdout=$process.StandardOutput.ReadToEndAsync()
  $stderr=$process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit(20000)) {$process.Kill();throw '生成部署密钥超时。'}
  if ($process.ExitCode -ne 0) {throw '部署密钥生成失败，请把这个提示发给我。'}
  $process.Dispose()
 }
 $public=[IO.File]::ReadAllText($privateKey+'.pub').Trim()
 if ($public -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+(?: [A-Za-z0-9_.-]+)?$') {throw '部署公钥格式不正确，请把这个提示发给我。'}
 $expires=[DateTime]::UtcNow.AddDays(7).ToString('yyyyMMddHHmmss')+'Z'
 $authorized='restrict,expiry-time="'+$expires+'" '+$public
 # Only the public key is put in the server command. No password or private key is sent to the terminal.
 $serverScript=@'
set -eu
umask 077
leyra_entry="$(getent passwd ubuntu)"
leyra_home="$(printf '%s' "$leyra_entry" | cut -d: -f6)"
case "$leyra_home" in /home/*) ;; *) printf 'Unexpected Ubuntu home directory\n' >&2; exit 1;; esac
leyra_dir="$leyra_home/.ssh"
if [ -L "$leyra_dir" ] || [ -L "$leyra_dir/authorized_keys" ]; then printf 'Linked SSH path rejected\n' >&2; exit 1; fi
install -d -m 700 -o ubuntu "$leyra_dir"
touch "$leyra_dir/authorized_keys"
chmod 600 "$leyra_dir/authorized_keys"
chown ubuntu "$leyra_dir/authorized_keys"
leyra_key='__PUBLIC_KEY__'
grep -qxF "$leyra_key" "$leyra_dir/authorized_keys" || printf '%s\n' "$leyra_key" >> "$leyra_dir/authorized_keys"
printf '\nLEYRA_HOSTKEY '
cat /etc/ssh/ssh_host_ed25519_key.pub | cut -d ' ' -f 1,2
'@
 $serverScript=$serverScript.Replace('__PUBLIC_KEY__',$authorized)
 $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($serverScript))
 $serverCommand="printf '%s' '"+$encoded+"' | base64 -d | sudo -n bash"
 $form=New-Object Windows.Forms.Form
 $form.Text='LEYRA 一次性服务器连接设置'
 $form.ClientSize=[Drawing.Size]::new(760,510)
 $form.StartPosition='CenterScreen'
 $form.FormBorderStyle='FixedDialog'
 $form.MaximizeBox=$false
 $form.Font=[Drawing.Font]::new('Microsoft YaHei UI',10)
 $title=New-Object Windows.Forms.Label
 $title.Text='只做一次连接设置，后续部署和联调由我处理'
 $title.SetBounds(24,20,720,32)
 $title.Font=[Drawing.Font]::new('Microsoft YaHei UI',13,[Drawing.FontStyle]::Bold)
 $form.Controls.Add($title)
 $instructions=New-Object Windows.Forms.Label
 $instructions.Text="1. 点下面的按钮复制服务器命令，粘贴到腾讯云这台服务器的网页终端，按回车。`r`n`r`n2. 复制 GitHub 密钥并打开设置页面，Name 填 LEYRA_SSH_KEY，Secret 粘贴后保存。`r`n`r`n3. 把终端输出的 LEYRA_HOSTKEY 开头那一整行发回聊天。它是服务器公钥。"
 $instructions.SetBounds(24,70,710,152)
 $form.Controls.Add($instructions)
 function Add-Button([string]$text,[int]$left,[int]$top,[int]$width,[scriptblock]$action) {
  $button=New-Object Windows.Forms.Button
  $button.Text=$text
  $button.SetBounds($left,$top,$width,42)
  $button.Add_Click($action)
  $form.Controls.Add($button)
 }
 $status=New-Object Windows.Forms.Label
 $status.Text='密钥已在本机生成，尚未连接服务器。'
 $status.SetBounds(24,360,710,42)
 $status.ForeColor=[Drawing.Color]::FromArgb(30,80,160)
 $form.Controls.Add($status)
 Add-Button '① 复制腾讯云终端命令' 24 236 338 {
  [Windows.Forms.Clipboard]::SetText($serverCommand)
  $status.Text='服务器命令已复制。请在腾讯云网页终端粘贴并按回车。'
 }
 Add-Button '打开腾讯云服务器列表' 384 236 338 {
  Start-Process 'https://console.cloud.tencent.com/lighthouse/instance/index'
 }
 Add-Button '② 复制 GitHub 部署密钥' 24 292 338 {
  [Windows.Forms.Clipboard]::SetText([IO.File]::ReadAllText($privateKey))
  $status.Text='部署密钥已复制。请粘贴到 GitHub 的 Secret 框，保存后复制其他文字覆盖剪贴板。'
 }
 Add-Button '打开 GitHub 密钥设置' 384 292 338 {
  Start-Process 'https://github.com/1122u6b/CCfanghu/settings/secrets/actions/new'
 }
 $note=New-Object Windows.Forms.Label
 $note.Text="GitHub Name：LEYRA_SSH_KEY`r`n临时公钥有效期 7 天；原有 SSH 密钥、防火墙和业务文件不会被这条设置命令删除。`r`n部署密钥只放到 GitHub Secret，不要粘贴到聊天中。"
 $note.SetBounds(24,411,710,80)
 $note.ForeColor=[Drawing.Color]::DimGray
 $form.Controls.Add($note)
 [void]$form.ShowDialog()
} catch {
 [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'LEYRA 连接设置','OK','Error')
 exit 1
}
