(function () {
  'use strict';
  var P = window.__proto;
  if (!P || !window.__TAURI__) throw new Error('客户端桥接未加载');
  P.ROUTES.length = 0;
  P.ROUTES.push({ n: '-', tag: '尚未取得线路', desc: '', ms: null, ok: null, speed: null, sig: 0 });
  P.PM.unbound = { short: '本设备已停用', desc: '这台电脑的授权已停用或已在其他电脑上使用。请在售后 QQ 群联系客服。', btns: [['primary', '填原来的授权码', 'dialog:restoreCode'], ['secondary', '提交工单', 'newTicket']] };
  // 授权到期：网络已断开保护真实 IP；在售后群买新的授权码，填进来就顺延。
  Object.defineProperty(P.PM, 'expired', { configurable: true, enumerable: true, value: { short: '授权已到期', get desc() { return '授权码已于 ' + (P.S.exp && P.S.exp.date ? P.S.exp.date : '') + '到期。为了不暴露真实 IP，网络已断开。请在售后 QQ 群购买新的授权码，点「续费」填入即可恢复。'; }, btns: [['primary', '续费', 'dialog:switchCode'], ['secondary', '提交工单', 'newTicket']] } });
  P.S.browsers = [];
  P.A.demo = function () {};
  // 续费一律是填新的授权码（在售后群下单）。
  P.A.renew = P.A.renewWeb = P.A.renewOpen = P.A.upgrade = P.A.buySoft = function () { P.S.tray = false; P.A.dialog('switchCode'); };
  P.A.switchCodeGo = function () {
    var D = P.S.dialog, el = document.querySelector('[data-keep="switch-code"]'), c = el ? el.value.trim() : '';
    if (!c) { D.err = '先填授权码'; P.render(); return; }
    D.busy = true; D.err = ''; P.render();
    window.__TAURI__.core.invoke('daemon', { request: { type: 'switch_code', code: c } }).then(function (r) {
      P.S.dialog = null; var d = r && r.expires_at ? new Date(r.expires_at * 1000) : null;
      P.toast('续费成功' + (d ? '，新的到期日 ' + (d.getMonth() + 1) + ' 月 ' + d.getDate() + ' 日' : '')); P.render();
      if (window.CC_RELOAD_ACCOUNT) window.CC_RELOAD_ACCOUNT();
    }).catch(function (e) { if (P.S.dialog) { P.S.dialog.busy = false; P.S.dialog.err = e && e.message ? e.message : String(e); } P.render(); });
  };
  P.A.connectServer = function () {
    P.S.busy.pair = { text: '正在校验连接文件' }; P.render();
    window.__TAURI__.core.invoke('import_server').then(function () {
      delete P.S.busy.pair; P.toast('连接文件已保存，正在读取服务器状态'); P.render();
    }).catch(function (e) { delete P.S.busy.pair; P.toast(e.message || String(e), 'info'); P.render(); });
  };
})();
