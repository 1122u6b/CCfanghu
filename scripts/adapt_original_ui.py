"""One-time adaptation already applied to the user-owned recovered Windows UI.
Kept as a reproducible transform; do not run a second time on adapted files.
"""
from pathlib import Path
import re,json
root=Path(__file__).resolve().parents[1]
p=root/'client-assets/win/assets/ui.js';s=p.read_text()
if 'LEYRA_FREE_SETUP' in s:raise SystemExit('UI already adapted')
s=s.replace("  var CHANNELS = "+s.split('  var CHANNELS = ',1)[1].split('\n',1)[0], '  var CHANNELS = [];')
s=s.replace("function btn(kind, label, act, attrs) { return", "function btn(kind, label, act, attrs) { if (!window.LEYRA_FEATURES.licenseCodes && (/switchCode|restoreCode|upgradeCode/.test(act) || /授权码/.test(label))) return ''; return")
# Remove group entry points entirely; support remains available through tickets.
s=re.sub(r"  function qqChan\(\) \{[^\n]+\}", "  function qqChan() { return CHANNELS.filter(function (c) { return c[2] !== 'qqgroup'; })[0] || null; }",s)
start=s.index('  function qqCard() {');end=s.index('  function supportCard(',start)
s=s[:start]+"  function qqCard() { return ''; }\n"+s[end:]
s=s.replace('class="p-datagrid three"','class="p-datagrid two"')
start=s.index('  function groupCard() {');end=s.index('\n\n',start)
s=s[:start]+"  function groupCard() { return ''; }"+s[end:]
start=s.index('  function activateScreen() {');end=s.index('  function stepper(',start)
s=s[:start]+'''  function activateScreen() {
    // LEYRA_FREE_SETUP: imports a device connection profile, not a license code.
    return '<div class="p-full"><div class="p-wordmark" style="margin-bottom:20px"><span class="p-logo" aria-hidden="true"></span>LEYRA</div><div class="p-act"><div class="hx-card"><h1 class="hx-title">连接你的服务器</h1><p class="p-sub">选择管理员提供的服务器连接文件，客户端会校验证书并登记这台设备。</p>' + btn('primary', '导入服务器连接文件', 'connectServer') + '<p class="p-sub">连接文件包含设备凭据，请妥善保管。</p></div></div></div>';
  }
'''+s[end:]
# The legacy vendor's contract and privacy assertions do not describe this service.
for var,value in [
 ('DISCLAIMER', [['使用说明','客户端连接你配置的自有服务器。界面上的状态以本机实际检查结果为准。'],['状态说明','暂停防护或防护组件不可用时，请留意界面的网络状态提示。'],['设备凭据','连接文件属于当前设备，请不要向其他人分享。']]),
 ('TERMS',[['数据与服务器','设备记录、消息和工单由你配置的服务器保存。'],['操作影响','连接、暂停、恢复和卸载可能改变本机的网络状态。'],['服务说明','客户套餐、计费、退款和数据保存政策由服务运营方另行制定并公布。']]),
 ('GUIDES',[['连接与首次使用','导入服务器连接文件，核对设备和服务器信息。'],['暂停防护','暂停期间请留意网络状态提示。'],['网络恢复','遇到网络异常时使用设置中的网络恢复工具。'],['重装系统后连接','向管理员取得当前设备的连接文件后重新导入。']])]:
 start=s.index('  var '+var+' = [');end=s.index('\n  ];',start)+len('\n  ];')
 s=s[:start]+'  var '+var+' = '+json.dumps(value,ensure_ascii=False)+';'+s[end:]
s=s.replace('  function demoView() {', "  function demoView() { return ''; }\n  function unusedLegacyDemoView() {")
s=s.replace('cc-hahaeasy','LEYRA')
p.write_text(s)
p=root/'client-assets/assets/bridge.js';s=p.read_text()
s=s.replace("(a.channels || []).forEach(function (c)","(a.channels || []).filter(function (c) { return c.kind !== 'qqgroup'; }).forEach(function (c)")
p.write_text(s)
p=root/'client-assets/win/index.html';s=p.read_text().replace('<script src="../assets/bridge.js"></script>','<script src="../assets/bridge.js"></script>\n<script src="../assets/product.js"></script>');p.write_text(s)
