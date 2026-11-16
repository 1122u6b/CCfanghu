/* cc-hahaeasy 组件辅助脚本：图标、状态图形、四主题预览画布和几个小交互。不依赖任何库，不联网。 */
(function () {
  'use strict';

  /* 线性描边图标，24×24 画布，描边 1.5px（vector-effect 保持任何尺寸下都是 1.5px）。 */
  var ICONS = {
    shield: '<path d="M12 3l7 3v5.2c0 4.4-2.9 8.1-7 9.8-4.1-1.7-7-5.4-7-9.8V6l7-3z"/><path d="M9 12l2.2 2.2L15.2 10"/>',
    'shield-off': '<path d="M12 3l7 3v5.2c0 4.4-2.9 8.1-7 9.8-4.1-1.7-7-5.4-7-9.8V6l7-3z"/><path d="M4 4l16 16"/>',
    'shield-plain': '<path d="M12 3l7 3v5.2c0 4.4-2.9 8.1-7 9.8-4.1-1.7-7-5.4-7-9.8V6l7-3z"/>',
    pause: '<path d="M9 6v12"/><path d="M15 6v12"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
    refresh: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3"/><path d="M19.5 4.5v4h-4"/>',
    signal: '<path d="M5 19v-3"/><path d="M10 19v-6"/><path d="M15 19v-9"/><path d="M20 19V6"/>',
    pulse: '<path d="M3 12h4l2.2-5 4.6 10 2.2-5h5"/>',
    user: '<circle cx="12" cy="8.5" r="3.8"/><path d="M4.5 20c1.4-3.4 4.2-5 7.5-5s6.1 1.6 7.5 5"/>',
    chat: '<path d="M4.5 5.5h15v10.5h-9l-4.5 3.5v-3.5H4.5z"/><path d="M8.5 10h7"/><path d="M8.5 12.8h4.5"/>',
    settings: '<path d="M18.86 10.61L21.12 10.81L21.12 13.19L18.86 13.39L17.83 15.87L19.29 17.61L17.61 19.29L15.87 17.83L13.39 18.86L13.19 21.12L10.81 21.12L10.61 18.86L8.13 17.83L6.39 19.29L4.71 17.61L6.17 15.87L5.14 13.39L2.88 13.19L2.88 10.81L5.14 10.61L6.17 8.13L4.71 6.39L6.39 4.71L8.13 6.17L10.61 5.14L10.81 2.88L13.19 2.88L13.39 5.14L15.87 6.17L17.61 4.71L19.29 6.39L17.83 8.13Z"/><circle cx="12" cy="12" r="2.8"/>',
    copy: '<rect x="8.5" y="8.5" width="11" height="11" rx="2"/><path d="M15.5 8.5V6a1.5 1.5 0 0 0-1.5-1.5H6A1.5 1.5 0 0 0 4.5 6v8A1.5 1.5 0 0 0 6 15.5h2.5"/>',
    external: '<path d="M13.5 4.5h6v6"/><path d="M19.5 4.5l-8.5 8.5"/><path d="M17.5 13.5v4.5a1.5 1.5 0 0 1-1.5 1.5H6A1.5 1.5 0 0 1 4.5 18V8A1.5 1.5 0 0 1 6 6.5h4.5"/>',
    warning: '<path d="M12 4l8.5 15h-17z"/><path d="M12 10v4"/><path d="M12 16.8v.2"/>',
    info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5"/><path d="M12 7.8v.2"/>',
    close: '<path d="M6.5 6.5l11 11"/><path d="M17.5 6.5l-11 11"/>',
    plus: '<path d="M12 5v14"/><path d="M5 12h14"/>',
    chevron: '<path d="M6.5 9.5l5.5 5.5 5.5-5.5"/>',
    download: '<path d="M12 4.5v10.5"/><path d="M7.5 10.5l4.5 4.5 4.5-4.5"/><path d="M5 19.5h14"/>',
    chip: '<rect x="6.5" y="6.5" width="11" height="11" rx="2"/><rect x="9.5" y="9.5" width="5" height="5" rx="1"/><path d="M9.5 3.5v3M14.5 3.5v3M9.5 17.5v3M14.5 17.5v3M3.5 9.5h3M3.5 14.5h3M17.5 9.5h3M17.5 14.5h3"/>',
    windows: '<rect x="4.5" y="4.5" width="6.5" height="6.5" rx="1"/><rect x="13" y="4.5" width="6.5" height="6.5" rx="1"/><rect x="4.5" y="13" width="6.5" height="6.5" rx="1"/><rect x="13" y="13" width="6.5" height="6.5" rx="1"/>',
    laptop: '<rect x="5" y="5.5" width="14" height="9.5" rx="1.5"/><path d="M3 18.5h18"/>',
    server: '<rect x="4.5" y="4.5" width="15" height="6.5" rx="1.5"/><rect x="4.5" y="13" width="15" height="6.5" rx="1.5"/><path d="M8 7.8h.2M8 16.2h.2"/>',
    globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17"/><path d="M12 3.5c2.3 2.4 3.5 5.2 3.5 8.5s-1.2 6.1-3.5 8.5c-2.3-2.4-3.5-5.2-3.5-8.5s1.2-6.1 3.5-8.5z"/>',
    lock: '<rect x="5" y="10.5" width="14" height="9.5" rx="2"/><path d="M8.5 10.5V8a3.5 3.5 0 0 1 7 0v2.5"/>',
    key: '<circle cx="8.5" cy="15.5" r="3.8"/><path d="M11.2 12.8l8-8"/><path d="M16.2 7.8l2.3 2.3"/><path d="M13.9 10.1l1.8 1.8"/>',
    medkit: '<rect x="3.5" y="7" width="17" height="12.5" rx="2"/><path d="M9 7V5h6v2"/><path d="M12 10.5v5.5"/><path d="M9.25 13.25h5.5"/>',
    /* 以下为组件需要而补充的图标 */
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4 4"/>',
    menu: '<path d="M4.5 7h15"/><path d="M4.5 12h15"/><path d="M4.5 17h15"/>',
    eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="2.8"/>',
    calendar: '<rect x="4.5" y="5.5" width="15" height="14" rx="2"/><path d="M4.5 10h15"/><path d="M8.5 3.5v4M15.5 3.5v4"/>',
    bell: '<path d="M6.5 16v-5a5.5 5.5 0 0 1 11 0v5l1.5 2h-14z"/><path d="M10 20.5h4"/>',
    help: '<circle cx="12" cy="12" r="8.5"/><path d="M9.7 9.6a2.4 2.4 0 0 1 4.7.7c0 1.7-2.4 2.1-2.4 3.7"/><path d="M12 16.9v.2"/>',
    wallet: '<rect x="3.5" y="6.5" width="17" height="12.5" rx="2.5"/><path d="M3.5 10.5h17"/><path d="M15.5 15h2"/>',
    book: '<path d="M5 18.5V6a2 2 0 0 1 2-2h12v13H7a2 2 0 0 0-2 2 2 2 0 0 0 2 2h12"/>',
    sort: '<path d="M8 5v14"/><path d="M5 8l3-3 3 3"/><path d="M16 19V5"/><path d="M13 16l3 3 3-3"/>',
    sun: '<circle cx="12" cy="12" r="3.8"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4"/>',
    moon: '<path d="M19.5 14.5A7.5 7.5 0 0 1 9.5 4.5a7.5 7.5 0 1 0 10 10z"/>'
  };
  var ICON_NAMES = {
    shield: '盾牌', 'shield-off': '盾牌加斜线', 'shield-plain': '空盾牌', pause: '暂停', clock: '时钟', refresh: '刷新',
    signal: '线路（信号格）', pulse: '安全检查（心电）', user: '个人中心', chat: '工单（对话）', settings: '设置', copy: '复制', external: '外链',
    warning: '警告三角', info: '信息圆', close: '关闭', chevron: '展开收起', download: '下载', chip: 'Mac 芯片', windows: 'Windows 窗标',
    laptop: '电脑', server: '服务器', globe: '地球', lock: '锁', key: '钥匙', medkit: '恢复工具（急救箱）',
    check: '对勾', search: '搜索', menu: '菜单', eye: '显示', calendar: '日历', bell: '铃铛', sort: '排序', sun: '太阳', moon: '月亮'
  };

  function icon(name, size, cls, label) {
    size = size || 20;
    var a11y = label ? ' role="img" aria-label="' + label + '"' : ' aria-hidden="true"';
    return '<svg class="hx-icon ' + (cls || '') + '" width="' + size + '" height="' + size + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"' + a11y + '>' + (ICONS[name] || '') + '</svg>';
  }

  var THEMES = [
    { id: 'tech-dark', fam: 'tech', dark: true, label: '科技 · 深色', note: '科技主题默认 · 官网默认' },
    { id: 'tech-light', fam: 'tech', dark: false, label: '科技 · 浅色', note: '管理后台 · 官网浅色' },
    { id: 'warm-light', fam: 'warm', dark: false, label: '亲切 · 浅色', note: '亲切主题默认' },
    { id: 'warm-dark', fam: 'warm', dark: true, label: '亲切 · 深色', note: '' }
  ];

  /* 四组配色并排渲染同一个组件。opts: min（每格最小宽度）、use（这个组件实际使用的主题 id 列表）、
     useLabel（使用方名称）、ground（window | bg | sidebar | flush）。 */
  function themes(render, opts) {
    opts = opts || {};
    var root = document.getElementById('root');
    var use = opts.use || null;
    var html = '<div class="hx-sheet" style="--hx-min:' + (opts.min || 360) + 'px">';
    THEMES.forEach(function (t) {
      if (opts.only && opts.only.indexOf(t.id) < 0) return;
      var tag = '';
      if (use) tag = use.indexOf(t.id) >= 0 ? '<span class="hx-panel-use">' + ((opts.useLabel && typeof opts.useLabel === 'object') ? opts.useLabel[t.id] : (opts.useLabel || '使用')) + '</span>' : '<span>仅供参照</span>';
      else if (t.note) tag = '<span>' + t.note + '</span>';
      var g = opts.ground || 'window';
      var cls = g === 'bg' ? ' on-bg' : g === 'sidebar' ? ' on-sidebar' : g === 'flush' ? ' flush' : '';
      html += '<section class="hx-panel" data-theme="' + t.id + '"><div class="hx-panel-label"><b>' + t.label + '</b>' + tag + '</div><div class="hx-panel-body' + cls + '">' + render(t) + '</div></section>';
    });
    html += '</div>';
    root.innerHTML = html;
    startCountdowns(root);
    if (opts.reveal) reveal(root);
  }

  /* 官网动效：.hx-w-reveal 的区块进入视口时上滑 16px 淡入 240ms，只播一次。 */
  function reveal(scope) {
    scope = scope || document;
    var els = scope.querySelectorAll('.hx-w-reveal');
    if (!els.length || typeof IntersectionObserver !== 'function') return;
    var hosts = scope.querySelectorAll('.hx-w');
    Array.prototype.forEach.call(hosts.length ? hosts : [document.documentElement], function (h) { h.classList.add('hx-w-js'); });
    var io = new IntersectionObserver(function (ents) {
      ents.forEach(function (e) { if (e.isIntersecting) { e.target.classList.add('is-in'); io.unobserve(e.target); } });
    }, { threshold: 0.12 });
    Array.prototype.forEach.call(els, function (el) { el.classList.remove('is-in'); io.observe(el); });
  }

  /* 四种状态的标题和图标；两套主题文案完全一样。 */
  var COPY = {
    ok: { icon: 'shield', title: '已保护' },
    danger: { icon: 'shield-off', title: '已断网' },
    warn: { icon: 'pause', title: '防护已暂停' },
    neutral: { icon: 'shield-plain', title: '准备中' }
  };

  /* 大号状态图形。fam: tech（圆环 + 盾牌）| warm（圆角方块 + 盾牌）。opts.total / opts.left 秒：暂停中的倒计时。 */
  function statusGraphic(state, fam, opts) {
    opts = opts || {};
    var ic = opts.center || icon(COPY[state].icon, 40);
    var frac = state === 'warn' ? (opts.left || 0) / (opts.total || 1) : 1;
    var cd = state === 'warn' ? ' data-hx-countdown="' + (opts.left || 0) + '" data-hx-total="' + (opts.total || 0) + '"' : '';
    if (fam === 'tech') {
      var R = 52, C = 2 * Math.PI * R;
      var dash = state === 'neutral' ? ' stroke-dasharray="4 8"' : ' stroke-dasharray="' + C.toFixed(1) + '" stroke-dashoffset="' + (C * (1 - frac)).toFixed(1) + '"';
      return '<div class="hx-sg ' + state + '"' + cd + ' role="img" aria-label="' + COPY[state].title + '"><svg class="ring" width="120" height="120" viewBox="0 0 120 120" aria-hidden="true">' +
        '<circle class="fill" cx="60" cy="60" r="48"/><circle class="track" cx="60" cy="60" r="' + R + '" fill="none" stroke-width="3.5"/>' +
        '<circle class="arc" data-hx-arc="' + C.toFixed(2) + '" cx="60" cy="60" r="' + R + '" fill="none" stroke-width="3.5" stroke-linecap="round"' + dash + ' transform="rotate(-90 60 60)"/></svg><div class="ctr">' + ic + '</div></div>';
    }
    var prog = state === 'warn' ? '<svg class="ring" width="132" height="132" viewBox="0 0 132 132" style="position:absolute;inset:0" aria-hidden="true"><rect class="arc" data-hx-arc="100" x="2" y="2" width="128" height="128" rx="12" pathLength="100" fill="none" stroke-width="4" stroke-linecap="round" stroke-dasharray="100" stroke-dashoffset="' + (100 * (1 - frac)).toFixed(2) + '"/></svg>' : '';
    return '<div class="hx-sg sq ' + state + '"' + cd + ' role="img" aria-label="' + COPY[state].title + '"><div class="tile" style="position:relative">' + ic + prog + '</div></div>';
  }

  function mmss(s) { s = Math.max(0, Math.round(s)); var m = Math.floor(s / 60), x = s % 60; return (m < 10 ? '0' : '') + m + ':' + (x < 10 ? '0' : '') + x; }

  /* 倒计时：带 data-hx-countdown 的元素每秒减一，圆环 / 方框描边同步减少，[data-hx-mmss] 显示 mm:ss。 */
  var timers = [];
  function startCountdowns(scope) {
    timers.forEach(clearInterval); timers = [];
    var els = (scope || document).querySelectorAll('[data-hx-countdown]');
    Array.prototype.forEach.call(els, function (el) {
      var left = +el.getAttribute('data-hx-countdown'), total = +el.getAttribute('data-hx-total') || left;
      var panel = el.closest('.hx-panel') || document;
      function paint() {
        var arcs = el.querySelectorAll('[data-hx-arc]');
        Array.prototype.forEach.call(arcs, function (a) { var len = +a.getAttribute('data-hx-arc'); a.setAttribute('stroke-dashoffset', (len * (1 - left / total)).toFixed(2)); });
        Array.prototype.forEach.call(panel.querySelectorAll('[data-hx-mmss]'), function (t) { t.textContent = mmss(left); });
      }
      paint();
      timers.push(setInterval(function () { left = left > 0 ? left - 1 : total; paint(); }, 1000));
    });
  }

  /* 小交互：分段选择器、开关、单选行、导航、打码字段、复制。 */
  function siblingsSet(el, sel, attr, val) {
    var p = el.parentNode; Array.prototype.forEach.call(p.querySelectorAll(':scope > ' + sel), function (s) { s.setAttribute(attr, s === el ? val : (attr === 'aria-current' ? 'false' : 'false')); });
  }
  /* 写进剪贴板：先用 Clipboard API；没有或不让用时，用隐藏的文本框 + execCommand('copy')。done(成功没有) */
  function copyText(text, done) {
    function fallback() {
      var ta = document.createElement('textarea'), was = document.activeElement, ok = false;
      ta.value = text; ta.setAttribute('readonly', ''); ta.style.cssText = 'position:fixed;top:0;left:-9999px;opacity:0';
      document.body.appendChild(ta); ta.select();
      try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
      document.body.removeChild(ta);
      if (was && was.focus) was.focus();
      done(ok);
    }
    if (!text) return done(false);
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(function () { done(true); }, fallback);
    else fallback();
  }
  document.addEventListener('click', function (e) {
    var t = e.target.closest ? e.target : e.target.parentElement;
    if (!t) return;
    var b;
    if ((b = t.closest('[data-hx-seg] > button'))) { siblingsSet(b, 'button', 'aria-pressed', 'true'); return; }
    if ((b = t.closest('.hx-switch')) && !b.disabled) { b.setAttribute('aria-checked', b.getAttribute('aria-checked') === 'true' ? 'false' : 'true'); return; }
    if ((b = t.closest('[data-hx-pick] > .hx-route'))) { siblingsSet(b, '.hx-route', 'aria-selected', 'true'); return; }
    if ((b = t.closest('[data-hx-nav] > .hx-nav-item'))) { siblingsSet(b, '.hx-nav-item', 'aria-current', 'page'); return; }
    if ((b = t.closest('[data-hx-stat]'))) { siblingsSet(b, '[data-hx-stat]', 'aria-pressed', 'true'); return; }
    if ((b = t.closest('[data-hx-reveal]'))) {
      var box = b.closest('.hx-mask'), code = box.querySelector('code'), on = b.getAttribute('aria-pressed') === 'true';
      code.textContent = on ? '••••••' : code.getAttribute('data-plain');
      b.setAttribute('aria-pressed', on ? 'false' : 'true');
      b.lastChild.textContent = on ? '显示' : '隐藏';
      return;
    }
    if ((b = t.closest('[data-hx-copy]'))) {
      var cf = b.closest('.hx-copyfield'), inp = cf && cf.querySelector('input');
      var lab = b.querySelector('span') || b;
      if (!lab.hasAttribute('data-hx-label')) lab.setAttribute('data-hx-label', lab.textContent);
      copyText(b.getAttribute('data-hx-copy') || (inp ? inp.value : ''), function (ok) {
        lab.textContent = ok ? '已复制' : '没复制上';
        clearTimeout(lab.__hxCopy);
        lab.__hxCopy = setTimeout(function () { lab.textContent = lab.getAttribute('data-hx-label'); }, 1500);
      });
      return;
    }
    if ((b = t.closest('[data-hx-tabs] > button'))) { siblingsSet(b, 'button', 'aria-selected', 'true'); return; }
    if ((b = t.closest('[data-hx-open]'))) { var host = b.closest('.hx-panel-body') || document, dlg = host.querySelector('[data-hx-dialog="' + b.getAttribute('data-hx-open') + '"]'); if (dlg) { dlg.hidden = false; var f = dlg.querySelector('[data-hx-close]'); if (f) f.focus(); } return; }
    if ((b = t.closest('[data-hx-close]'))) { var d = b.closest('[data-hx-dialog]'); if (d) d.hidden = true; return; }
    if ((b = t.closest('[data-hx-themetoggle]'))) { var pnl = b.closest('[data-theme]'), dark = pnl.getAttribute('data-theme') === 'tech-dark'; pnl.setAttribute('data-theme', dark ? 'tech-light' : 'tech-dark'); return; }
    if ((b = t.closest('[data-hx-dismiss]'))) { var box2 = b.closest('[data-hx-box]'); if (box2) box2.style.display = 'none'; return; }
  });

  window.CCHahaeasy = {
    icon: icon,
    icons: ICONS,
    iconNames: ICON_NAMES,
    themes: themes,
    THEMES: THEMES,
    COPY: COPY,
    statusGraphic: statusGraphic,
    mmss: mmss,
    startCountdowns: startCountdowns,
    reveal: reveal
  };
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    Array.prototype.forEach.call(document.querySelectorAll('[data-hx-dialog]:not([hidden])'), function (d) { if (d.hasAttribute('data-hx-escape')) d.hidden = true; });
  });
})();
