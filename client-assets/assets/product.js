(function () {
  'use strict';
  var P = window.__proto;
  if (!P || !window.__TAURI__) throw new Error('客户端桥接未加载');
  P.ROUTES.length = 0;
  P.ROUTES.push({ n: '-', tag: '尚未取得线路', desc: '', ms: null, ok: null, speed: null, sig: 0 });
  P.PM.unbound = { short: '本设备已停用', desc: '请联系管理员检查设备状态。', btns: [['secondary', '提交工单', 'newTicket']] };
  P.S.browsers = [];
  P.TTYPES.forEach(function (t) { if (/授权码|激活/.test(JSON.stringify(t))) { t.n = '设备连接与重装'; t.help = [['导入设备连接文件', 'connectServer']]; t.q = '是更换电脑，还是重装系统？'; } });
  P.A.demo = function () {};
  P.A.connectServer = function () {
    P.S.busy.pair = { text: '正在校验连接文件' }; P.render();
    window.__TAURI__.core.invoke('import_server').then(function () {
      delete P.S.busy.pair; P.toast('连接文件已保存，正在读取服务器状态'); P.render();
    }).catch(function (e) { delete P.S.busy.pair; P.toast(e.message || String(e), 'info'); P.render(); });
  };
  // Native notifications and actual service replies retain ownership of status.
})();
