(function () {
  'use strict';
  var next = 0, pending = Object.create(null);
  function invoke(command, args) {
    if (!window.chrome || !window.chrome.webview) return Promise.reject(new Error('请在 Windows 客户端中打开'));
    return new Promise(function (resolve, reject) {
      var id = ++next;
      var req = args && args.request, deep = command === 'daemon' && req && req.type === 'doctor' && req.deep;
      var interactive = command === 'verify_owner' || command === 'import_server' || command === 'run_rescue' || (req && /^(pause|pair|add_direct_rule|block_before_ready)$/.test(req.type));
      var slow = req && /^(activate|restore|own_exit|switch_code)$/.test(req.type);
      var timer = setTimeout(function () { delete pending[id]; reject(new Error('本机操作超时，请查看后台服务状态')); }, interactive ? 300000 : deep ? 110000 : slow ? 105000 : 40000);
      pending[id] = { resolve: resolve, reject: reject, timer: timer };
      window.chrome.webview.postMessage({ id: id, command: command, args: args || {} });
    });
  }
  if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', function (event) {
    var data = event.data, item = data && pending[data.id];
    if (!item) return;
    delete pending[data.id]; clearTimeout(item.timer);
    if (data.ok === true) item.resolve(data.value); else item.reject(new Error(data.error || '本机操作失败'));
  });
  window.LEYRA_FEATURES = Object.freeze({ licenseCodes: true, supportGroup: true });
  window.__TAURI__ = { core: { invoke: invoke }, app: { getVersion: function () { return invoke('app_version'); } } };
  document.documentElement.classList.add('app');
})();
