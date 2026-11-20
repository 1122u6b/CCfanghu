/* 界面 ↔ 守护程序的桥接层。只在 App 里（Tauri 窗口）生效；在浏览器里直接打开时什么都不做，界面是原型演示。
 *
 * - 每 2 秒向守护程序要一次状态（本机通信的 Status），写进界面的状态（S.status、S.pause、线路列表……）再重画。
 * - 界面上的操作改成真的请求：暂停（两种）、恢复、选线路、改回自动、刷新连接、这条线路不好用、重新启动保护、安全检查。
 * - 安全检查的结果用守护程序的体检结果（Doctor），只列真的查过的项，没查的不显示（不拿演示数据充「正常」）。
 * - 账户（每 30 秒，操作后马上，重新连上守护程序时马上；没拿到时每 3 秒再要）：到期、续费申请状态、售后渠道、公告、账户编号；
 *   拿不到（出错、守护程序刚起来回了空的）时留着上一次的，不显示成「全套 · 0 天」；工单（进工单页、每 60 秒）。
 * - 多个出口（docs/01 §5.2「多个出口」）：账户信息的出口列表进线路页的「出口」一栏（老套餐是我们分配的加自己加的，单独软件、
 *   免费试用只有自己加的；全套没有这一栏）；切换、添加、换链接、删除经守护程序找服务端（服务端改当前出口、推新配置），
 *   本机不改线路；服务端的说明原样显示。概览「出口」格、侧栏、菜单栏写当前出口的名字。
 * - 经服务端的操作：激活（授权码已激活过时自动按重装恢复再试）、接入码、工单（新建、回复、评价）、续费申请、
 *   开通前先断网、直连规则（校验都在守护程序：01 §5.5 那张表，界面不再自己判断地址）。
 * - 时区：出口所在地、出口时区显示名都是配置包里后台登记的，电脑时区是系统给的（TZN）；不一致又没点过「暂不」时
 *   弹原型的时区弹窗；改时区、恢复、暂不走守护程序。
 * - 安全检查里时间不准（E3）给「同步时间」（按服务端时间校准）；后台运行没允许（E10）显示原型的「请在系统设置里允许」页，
 *   按钮打开系统设置的「登录项与扩展」。
 * - 安全检查的检查编号是守护程序给的；体检做完守护程序自动上传完整报告（去敏感、设备签名），界面上没有「发给客服」。
 * - 个人中心的联系方式是服务端那份，「修改」经守护程序提交。
 * - 个人中心「在网页上打开」：App 壳的 open_web_center 向守护程序要一次性登录链接、交给默认浏览器；链接不经过界面。
 *   「补号」带上去向 to: 'refill'，「升级到全套」「重新开通全套」带 to: 'upgrade'，壳加在链接后面，官网登录后打开这台电脑的购买弹窗。
 *   「续费」先带 to: 'renew' 去官网自助续费（余额或在线付款，付完自动顺延），线下付过钱的点「已经线下付了钱？」打开原来的续费面板
 *   提交申请；全套 Pro 服务端说能升档（suite.can_tier_up）时个人中心的全套卡上有「升到 Max 5x」，带 to: 'tier_up'。
 * - 定价三种（docs/01 §3.1）：账户信息的 suite 说这台电脑是全套、单独软件还是老套餐。全套写「全套 X 月 X 日到期」、续费面板只有
 *   「全套」（续 suite）和「流量」；单独软件写「永久使用」、流量不限；老套餐照原样写「软件」到期、续「软件」（node）。
 *   全套到期没续、还没接自己的出口（own_exit 是 lapsed）：照旧断网，断网页写全套已到期，主按钮「接入你的出口」，另有「重新开通全套」。
 * - 免费试用（docs/01 §3.1「免费试用」、§4 第 8 步）：suite.kind 是 trial。剩 3 天起横幅「免费试用还剩 X 天」，前 1 天发系统通知；
 *   「购买」带去向 to: 'buy_soft'（官网打开单独软件的购买弹窗、预选这台电脑）。到期断网页写「免费试用已到期」，给「购买单独软件」
 *   「退出软件」；「退出软件」和彻底退出一样先要电脑密码，守护程序撤掉防护（trial_quit）以后界面退出。守护程序在停用状态
 *   （trial_stopped：不接管网络）时界面只显示到期页（另有「换授权码」），菜单栏 / 托盘只有「购买单独软件…」「退出软件」；转正后自动回到主界面。
 *   单独软件电脑在个人中心有「填全套码升级」（同一个换码对话框，服务端只收全套码），换成全套后线路开通前照常用自己的出口。
 * - 等待时看得到进度：准备中页写激活时间（bound_at）、一般多久，「开通前先断网」也能在这页开关；线路从准备中到保护中发系统通知
 *   「独享线路开通了，已开始保护」。账户信息的 suite.ai_preparing（line / queued / issuing）在时「我的账号」不隐藏，显示「Claude 账号准备中」，
 *   灰卡片不给「补号」「重新开通全套」。激活成功的提示按套餐写：全套「线路开通中」，单独软件、试用「下一步接入你的出口」。
 *   一直没接自己的出口就到期的（守护程序还在等开通 · 照常上网）也一样只显示到期页，「退出」只关界面（网络本来没接管，不要密码）。
 * - 菜单栏 / 托盘（App 壳的 appshell）：每次问完状态把状态（种类、原因、菜单顶上的两三行字、各条线路和延迟、自动还是手动、未读数、红点）推过去，
 *   壳据此换图标、搭系统原生菜单；顺便取走菜单上点过的
 *   操作（刷新、暂停 10 / 30 / 60 分钟、恢复）照常发请求。红点 = 有没看的工单回复或没关的重要公告。
 * - 系统通知（设置里「通知」开着才发）：防护状态变化、工单新回复、后台勾了「弹系统通知」的公告、明天到期、需要处理的消息。
 * - 消息（docs/01 §6.10）：账户信息里的未读数、最新编号变了就向守护程序要消息列表（守护程序本机有缓存，断网时也有）；
 *   点开一条、全部标为已读告诉守护程序（它先记本机、连上再补报）；跳转：工单 → 工单页，续费 → 续费面板（号跟着全套一起续），
 *   补号 / 重新开通全套 → 和「我的账号」的按钮一样，订单 / 别的商品 / 官网页面 → 在网页上打开。
 *   需要处理的消息、勾了弹窗的公告打开主界面时弹一次（一次一个；「每次都弹」的公告每次打开 App 弹到点「知道了」为止）。
 * - 我的账号（docs/01 §5.12）：版本变了重新要列表（不含登录信息）：在用的和 30 天内收回、停用的。服务端的状态换成原型的写法：
 *   收回、停用的是灰卡片（已到期收回 / 已停用 · 已退款 ¥X / 已停用；下面那行小字按原因写：到期、退款收回、违规收回、确认被封、
 *   登录信息对不上作废），没有在用的号时给「补号」（服务端说能补，suite.can_refill）或「重新开通全套」；7 天内到期的卡上一行和「续费」
 *   （原型的续费面板选好「全套」）。「登录 Claude」（docs/01 §5.12）：经 App 壳的 ai_login 开始（壳先打开 Claude 登录页），
 *   之后每秒 ai_login_poll 问进度，壳等到了登录链接自己在默认浏览器里打开；界面只拿到状态（waiting 带剩余秒数 / opened /
 *   timeout / unavailable / paused），拿不到链接。保护暂停着时「登录 Claude」灰着、上方提示暂停期间别登录（原型画）。
 *   密码号（服务端的 login 是 password，docs/01 §3.4「两种交付方式」）卡上是「登录信息」一块：「显示登录信息」经守护程序
 *   向服务端现取邮箱密码、辅助邮箱和它的密码（ai_secrets，服务端每次记时间、设备和保护状态），交给原型显示、原型自己计时
 *   5 分钟收起；只在内存里，收起就清掉；保护暂停着时按钮灰着、显示着的马上收起；号已经收回时说那句话、收起、重新要列表。
 *   「账号登不上了」建包赔单。保障状态：服务端给了可退金额（refund_now_cents）的号卡头写「现在被封可退 ¥X」（在用的号用
 *   随心跳更新的 suite 那份），旁边「看怎么算的」一键登录去向 refund_calc；没给的不显示。
 * - 续费面板：全套是 suite（线路和号一起顺延），老套餐的「软件」是 node（线路的到期顺延），流量是 traffic；选了哪几项写进备注。
 * - 外观（跟随系统 / 浅色 / 深色）、主题、通知开关记在本机，下次打开还是这样。
 *
 * App 壳的命令 daemon：收一条本机通信的请求（JSON），转给守护程序，回守护程序的回应（JSON）；带登录票据、登录链接的请求界面不能发。 */
(function () {
  'use strict';
  var T = window.__TAURI__;
  var P = window.__proto;
  if (!T || !T.core || !P) return;
  var S = P.S, A = P.A, ROUTES = P.ROUTES;
  // 壳开了窗口材质（macOS 侧栏半透明、Windows 11 Mica）就把窗口底设成透明（app.css 的 html.glass）。
  T.core.invoke('shell_info').then(function (i) { if (i && i.glass) document.documentElement.classList.add('glass'); }).catch(function () {});

  function call(req) { return T.core.invoke('daemon', { request: req }); }
  function msg(e) { return (e && e.message) || String(e || '没有回应'); }
  function hhmm(d) { return ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2); }

  /* 原型里的演示数据在 App 里不显示：工单、到期、售后渠道、账户编号、出口 IP、预填的授权码都换成守护程序给的。 */
  S.tickets = [];
  // 实时网速的曲线（概览数据格）：原型带了演示曲线，App 里从守护程序给的速度一点点攒（ui.js 的 rateTick）。
  S.rateHist = [];
  S.contact = '';
  // 免责声明同意到第几版：等守护程序报上来再决定拦不拦。
  S.consentVer = null;
  P.CHANNELS.length = 0;
  P.set({ account: '-', ip: '-', code: '', checkId: '—' });
  // 出口所在地、出口时区：守护程序给之前是空（界面就不提）；电脑时区给之前显示「—」。
  P.TZN.city = ''; P.TZN.exit = ''; P.TZN.sys = '—';
  // 个人中心的套餐、到期、流量、本机设备：守护程序给之前也显示「—」。
  P.INFO.plan = '—'; P.INFO.used = '—'; P.INFO.limit = '—'; P.INFO.pct = 0; P.INFO.reset = '—'; P.INFO.device = '—'; P.INFO.bound = '—'; P.INFO.uptime = ''; P.INFO.daily = [];
  S.exp = { date: '—', days: 0 };
  S.licensePermanent = null;
  S.lan = [];
  S.banner = null;
  S.notice = null;
  S.alerts = { expire1d: false, update: false, conflict: false };
  S.tzChanged = false;
  S.hist = { lines: [], events: [] };
  S.speedTesting = false;
  S.lastCheck = null;
  // 检查历史、帮助内容、余额（docs/01 §5.7、§5.8、§5.13）：守护程序给之前都是空的。
  S.checkHist = [];
  S.balance = null;
  P.FAQ.length = 0; P.GUIDES.length = 0;
  var guideUrls = [];
  S.guard = null;
  S.rate = null;
  S.status = 'starting';
  S.starting = { stage: 0, t0: Date.now(), slow: false, hold: true };

  /* ---------- 状态 ---------- */
  var lineIds = [];
  var curIp = '-';
  var conflictNow = null;
  var offline = false, offlineSince = 0;
  // 连不上守护程序多久算「防护没有运行」：它被系统拉起要几秒到十几秒，这段时间不下结论。
  var DOWN_AFTER = 15000;
  // 切换出口时守护程序换配置、重启内核，本机通信会断一下（1.0.13）：正在切换、或切换成功后 20 秒内连不上，
  // 不弹「连不上防护组件」，连上了也不弹「已连上」；过了 20 秒还连不上照常提示。
  var ROUTE_QUIET = 20000, routeAt = 0, offlineQuiet = false;
  function routeQuiet() { return !!S.busy.route || Date.now() - routeAt < ROUTE_QUIET; }

  function busy() { for (var k in S.busy) if (S.busy[k]) return true; return false; }

  // 断网原因 → 原型里的断网页。
  var REASON = {
    all_lines_down: 'route', software_expired: 'expired', node_expired: 'expired', quota_exhausted: 'traffic',
    device_unbound: 'unbound', banned: 'banned', maintenance: 'service', credential_rotated: 'rotated',
    kernel_down: 'component', rules_failed: 'rules', preflight_failed: 'settings', config_invalid: 'settings',
    tun_taken: 'taken', all_exits_down: 'exits', exit_down: 'exitoff'
  };
  // 别的代理软件占着虚拟网卡（docs/03 P4）：原型里没有这一页，在这里补上；是谁用冲突提示里认出来的名字。
  P.PM.taken = {
    short: '别的代理软件占着网络',
    get desc() { return (P.CONFL.name || '别的代理软件') + ' 占着虚拟网卡，网络暂时不通，真实 IP 没有暴露。退出它之后自动恢复。'; },
    btns: [['primary', '帮我退出', 'quitApp']]
  };

  function applyLines(st) {
    var lines = st.lines || [];
    lineIds = lines.map(function (l) { return l.id; });
    var rows = lines.map(function (l, i) {
      var m = /(\d+)/.exec(l.label || '');
      var tag = (l.label || '').split('·').slice(1).join('·').trim();
      return { n: m ? +m[1] : i + 1, tag: tag, desc: l.region || '', ms: l.latency_ms == null ? null : l.latency_ms, ok: l.success_pct == null ? null : l.success_pct, speed: l.speed ? { mbps: l.speed.down_bps * 8 / 1000000, ago: l.speed.ago_secs, measured: l.speed.measured } : null, sig: l.signal };
    });
    // 原型的页面默认至少有一条线路（还没开通、刚启动时没有）：放一行占位，免得页面画不出来。
    if (!rows.length) rows = [{ n: '-', tag: '还没有线路', desc: '', ms: null, ok: 0, sig: 0 }];
    ROUTES.length = 0;
    Array.prototype.push.apply(ROUTES, rows);
    var cur = lineIds.indexOf(st.current_line);
    if (cur >= 0) S.route = cur;
    if (S.route >= ROUTES.length) S.route = 0;
    curIp = (lines[cur] && lines[cur].exit_ip) || '-';
    P.set({ ip: curIp });
    S.routeMode = st.line_mode && st.line_mode.mode === 'manual' ? 'manual' : 'auto';
    // 后台没登记出口所在地时，用当前线路的地区（「美国·洛杉矶」）。
    if (!(zone && zone.location)) P.TZN.city = (ROUTES[S.route] && ROUTES[S.route].desc) || (zone ? zoneCity(zone.exit) : '') || '';
  }

  function applyProtection(p) {
    var now = Date.now();
    // 「防护已暂停」的常驻提示只在暂停期间有：恢复了、断网了都收起。
    if (p.state !== 'paused' || p.off) S.warnToast = null;
    else if (S.status !== 'paused') S.warnToast = '防护已暂停，你访问的网站能看到你的真实 IP';
    switch (p.state) {
      case 'unprotected':
        S.status = 'down'; S.pause = null; S.starting = null; S.warnToast = '防护未生效，当前网络可能直接连接'; break;
      case 'protected':
        S.status = 'ok'; S.pause = null; S.starting = null; break;
      case 'starting':
        var since = p.since_secs || 0;
        if (S.status !== 'starting' || !S.starting) S.starting = { stage: 0, t0: now - since * 1000, slow: false, hold: true };
        S.starting.stage = since > 5 ? 1 : 0;
        S.starting.slow = since >= 20;
        S.starting.switching = p.reason === 'switching_exit';
        S.status = 'starting'; S.pause = null; break;
      case 'paused':
        // 彻底退出（docs/01 §5.3）：界面刚启动时看到，是上次退出后用户又打开了软件，恢复保护；
        // 界面开着时守护程序才进的（命令行 pause quit），界面跟着退出，不把保护恢复回来。正在退出时不算。
        // 只认「从别的状态变成彻底退出」：恢复保护时还在路上的旧回应也写着彻底退出，不能当成又退了一次。
        // 重新打开软件不再自动恢复：显示「防护未开启」，由客户自己点「开启防护」。
        if (p.quit && !quitting && seenStatus && !lastQuit) {
          quitting = true;
          T.core.invoke('quit_app').catch(function () { quitting = false; });
        }
        // 激活后还没点「开启防护」：不是暂停，没有倒计时，不会自动开启。
        if (p.off) { S.status = 'paused'; S.pause = { left: 0, total: 0, kind: 'off', from: { status: 'ok' }, end: '' }; break; }
        var left = p.remaining_secs || 0;
        var prev = S.status === 'paused' && S.pause ? S.pause : null;
        S.status = 'paused';
        S.pause = {
          left: left, total: prev ? Math.max(prev.total, left) : left,
          kind: p.auto_resume_on_node ? 'wifi' : 'user',
          from: prev ? prev.from : { status: 'ok', reason: S.reason },
          end: hhmm(new Date(now + left * 1000))
        };
        break;
      case 'not_activated':
        if (S.screen !== 'activate') { S.screen = 'activate'; S.wizard = null; }
        S.status = 'prep'; S.pause = null; break;
      case 'provisioning':
        if (S.screen === 'activate' && !S.busy.act) S.screen = 'main';
        S.status = 'prep'; S.prepPaused = !!p.blocked; S.pause = null;
        // 试用电脑一直没接自己的出口就到期了：网络本来就没接管，和停用一样只显示到期页，「退出」只关界面。
        if (trialOver()) stopTrial();
        break;
      case 'blocked':
        S.status = 'pm'; S.reason = REASON[p.reason] || 'route'; S.pause = null;
        // 全套到期没续、还没接自己的出口（docs/01 §4 第 8 步）：线路已经停了，连不上就是因为到期。
        if (S.ownExit === 'lapsed' && S.reason === 'route') S.reason = 'expired';
        // 免费试用到期：断网页给「购买单独软件」「退出软件」。
        if (S.reason === 'expired' && suiteKind() === 'trial') S.reason = 'trialExpired';
        break;
      case 'trial_stopped':
        // 免费试用到期、退出过：不接管网络，界面只显示到期页（「购买单独软件」「换授权码」「退出」）。
        stopTrial(); S.status = 'prep'; S.pause = null;
        break;
    }
    // 转正、后台顺延以后守护程序回到保护：离开到期页。
    var stoppedNow = p.state === 'trial_stopped' || (p.state === 'provisioning' && trialOver());
    if (!stoppedNow && S.screen === 'trialStopped') { S.screen = 'main'; S.page = 'overview'; }
    // 激活后第一次开通完成、线路在连或已连上：进首次向导（时区、局域网、浏览器保护设置、安全检查）。关掉 App 再打开也接着弹。
    // 向导中途断网（解绑、到期等）或重新开通：先回主界面显示原因，恢复后从第一步重来（docs/01 §4 第 5 步）。
    var live = p.state === 'protected' || p.state === 'starting';
    if (S.screen === 'wizard' && (p.state === 'blocked' || p.state === 'provisioning')) {
      S.screen = 'main'; S.page = 'overview'; S.wizard = null; S.dialog = null;
      if (S.check && S.check.ctx === 'wizard') S.check = { phase: 'idle' };
    }
    if (live && store('cc.wizard.pending') && S.screen === 'main' && !S.wizard) {
      S.screen = 'wizard'; S.wizard = { step: 0, tz: 'ask' };
    }
  }

  // 进到期页时关掉别的对话框、向导；已经在到期页就不动（在到期页上打开的「换授权码」不被每秒的查状态关掉）。
  function stopTrial() {
    if (S.screen !== 'trialStopped') { S.dialog = null; S.wizard = null; }
    S.screen = 'trialStopped'; S.trial = 'stopped';
  }

  var failoverSeen = 0, modeAsked = false;
  function apply(st) {
    applyLines(st);
    if (st.auto_exit != null) S.autoExit = !!st.auto_exit;
    if (st.listed != null) S.listed = !!st.listed;
    S.holding = !!st.holding;
    // 老客户升级后问一次走出口的方式（新装的已经是「只让 Claude 走出口」，不问）。
    if (st.mode_offer && !modeAsked && S.screen === 'main' && !S.dialog) { modeAsked = true; S.dialog = { type: 'modeOffer' }; }
    // 出口连不上、已自动换到另一个：系统通知 + 界面提示（同一次只提醒一次；打开软件时 10 分钟前的旧事不再提）。
    var lf = st.last_failover;
    if (lf && lf.at && lf.at !== failoverSeen) {
      failoverSeen = lf.at; S.failover = { at: lf.at, from: lf.from || '原出口', to: lf.to || '另一个出口' };
      if (Date.now() / 1000 - lf.at < 600) {
        notifyOnce('failover.' + lf.at, '出口已自动切换', '「' + S.failover.from + '」连不上，已换到「' + S.failover.to + '」，网站看到的 IP 会变');
        P.toast('「' + esc(S.failover.from) + '」连不上，已自动换到「' + esc(S.failover.to) + '」，网站看到的 IP 会变', 'info');
      }
    }
    S.rate = st.rate ? { up: st.rate.up_bps, down: st.rate.down_bps } : null;
    S.speedTesting = !!S.busy.speed || !!st.speed_testing;
    // 最近一次检查（docs/03 §4）：安全检查页一打开就显示。
    var lc = st.last_check;
    // 防护记录（docs/01 §5.3）：时长按现在的时间算，每次刷新状态跟着走。
    var g = st.guard;
    S.guard = g ? { dur: span(Math.max(0, Math.floor(Date.now() / 1000) - g.since)), blocked: g.blocked == null ? null : g.blocked } : null;
    S.lastCheck = lc ? { at: (new Date(lc.at * 1000).toDateString() === new Date().toDateString() ? '今天 ' : '') + when(lc.at), kind: lc.kind, problems: lc.problems } : null;
    // 名单以外的浏览器（docs/03 P7）：第一次发现时发一条系统通知；只是提示，不断网、不拦。
    var others = (st.other_browsers || []).slice().sort();
    if (others.length) notifyOnce('browsers.' + others.join(','), '发现其他浏览器', others.join('、') + '装不上浏览器保护设置，上网建议用 Chrome、Edge 或 Firefox');
    if (!busy()) { var pr = st.protection || {}; applyProtection(pr); seenStatus = true; lastQuit = pr.state === 'paused' && !!pr.quit; }
    var warn = (st.conflicts || []).filter(function (c) { return c.severity === 'warn'; });
    S.alerts.conflict = warn.length > 0;
    conflictNow = warn[0] || null;
    // 「XX 还在运行」的弹窗：用户后来自己从托盘退了、或它被结束了，状态里没有它就自动收起，排着的也撤掉。
    var live = (st.conflicts || []).map(function (c) { return 'quit.' + c.id; });
    var stale = function (p) { return p && /^quit\./.test(p.id) && live.indexOf(p.id) < 0; };
    S.popQueue = S.popQueue.filter(function (p) { return !stale(p); });
    if (stale(S.popup)) { var gone = S.popup.who || S.popup.title.replace(/ 还在运行$/, ''); A.popAck(); P.toast(gone + ' 已经退出了'); }
    // 冲突软件（docs/01 §5.3「说出是谁」）：横幅写守护程序给的那句话（查出了就有真名，查不出写证据和去哪找）；
    // 按钮看退出方式：能退的「帮我退出」、系统代理没关的「帮我关掉」、都没有的「发给客服」。
    if (conflictNow) {
      var hw = conflictNow.quit && conflictNow.quit.how;
      P.CONFL.name = esc(conflictNow.name || '其他网络软件');
      P.CONFL.msg = esc(conflictNow.message || '');
      P.CONFL.kind = hw === 'clear_proxy' ? 'stale' : hw ? 'named' : 'unknown';
      P.CONFL.evidence = esc(conflictNow.evidence || '');
    }
    // 刚替用户关掉系统代理：两分钟里横幅给「撤销」。
    S.proxyUndo = !!st.proxy_undo && Date.now() - proxyOffAt < PROXY_UNDO_MS && !proxyUndoHidden;
    S.coexist = !!st.coexist;   // 共存模式：名单里的走 Leyra 出口，其他走客户自己的代理
    applyUpdate(st);
    // 后台运行没允许（E10）：显示「请在系统设置里允许」页；允许了回到首页。
    var bgBad = (st.checks || []).some(function (c) { return c.id === 'E10' && c.verdict === 'fail'; });
    applyBypass(st);
    applyFilters(st);
    if (bgBad && S.screen === 'main') S.screen = 'bgperm';
    else if (!bgBad && S.screen === 'bgperm') S.screen = 'main';
    P.render();
    hintProfile(st);
  }

  // 绕过保护的软件（docs/03 L8）：弹窗、系统通知；切不断时把主窗口叫到前面。同一批软件不重复弹。
  var bypassShown = '';
  function applyBypass(st) {
    var c = (st.checks || []).filter(function (x) { return x.id === 'L8' && (x.verdict === 'fail' || x.verdict === 'warn'); })[0];
    if (!c) return;
    var who = (c.detail || '').split('：').slice(1).join('：').replace(/（\d+ 条）/g, '');
    // 同一批软件一天只弹一次（状态短暂变化也不重复弹，不反复把窗口叫到前面）。
    var key = c.verdict + ':' + who.split('、').sort().join('、') + ':' + new Date().toDateString();
    if (key === bypassShown || popped.indexOf(key) >= 0) return;
    bypassShown = key; popped.push(key); store('cc.popped', popped.slice(-200));
    var bad = c.verdict === 'fail';
    var title = bad ? '有软件绕过了保护，没能切断' : '有软件绕过了保护';
    var body = bad ? who + ' 正在绕过独享线路，可能暴露真实 IP，没能切断。请先退出它，不要访问重要网站。已通知客服。'
      : who + ' 绕过了独享线路，已被切断。它可能用了你的真实 IP，建议退出这个软件。';
    notifyOnce('bypass.' + key + '.' + new Date().toDateString(), title, body);
    P.showPopup({ id: 'bypass.' + key, lv: 'action', title: title, body: body });
  }

  // 有网络过滤排在防护前面（docs/03 E6）：装机后一直在的，只在第一次发现时弹，换了内容再弹。
  function applyFilters(st) {
    var c = (st.checks || []).filter(function (x) { return x.id === 'E6' && x.verdict === 'warn'; })[0];
    if (!c) return;
    var key = 'f' + c.detail;
    if (popped.indexOf(key) >= 0) return;
    popped.push(key); store('cc.popped', popped.slice(-200));
    var who = (c.detail || '').split('：').slice(1).join('：').split('；')[0];
    var body = who + ' 有能力绕过防护。现在没有发现漏出去的流量，如果上网有异常，可以暂时关掉它的网络防护功能。';
    notifyOnce('filter.' + key, '有网络过滤排在防护前面', body);
    P.showPopup({ id: key, lv: 'action', title: '有网络过滤排在防护前面', body: body });
  }

  /* ---------- 升级 ---------- */
  // 新版本下载好、检查过才提示（docs/04）。「下次开机时更新」之后这个版本不再提示，守护程序下次启动时自己装。
  var later = null;
  function applyUpdate(st) {
    if (st.version) S.version = st.version;
    // 守护程序已经是新版本、界面还是旧的（Windows 自动升级后）：请 App 壳从新版本目录打开新界面（docs/01 §5.10）。
    if (st.version && appVersion && st.version !== appVersion) relaunchNew();
    if (st.update) S.newVersion = st.update;
    // 已选「下次开机时更新」的版本（后台服务记着）不再提示，重开界面也一样。
    if (st.update_at_boot) later = st.update_at_boot;
    if (!S.busy.update) S.alerts.update = !!st.update && st.update !== later;
    // 新版本下载好了（docs/01 §5.10）：每个版本只弹一次窗，「立即更新」「下次开机时更新」；不点的横幅一直在。
    if (st.update && st.update !== later && !S.busy.update && store('cc.update.popped') !== st.update) { store('cc.update.popped', st.update); P.updatePopup(); }
    // 免责声明（docs/01 §4 第 3 步）：同意过的版本比现在的低就整页拦着；旧守护程序不报这一项，不拦。
    S.consentVer = st.consent_version == null ? null : st.consent_version;
  }
  A.consentGo = function () {
    var el = document.querySelector('#f-consent'), v = el ? el.value.replace(/\s/g, '') : '';
    if (v !== P.CONSENT_TEXT) { S.actErr.consent = '要一字不差地输入：' + P.CONSENT_TEXT; P.render(); return; }
    call({ type: 'consent', version: P.DISCLAIMER_VER }).then(function () {
      S.actErr.consent = null; S.consentVer = P.DISCLAIMER_VER; P.render();
    }).catch(function (e) { S.actErr.consent = '没记下来：' + msg(e) + '，再点一次试试'; P.render(); });
  };
  // 不同意：只关界面，防护照常（守护程序不受影响）。
  A.consentNo = function () { T.core.invoke('quit_app').catch(function () {}); };

  A.updateNow = function () {
    // 守护程序切到新版本后退出、由系统拉起：这几秒连不上防护组件、网络断开。版本号变了就是装好了。
    var from = S.version, to = S.newVersion, t0 = Date.now();
    S.busy.update = { text: '正在切换到新版本，网络暂停几秒' }; P.render();
    call({ type: 'install_update' }).then(function () {
      var wait = setInterval(function () {
        call({ type: 'status' }).then(function (r) {
          if (r.version && r.version !== from) {
            clearInterval(wait); S.busy.update = null; S.alerts.update = false;
            apply(r); P.toast('已更新到 ' + r.version);
          }
        }).catch(function () {
          if (Date.now() - t0 > 120000) {
            clearInterval(wait); S.busy.update = null; P.render();
            P.toast('新版本 ' + to + ' 还没启动起来，网络保持断开。可以重启电脑，或联系客服', 'info');
          }
        });
      }, 1000);
    }).catch(function (e) { S.busy.update = null; P.render(); P.toast('没能更新：' + msg(e), 'info'); });
  };
  // 「下次开机时更新」：交给后台服务记住，下次开机它自己装好，不再提示。
  A.updateLater = function () {
    var v = S.newVersion;
    call({ type: 'update_at_boot' }).then(function () {
      later = v; S.alerts.update = false; P.render(); P.toast('下次开机时自动更新到 ' + v + '，不用再点', 'clock');
    }).catch(function (e) { P.toast('没记下来：' + msg(e) + '。可以点「立即更新」', 'info'); });
  };
  A.checkUpdate = function () {
    if (S.alerts.update) { A.go('overview'); return; }
    S.busy.upd = { text: '正在检查' }; P.render();
    var done = function (text) { S.busy.upd = null; P.render(); if (text) P.toast(text, 'info'); };
    call({ type: 'check_update' }).then(function () {
      // 有新版本时守护程序先下载、核对，下好了才提示：这里等几秒看有没有已经下好的。
      setTimeout(function () {
        call({ type: 'status' }).then(function (r) {
          later = null; apply(r);
          done(r.update ? '' : '当前是最新版本 ' + S.version + '；有新版本时会自动下载，下载好再提示');
        }).catch(function (e) { done('没查成：' + msg(e)); });
      }, 3000);
    }).catch(function (e) { done('没查成：' + msg(e)); });
  };

  // 线路页的曲线和事件、概览的最近动态（docs/01 §5.13，同一份记录）：打开这两页时取一次，之后每 20 秒取一次。
  var histAt = 0;
  function loadHistory() {
    if ((S.page !== 'routes' && S.page !== 'overview') || S.screen !== 'main' || Date.now() - histAt < 20000) return;
    histAt = Date.now();
    call({ type: 'history' }).then(function (h) {
      S.hist = {
        lines: (h.lines || []).map(function (l) {
          var i = lineIds.indexOf(l.id), r = ROUTES[i];
          return { name: r ? '线路 ' + r.n : l.id, pts: (l.points || []).map(function (p) { return [p.ago_secs, p.ms == null ? null : p.ms]; }) };
        }),
        events: (h.events || []).map(function (e, i, all) { return { time: evTime(e.at), text: eventText(e, all[i + 1]) }; })
      };
      P.render();
    }).catch(function () { histAt = 0; });
  }
  // 今天写 HH:MM，昨天写「昨天 HH:MM」，更早写「X 月 X 日」。
  function evTime(at) {
    var d = new Date(at * 1000), now = new Date(), y = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
    if (d.toDateString() === now.toDateString()) return hhmm(d);
    if (d.toDateString() === y.toDateString()) return '昨天 ' + hhmm(d);
    return (d.getMonth() + 1) + ' 月 ' + d.getDate() + ' 日';
  }
  // `prev` 是更早的那一条：断网、暂停之后回到保护写「防护恢复」。
  function eventText(e, prev) {
    if (e.kind === 'switch') return '换到' + esc(e.detail);
    if (e.kind === 'exit') return '切换到出口「' + esc(e.detail) + '」';
    if (e.kind === 'paused') return e.detail === 'quit' ? '彻底退出' : /^\d+$/.test(e.detail || '') ? '暂停防护 ' + e.detail + ' 分钟' : '暂停防护';
    if (e.kind === 'activated') return '已激活';
    if (e.kind === 'exit_failover') return '出口连不上，已自动切换：' + esc(e.detail);
    if (e.kind === 'timezone') return '电脑时区已自动改成出口所在地（' + esc(e.detail) + '），可在设置里恢复';
    if (e.kind === 'bypass') return '有软件绕过保护：' + esc(e.detail);
    if (e.kind === 'protected') return prev && (prev.kind === 'paused' || prev.kind === 'blocked') ? '防护恢复' : '已保护';
    if (e.kind === 'check') return +e.detail ? '安全检查 ' + (+e.detail) + ' 项要处理' : '安全检查一切正常';
    if (e.kind === 'update') return '更新到 ' + esc(e.detail);
    if (e.kind === 'update_ready') return '新版本 ' + esc(e.detail) + ' 已下载好';
    if (e.kind === 'update_at_boot') return '下次开机时自动更新到 ' + esc(e.detail);
    if (e.kind === 'exit_switched') return '已换出口，网站现在看到 ' + esc(e.detail);
    // 自动修复（docs/01 §5.14）：「暂停了冲突软件检测」「进入安全模式，辅助功能先暂停」。
    if (e.kind === 'repair') return '自动修复：' + esc(e.detail);
    return '已断网 · ' + (shortOf(REASON[e.detail]) || '线路连不上');
  }

  // 界面自己的版本（App 壳给的）；拿不到就不比。
  var appVersion = null, relaunchAt = 0;
  if (T.app && T.app.getVersion) T.app.getVersion().then(function (v) { appVersion = v; }).catch(function () {});
  // 升级完界面自己换新（docs/01 §5.10）：Windows 的 App 壳有 relaunch_new_version（看新版本目录比自己新才换）；
  // macOS 由守护程序重开界面，没有这个命令，调了也只是报错，不管它。一分钟最多请一次。
  function relaunchNew() {
    if (Date.now() - relaunchAt < 60000) return;
    relaunchAt = Date.now();
    T.core.invoke('relaunch_new_version').catch(function () {});
  }

  var activationEpoch = 0;
  function poll() {
    var epoch = activationEpoch;
    return call({ type: 'status' }).then(function (r) {
      if (epoch !== activationEpoch) return;
      // 重新连上（睡眠唤醒、守护程序重启）：马上重拉账户信息，不等 30 秒。
      if (offline) { offline = false; if (!offlineQuiet) P.toast('已连上防护组件'); offlineQuiet = false; loadAccount(); }
      apply(r);
      loadHistory();
    }).catch(function (e) {
      // 界面和守护程序的版本对不上：守护程序已经升级了，界面换新就好，不算「防护没有运行」，也不提杀毒软件。
      if (/版本对不上/.test(msg(e))) {
        if (!S.busy.update) { S.busy.update = { text: '正在切换到新版本' }; P.render(); }
        relaunchNew();
        return;
      }
      if (!offline) {
        offline = true; offlineSince = Date.now(); offlineQuiet = routeQuiet();
        if (!offlineQuiet) P.toast('连不上防护组件，正在重试', 'info');
      } else if (offlineQuiet && !routeQuiet()) { offlineQuiet = false; P.toast('连不上防护组件，正在重试', 'info'); }
      // 升级切换版本、正在恢复防护时本来就连不上，由各自的流程收尾。
      if (S.status !== 'down' && !S.busy.update && !S.busy.daemon && Date.now() - offlineSince >= DOWN_AFTER) enterDown();
      console.warn('[桥接] 状态：' + msg(e));
    });
  }

  // 连不上守护程序：不能再显示上一次的状态（可能还写着「已保护」），改显示「防护没有运行」（docs/01 §4）。
  function enterDown() {
    S.status = 'down'; S.pause = null; S.starting = null; S.warnToast = null; S.dialog = null; S.wizard = null;
    S.screen = 'main'; S.page = 'overview';
    P.render();
  }

  // 「恢复防护」：系统要一次管理员权限，重新启动守护程序，等它回应。
  A.daemonStart = function () {
    if (S.busy.daemon) return;
    S.busy.daemon = { text: '正在请求管理员权限' }; P.render();
    T.core.invoke('start_daemon').then(function () {
      S.busy.daemon = { text: '正在启动防护' }; P.render();
      var t0 = Date.now(), wait = setInterval(function () {
        call({ type: 'status' }).then(function (r) {
          clearInterval(wait); S.busy.daemon = null; offline = false; offlineQuiet = false; loadAccount();
          apply(r); P.toast('防护组件已启动');
        }).catch(function () {
          // 系统拉起守护程序、它自动修复后再起来，最长要几十秒（docs/01 §5.14）。
          if (Date.now() - t0 < 60000) return;
          clearInterval(wait); S.busy.daemon = null;
          // 程序文件不见了、服务没装上这两种，start_daemon 自己会说是杀毒软件；到这里多半是起来又退出，它会自己重试。
          // 说明长，普通提示 2.5 秒就消失读不完：用带关闭按钮的提示条，守护程序回来后自动清掉（applyPause）。
          S.warnToast = '防护组件没起来，已经在自动重试。过一两分钟还是这样，请重启电脑；还不行请联系客服';
          P.render();
        });
      }, 1000);
    }).catch(function (e) { S.busy.daemon = null; S.warnToast = '没有恢复：' + msg(e); P.render(); });
  };

  /* ---------- 操作 ---------- */
  var quitting = false;
  // 界面启动以来拿到过守护程序的状态没有（彻底退出时分辨是刚打开软件还是开着时进的）。
  var seenStatus = false, lastQuit = false;
  function act(req, okText) {
    return call(req).then(function () {
      if (okText) P.toast(okText);
      return poll();
    }).catch(function (e) { P.toast(msg(e), 'info'); });
  }

  // 真的会切断防护的请求（暂停、关掉「开通前先断网」）先让用户输一次电脑密码（docs/01 §4 第 6 条），系统框由壳弹，密码不经过界面。
  function withOwner(reason, cancelled) {
    return T.core.invoke('verify_owner', { reason: reason }).then(function () { return true; }, function (e) {
      P.toast(cancelled + '：' + msg(e), 'info'); return false;
    });
  }
  A.pauseGo = function () {
    var d = S.dialog || {};
    var req = { type: 'pause', minutes: d.login ? 10 : (d.min || 10), reason: d.login ? 'wifi_login' : 'user' };
    S.dialog = null; P.render();
    withOwner('暂停防护后你的真实 IP 会暴露。确认是你本人后继续。', '没有暂停，防护照常').then(function (ok) { return ok && act(req); }).then(function () { if (S.status === 'paused') { S.warnToast = '防护已暂停，你访问的网站能看到你的真实 IP'; P.render(); } });
  };
  A.resume = function () { var off = S.pause && S.pause.kind === 'off'; S.tray = false; act({ type: 'resume' }, off ? '正在开启防护' : '正在恢复防护'); };
  // 免费试用到期「退出软件」（docs/01 §4 第 8 步）：和彻底退出一样要电脑密码；守护程序撤掉防护、记下停用（以后开机也不接管网络），
  // 界面再退出。停用以后到期页上的「退出」只退界面。
  A.trialQuitGo = function () {
    S.dialog = null; P.render();
    withOwner('退出软件后不再保护，你的真实 IP 会暴露。确认是你本人后继续。', '没有退出，防护照常').then(function (ok) {
      if (!ok) return;
      return call({ type: 'trial_quit' }).then(function () { return T.core.invoke('quit_app'); });
    }).catch(function (e) { P.toast('没有退出：' + msg(e), 'info'); });
  };
  A.trialExit = function () { S.tray = false; T.core.invoke('quit_app').catch(function (e) { P.toast('没有退出：' + msg(e), 'info'); }); };
  // 彻底退出（docs/01 §5.3）：和暂停一样要电脑密码；守护程序撤了防护再退出界面。还没开始保护时没什么要撤的，直接退。
  A.quitAllGo = function () {
    S.dialog = null; P.render();
    withOwner('彻底退出后你的真实 IP 会暴露。确认是你本人后继续。', '没有退出，防护照常').then(function (ok) {
      if (!ok) return;
      quitting = true;
      return call({ type: 'pause', minutes: 10, reason: 'quit' }).catch(function (e) {
        if (!/还没开始保护|还没激活/.test(msg(e))) throw e;
      }).then(function () { return T.core.invoke('quit_app'); });
    }).catch(function (e) { quitting = false; P.toast('没有退出：' + msg(e), 'info'); });
  };
  A.pickRoute = function (i) {
    i = +i;
    var id = lineIds[i];
    if (!id) return;
    act({ type: 'pick_line', id: id }, '已切换至线路 ' + (ROUTES[i] ? ROUTES[i].n : i + 1) + '，已固定使用');
  };
  A.routeMode = function (m) {
    if (m === 'auto') act({ type: 'auto_line' }, '已改回自动选择线路');
    else { S.routeMode = 'manual'; P.render(); }
  };
  A.routeRefresh = A.pmRefresh = A.trayRefresh = function () { act({ type: 'refresh' }, '线路已重新测过'); };
  // 测速（docs/01 §5.2）：只测当前线路；冷却、正在大量下载时守护程序回一句人话，照说。进度看状态里的 speed_testing。
  A.speedTest = function () {
    if (S.speedTesting || S.busy.speed) return;
    S.busy.speed = true; S.speedTesting = true; P.render();
    call({ type: 'speed_test' }).then(function (r) {
      S.busy.speed = false; S.speedTesting = false;
      if (ROUTES[S.route] && r && r.measured) ROUTES[S.route].speed = { mbps: r.down_bps * 8 / 1000000, ago: 0, measured: true };
      if (r && r.measured) results.S4 = { id: 'S4', verdict: 'info', detail: '最近测速 ' + (r.down_bps * 8 / 1000000).toFixed(2) + ' Mbps' };
      P.render(); poll();
    }).catch(function (e) { S.busy.speed = false; S.speedTesting = false; P.render(); P.toast('测速未完成：' + msg(e), 'info'); });
  };
  A.refreshAny = function () { A.go(S.status === 'pm' ? 'overview' : 'routes'); A.routeRefresh(); };
  A.routeBad = A.pmSwitch = function () { act({ type: 'report_bad_line' }, '已换一条线路'); };
  A.restart = function () { act({ type: 'restart_kernel' }, '正在重新启动保护'); };

  /* ---------- 安全检查 ---------- */
  // 对外名称 ↔ 检查项编号（docs/03 各表的顺序）。
  var IDS = {
    '系统版本': 'E1', '防护程序权限': 'E2', '系统时间': 'E3', '其他网络软件': 'E4', '系统防火墙可用': 'E5', '安全软件和网络过滤': 'E6',
    '能建立独享线路': 'E7', '磁盘空间': 'E8', '服务器连接': 'E9', '后台运行权限': 'E10', '开机自动运行': 'E10',
    '防护规则': 'P1', '系统防火墙已开启': 'P2', '防护组件': 'P3', '独享线路已接入': 'P4', '所有流量走独享线路': 'P5', '网址解析': 'P6',
    '浏览器保护设置': 'P7', '暂停状态': 'P8',
    '出口 IP': 'L1', '绕开独享线路的连接': 'L2', '绕开独享线路的网址查询': 'L3', '网址解析不经过国内': 'L4', '浏览器看到的 IP': 'L5', '浏览器绕开独享线路': 'L6',
    '另一种网络地址': 'L7', '绕开独享线路的软件': 'L8', '1 分钟网络观察': 'L9', '浏览器实测': 'L10',
    '时区': 'C1',
    '授权和到期': 'S1', '配置是否最新': 'S2', '线路质量': 'S3', '线路速度': 'S4', '专用通道': 'S5', '最近一次同步': 'S6'
  };
  var results = {};
  var STATE = { pass: 'ok', info: 'ok', warn: 'warn', fail: 'danger' };

  // 守护程序还没回结果时，界面停在最后一个阶段（按实际进度，不固定时长）。
  var pending = false;
  window.CC_CHECK_PENDING = function () { return pending; };

  function startDoctor(deep) {
    results = {};
    pending = true;
    loadBrowsers();
    call({ type: 'doctor', deep: !!deep }).then(function (r) {
      pending = false;
      if (r.id) P.set({ checkId: r.id });
      (r.items || []).forEach(function (it) { results[it.id] = it; });
      P.render();
      loadCheckHist();
    }).catch(function (e) { pending = false; P.toast('安全检查没做完：' + msg(e), 'info'); });
    call({ type: 'check_conflicts' }).then(function (r) {
      var items = (r.items || []).filter(function (c) { return c.severity === 'warn'; });
      results.E4 = items.length
        ? { id: 'E4', verdict: 'fail', detail: items.map(function (c) { return c.message; }).join('；') }
        : r.complete === false ? { id: 'E4', verdict: 'skipped', detail: '已检查系统代理；其他网络软件尚未完成检测' } : { id: 'E4', verdict: 'pass', detail: '正常' };
      P.render();
    }).catch(function () {});
  }

  /* ---------- 浏览器实测（docs/01 §5.3） ---------- */
  // 装了哪些、测过的结果在守护程序那边；打开浏览器由 App 壳做（以当前用户的身份），结果由页面交给守护程序，这里隔几秒问一次。
  S.browsers = [];
  var browserTesting = {};
  function loadBrowsers() {
    return call({ type: 'browser_results' }).then(function (r) {
      var done = {};
      (r.checks || []).forEach(function (it) { results[it.id] = it; });
      (r.items || []).forEach(function (i) { done[i.browser] = i; });
      S.browsers = (r.installed || []).map(function (n) {
        var t = browserTesting[n], d = done[n];
        if (t && d && d.at >= t) { delete browserTesting[n]; }
        return { n: n, st: browserTesting[n] ? 'testing' : d ? d.exposure : 'none' };
      });
      if (!busy()) P.render();
    }).catch(function () {});
  }
  var dialogOrig = A.dialog;
  A.dialog = function (t) { if (t === 'browsers') loadBrowsers(); return dialogOrig.apply(this, arguments); };
  A.browserTest = function (i) {
    var b = S.browsers[+i];
    if (!b) return;
      var at = Math.floor(Date.now() / 1000), tries = 0;
    browserTesting[b.n] = at; b.st = 'testing'; P.render();
    T.core.invoke('test_browser', { browser: b.n }).then(function () {
      (function poll() {
        loadBrowsers().then(function () {
          if (!browserTesting[b.n]) return;
          // 两分钟没回：测试页已经关了，算这次没查成。
          if (++tries > 60) { delete browserTesting[b.n]; loadBrowsers(); P.toast(b.n + ' 没查完，可以再查一次', 'info'); return; }
          setTimeout(poll, 2000);
        });
      })();
    }).catch(function (e) { delete browserTesting[b.n]; loadBrowsers(); P.toast('打不开浏览器：' + msg(e), 'info'); });
  };

  // 最近的检查（docs/01 §5.8）：守护程序存在本机的最近 10 次；点开一条按那次的结果显示（和体检结果共用 CC_CHECK）。
  var checkRecs = [];
  function loadCheckHist() {
    return call({ type: 'check_history' }).then(function (r) {
      checkRecs = r.items || [];
      S.checkHist = checkRecs.map(function (c) { return { at: evTimeFull(c.at), kind: c.kind, problems: c.problems || 0, id: esc(c.id || '') }; });
      if (!busy()) P.render();
    }).catch(function () {});
  }
  function evTimeFull(at) {
    var d = new Date(at * 1000), now = new Date(), y = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
    if (d.toDateString() === now.toDateString()) return '今天 ' + hhmm(d);
    if (d.toDateString() === y.toDateString()) return '昨天 ' + hhmm(d);
    return (d.getMonth() + 1) + ' 月 ' + d.getDate() + ' 日 ' + hhmm(d);
  }
  var checkHistOrig = A.checkHist;
  A.checkHist = function (i) {
    var rec = checkRecs[+i];
    if (!rec) return;
    results = {};
    (rec.items || []).forEach(function (it) { results[it.id] = it; });
    var opened = checkHistOrig.apply(this, arguments);
    // The browser section reports the current exit, including tests completed after
    // this check. Refresh its IP/WebRTC rows together with the live browser summary.
    loadBrowsers();
    return opened;
  };

  window.CC_CHECK = function (GROUPS) {
    var rank = { danger: 0, warn: 1, ok: 2, skip: 3 };
    return GROUPS.map(function (g) {
      var items = g[1].map(function (n) {
        // 浏览器实测不在体检里跑（要弹浏览器窗口）：这一行看实测结果，一直给「检查浏览器」。
        if (IDS[n] === 'L10') { var bw = P.browserSum(); return { n: n, s: bw.s, d: bw.d, act: ['检查浏览器', 'dialog:browsers'] }; }
        var r = results[IDS[n]];
        if (!r) return null;
        // 没查的如实显示，不算进正常：守护程序跳过的，和出口 IP 这种这次没比对的（docs/03 §4）。
        if (r.verdict === 'skipped') return { n: n, s: 'skip', d: '这次没查：' + esc(r.detail) };
        if ((IDS[n] === 'L1' || IDS[n] === 'L5') && r.verdict === 'info') return { n: n, s: 'skip', d: esc(r.detail) };
        // 「浏览器保护设置」记成「提示」的是名单以外的浏览器（docs/03 P7）：显示成提示，不给「安装保护设置」。
        var hint = IDS[n] === 'P7' && r.verdict === 'info';
        var it = { n: n, s: hint ? 'warn' : (STATE[r.verdict] || 'ok'), d: r.verdict === 'pass' && IDS[n] !== 'L5' ? '正常' : esc(r.detail) };
        if (IDS[n] === 'P7' && !hint && it.s !== 'ok' && canInstallProfile) it.act = ['安装保护设置', 'webrtcInstall'];
        if (IDS[n] === 'E3' && it.s !== 'ok') it.act = ['同步时间', 'syncTime'];
        if (IDS[n] === 'E10' && it.s !== 'ok' && canInstallProfile) it.act = ['打开系统设置', 'bgOpen'];
        return it;
      }).filter(Boolean);
      items.sort(function (a, b) { return rank[a.s] - rank[b.s]; });
      return { n: g[0], items: items };
    }).filter(function (g) { return g.items.length > 0; });
  };

  ['checkStart', 'runCheck', 'checkAgain'].forEach(function (k) {
    var orig = A[k];
    if (orig) A[k] = function () { startDoctor(false); return orig.apply(this, arguments); };
  });
  // 首次向导最后一步的安全检查也由守护程序做。
  var wizNextOrig = A.wizNext;
  A.wizNext = function () {
    var r = wizNextOrig.apply(this, arguments);
    if (S.check && S.check.ctx === 'wizard' && S.check.phase === 'run') startDoctor(false);
    return r;
  };
  if (A.checkDeep) {
    var deepOrig = A.checkDeep;
    A.checkDeep = function () { startDoctor(true); return deepOrig.apply(this, arguments); };
  }

  /* ---------- 浏览器保护设置（P7） ---------- */
  // macOS：引导用户装描述文件（系统不让程序自己装）；Windows 由守护程序直接写浏览器策略，不用引导。
  // Windows 由后台服务直接写 Chrome / Edge 的策略；Mac 打开描述文件。两边都有「安装保护设置」按钮。
  var canInstallProfile = true;
  var profileHinted = false;
  A.webrtcInstall = function () {
    T.core.invoke('install_webrtc_profile').then(function () {
      P.toast('已打开系统设置：在「描述文件」里点「安装」，装好后重新做一次安全检查');
    }).catch(function (e) { P.toast('没打开：' + msg(e), 'info'); });
  };
  // 首次向导「浏览器」这一步：打开系统设置；守护程序的 P7 不再报问题就算装好了。
  A.wizWebrtc = function () {
    S.busy.wz = { text: '正在打开系统设置' }; P.render();
    T.core.invoke('install_webrtc_profile').then(function () {
      S.busy.wz = null; if (S.wizard) S.wizard.webrtc = 'opened'; P.render();
    }).catch(function (e) { S.busy.wz = null; P.render(); P.toast('没打开：' + msg(e), 'info'); });
  };
  var wizDoneOrig = A.wizDone;
  A.wizDone = function () {
    store('cc.wizard.pending', false);
    // 向导里的安全检查有问题：完成后直接去安全检查页看结果。
    var c = S.check, bad = Object.keys(results).some(function (k) { return results[k].verdict === 'warn' || results[k].verdict === 'fail'; });
    var r = wizDoneOrig.apply(this, arguments);
    if (bad && c && c.phase === 'result') { c.ctx = 'main'; S.check = c; S.page = 'check'; P.render(); }
    return r;
  };
  function hintProfile(st) {
    var p7 = (st.checks || []).filter(function (c) { return c.id === 'P7'; })[0];
    if (S.wizard && S.wizard.webrtc === 'opened' && !p7) { S.wizard.webrtc = 'done'; P.toast('浏览器保护设置已安装'); P.render(); }
    if (profileHinted || !canInstallProfile || S.wizard) return;
    if (!p7) return;
    profileHinted = true;
    P.toast('浏览器还没装保护设置：在「安全检查」的「浏览器保护设置」一项点「安装保护设置」', 'info');
  }

  /* ---------- 账户 ---------- */
  function field(key) {
    var el = document.querySelector('[data-keep="' + key + '"]');
    return el ? (el.type === 'checkbox' ? el.checked : el.value.trim()) : '';
  }
  // 不在今年的带上年份（「2027 年 10 月 29 日」），免得和今天混淆。
  function day(t) { var d = new Date(t * 1000), y = d.getFullYear(); return (y !== new Date().getFullYear() ? y + ' 年 ' : '') + (d.getMonth() + 1) + ' 月 ' + d.getDate() + ' 日'; }
  /* 服务端的「长期」（9999-12-31）：软件买断 */
  var FOREVER = 253402300799;
  function when(t) {
    var d = new Date(t * 1000), now = new Date();
    return d.toDateString() === now.toDateString() ? hhmm(d) : day(t) + ' ' + hhmm(d);
  }
  function span(s) {
    if (s < 60) return '不到 1 分钟';
    var d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
    return d ? d + ' 天 ' + h + ' 小时' : h ? h + ' 小时 ' + m + ' 分' : m + ' 分钟';
  }
  function store(k, v) { try { if (v === undefined) return JSON.parse(localStorage.getItem(k) || 'null'); localStorage.setItem(k, JSON.stringify(v)); } catch (e) { return null; } }
  // 分 → 「12,800.50」（余额、退款金额）。
  function money(c) { return (c / 100).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ','); }

  var account = null;
  // 没有续费申请时的写法：macOS 原型是 'none'，Windows 原型是 null（它拿真假判断有没有申请）。
  var NO_RENEW = S.renew.phase === null ? null : 'none';
  var dismissedBanner = store('cc.banner.dismissed') || [];
  var bannerNow = null;

  // 公告：维护通知用原型的「维护」样式，其余用「普通」样式；内容是后台写的。
  // 连不上服务端（docs/01 §3.1）：3 天黄灯、30 天红灯，借公告的位置显示；防护和线路照常。
  window.CC_BANNER = function () {
    if (!bannerNow) return null;
    if (bannerNow.offline) return ['warning', bannerNow.days >= 30 ? ' important danger' : ' important', '已 ' + bannerNow.days + ' 天没连上服务器', bannerNow.days >= 30 ? '防护正常。请联系客服' : '防护正常，线路照常可用'];
    // 第五项是「查看」跳到「消息 → 公告」展开的那一条。
    var id = 'N-' + bannerNow.id;
    if (bannerNow.maintenance) return ['clock', ' important', esc(bannerNow.title), esc(bannerNow.body), id];
    return bannerNow.important ? ['warning', ' important', esc(bannerNow.title), esc(bannerNow.body), id] : ['info', '', esc(bannerNow.title), esc(bannerNow.body), id];
  };
  function esc(x) { return String(x).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

  /* ---------- 全套、单独软件、老套餐（docs/01 §3.1） ---------- */
  // 接着自己出口的是单独软件（全套到期退回来、刚接上出口的，服务端下一次心跳才改过来）；全套到期没续的还是全套的写法。
  // 服务端没说是哪种（旧服务端）的照老套餐写；账户信息还没拿到时照原型（全套）。
  function suiteKind() {
    // 免费试用也接自己的出口，但它会到期（docs/01 §3.1「免费试用」）。
    if (account && account.suite && account.suite.kind === 'trial') return 'trial';
    if (S.ownExit === 'waiting' || S.ownExit === 'connected') return 'soft';
    if (S.ownExit === 'lapsed') return 'suite';
    if (!account) return 'suite';
    var k = account.suite && account.suite.kind;
    return k === 'suite' || k === 'soft' ? k : 'old';
  }
  function suiteWord() { return suiteKind() === 'old' ? '软件' : '全套'; }
  window.CC_SUITE_WORD = suiteWord;
  // 续费面板「续什么」：全套只有全套（线路和号一起，suite）和流量；老套餐照旧是软件（线路的到期，node）和流量。
  // macOS 原型的选项是 { n, what }，Windows 的是 [键, 名字, 续什么]（全套那项的键是 all，原型的「续费」按它预选）。
  var WIN_OPTS = Array.isArray(P.renewOpts()[0]);
  window.CC_RENEW_OPTS = function () {
    var dur = suiteKind() === 'old' ? ['软件', 'node'] : ['全套', 'suite'];
    return WIN_OPTS ? [['all', dur[0], dur[1]], ['traffic', '流量', 'traffic']] : [{ n: dur[0], what: dur[1] }, { n: '流量', what: 'traffic' }];
  };
  window.CC_RENEW_HELP = function () {
    return suiteKind() === 'old' ? '软件到期顺延，没过期的从原到期日接着算，过期了从续费那天算；流量只在本期有效'
      : '全套是线路和 Claude 账号一起续，从原到期日按月接着算；流量只在本期有效';
  };
  // 「我的账号」灰卡片（docs/01 §5.12）：服务端说能补号（全套、没到期、号被封或作废了）给「补号」，否则「重新开通全套」。
  // 旧服务端没给 suite 的照原型：停用的、全套没到期给「补号」。
  window.CC_CAN_REFILL = function (a) {
    if (account && account.suite) return !!account.suite.can_refill;
    return (a.st === 'refunded' || a.st === 'stopped') && !(S.status === 'pm' && S.reason === 'expired') && suiteKind() !== 'soft';
  };
  // 断网页（docs/01 §4 第 8 步）：全套到期用原型的（接入你的出口、重新开通全套、暂停防护）；老套餐到期照原样续费，
  // 提交了续费申请的写「已提交」、按钮换成「查看进度」。
  var PM_SUITE = P.PM.expired;
  function renewingNow() { return !!S.renew.phase && S.renew.phase !== 'none'; }
  var PM_OLD = {
    short: '软件已到期',
    get desc() { return renewingNow() ? '网络已断开。续费申请已提交，开通后自动恢复' : '软件已于 ' + S.exp.date + '到期。为了不暴露真实 IP，网络已断开。续费后自动恢复。'; },
    get btns() { return renewingNow() ? [['primary', '查看进度', 'renewProgress']] : [['primary', '续费', 'renewWeb'], ['text', '已经线下付了钱？', A.renewOpen ? 'renewOpen' : 'renew']]; }
  };
  Object.defineProperty(P.PM, 'expired', { configurable: true, enumerable: true, get: function () { return suiteKind() === 'old' ? PM_OLD : PM_SUITE; } });

  function applyAccount(a) {
    if (!a || typeof a !== 'object') return;
    // 激活过的电脑不会变回没激活：守护程序刚连上、还没读好时回的空账户不算数，留着上一次的（不显示成「全套 · 0 天」）。
    // 真的没激活由状态（not_activated）进激活页，那时再收。
    if (!a.activated && account && account.activated && S.screen !== 'activate') return;
    account = a;
    if (!a.activated) return;
    if (S.screen === 'activate' && !S.busy.act) { S.screen = 'main'; S.page = 'overview'; S.wizard = null; }
    if (a.account_id) P.set({ account: a.account_id });
    if (!S.editingContact) S.contact = a.contact || '';
    // 单独软件 · 自带出口（docs/01 §5.2）：waiting 还没接出口 / connected 在用自己的出口 / upgraded 升级到了全套 /
    // lapsed 全套到期没续、还没接出口（照旧断网，§4 第 8 步）。
    S.ownExit = a.own_exit || '';
    applyExits(a);
    var end = Math.min(a.software_expires_at || Infinity, a.node_expires_at || Infinity);
    S.licensePermanent = isFinite(end) ? end >= FOREVER : null;
    if (isFinite(end) && end < FOREVER) S.exp = { date: day(end), days: a.days_left == null ? 0 : a.days_left };
    applyInfo(a);
    // 余额（docs/01 §5.7）：这台电脑归到网站账户才有；没有就不显示那张卡。
    S.balance = a.balance_cents == null ? null : money(a.balance_cents);
    // 到期前 7 天起横幅写日期（Windows 原型靠 expireSoon 这个开关），只有前 1 天写「明天」。
    S.alerts.expire1d = a.days_left === 1 && !store('cc.expire1d.' + end);
    S.alerts.expireSoon = a.days_left != null && a.days_left <= 7;
    applyTrial(a, end);
    // Pro 全套能升到 Max 5x（服务端说能升才有，全套到期了不给）；号在准备（line / queued / issuing，没有就是空）。
    var su = a.suite || {};
    S.tierUp = suiteKind() === 'suite' && !!su.can_tier_up && !(S.status === 'pm' && S.reason === 'expired');
    S.aiPrep = su.ai_preparing || '';
    // 准备中页写的激活时间：今天的写「今天 14:20」。
    var bd = a.bound_at ? new Date(a.bound_at * 1000) : null;
    S.prepAt = bd ? (bd.toDateString() === new Date().toDateString() ? '今天 ' : day(a.bound_at) + ' ') + hhmm(bd) : '';
    var phase = a.renewal === 'needs_info' ? 'needinfo' : a.renewal === 'processing' ? 'processing' : NO_RENEW;
    if (S.renew.phase !== phase && !S.renew.busy) S.renew = { phase: phase, at: '', items: S.renew.items || [], sel: null, busy: false };
    P.CHANNELS.length = 0;
    // [显示名（带备注）, 号码, 类型, 名称, 备注]：类型是 QQ 群号的第一条另外显示在「消息 → 公告」顶上。
    (a.channels || []).forEach(function (c) { P.CHANNELS.push([esc(c.label + (c.note ? '（' + c.note + '）' : '')), esc(c.value), c.kind, esc(c.label), esc(c.note || '')]); });
    bannerNow = (a.announcements || []).filter(function (n) { return dismissedBanner.indexOf(n.id) < 0; })[0] || null;
    notifyNotices(a.announcements || []);
    if (S.alerts.expire1d) notifyOnce('expire.' + end, suiteWord() + '明天到期', '点「续费」在网页上付款，付完自动顺延');
    var od = a.offline_days, level = od >= 30 ? 30 : od >= 3 ? 3 : 0;
    // 「知道了」只关这一级：到 30 天再提示一次。
    if (level && offlineDismissed !== level) bannerNow = { id: 'offline', offline: true, days: od };
    S.banner = bannerNow ? (bannerNow.maintenance ? 'maint' : bannerNow.offline ? 'important' : 'normal') : null;
    applyInbox(a);
    aiDays(); aiRefund();
  }
  // 免费试用（docs/01 §3.1「免费试用」）：原型的 S.trial（on 试用中 / expired 到期断网 / stopped 停用）和到期日；
  // 全套的到期横幅、「明天到期」不给试用（它有自己的横幅），前 1 天发一次系统通知。
  // 试用已经到期（按账户信息里的到期日）。
  function trialOver() {
    if (suiteKind() !== 'trial') return false;
    var end = Math.min(account.software_expires_at || Infinity, account.node_expires_at || Infinity);
    return isFinite(end) && end <= Date.now() / 1000;
  }
  function applyTrial(a, end) {
    if (suiteKind() !== 'trial') { S.trial = ''; return; }
    var expired = isFinite(end) && end <= Date.now() / 1000;
    S.trial = S.screen === 'trialStopped' ? 'stopped' : expired ? 'expired' : 'on';
    S.trialEnd = { date: isFinite(end) ? day(end) : '—', days: expired || a.days_left == null ? 0 : a.days_left };
    S.alerts.expire1d = false; S.alerts.expireSoon = false;
    if (S.trial === 'on' && a.days_left === 1) notifyOnce('trial1d.' + end, '免费试用明天到期', '买了单独软件就能一直用');
  }
  // 个人中心、流量卡片、断网页里的套餐、流量、重置日、本机设备、绑定日期、维护结束时间（原型里是演示文字）。
  // 一位小数，整数不带「.0」（「0 GB」「12 GB」「38.2 GB」）。
  function gb(b) { return b == null ? '—' : (b / 1073741824).toFixed(1).replace(/\.0$/, '') + ' GB'; }
  // 用掉的量不到 1 GB 时按 MB 写（「0 GB」看着像没在统计）。
  function usedText(b) { return b == null ? '—' : b < 1073741824 ? Math.round(b / 1048576) + ' MB' : gb(b); }
  function applyInfo(a) {
    var I = P.INFO, lim = a.traffic_limit_bytes, used = a.traffic_used_bytes;
    I.plan = esc(a.plan || '—');
    I.limit = lim ? gb(lim).replace('.0 GB', ' GB') : '不限';
    I.used = usedText(used);
    I.pct = lim && used != null ? Math.min(100, Math.round(used * 1000 / lim) / 10) : 0;
    I.reset = a.traffic_reset_at ? day(a.traffic_reset_at) : '—';
    // 每天用量（docs/01 §5.7）：服务端按天汇总、随心跳下发，最后一天是今天。
    var from = /^(\d{4})-(\d{2})-(\d{2})$/.exec(a.traffic_days_from || '');
    I.daily = from ? (a.traffic_days || []).map(function (b) { return b / 1073741824; }) : [];
    I.dailyFrom = from ? +from[2] + ' 月 ' + +from[3] + ' 日' : '';
    I.usedGB = (used || 0) / 1073741824;
    I.limitGB = lim ? lim / 1073741824 : null;
    I.resetLeft = a.traffic_reset_at ? Math.max(0, Math.ceil((a.traffic_reset_at - Date.now() / 1000) / 86400)) : null;
    // 电脑名去掉域名后缀（「macbook.local」「xxx.shared」只留前一段）。
    I.device = esc(a.device || '本机').replace(/^([^\s.·]+)\.[^\s·]+/, '$1').replace(/\bwindows \S+/, 'Windows').replace(/\bmacos \S+/, 'macOS');
    I.bound = a.bound_at ? day(a.bound_at) : '—';
    var now = Date.now() / 1000;
    var m = (a.announcements || []).filter(function (n) { return n.maintenance; })[0];
    I.maintEnd = m ? when(m.maint_end || m.ends_at) : '';
  }

  // 没拿到账户信息（刚打开、守护程序刚连上、上一次出错）时每 3 秒再要一次，拿到为止（平时 30 秒一次）；拿不到时界面留着上一次的。
  var accountFails = 0, accountLoading = 0;
  window.CC_RELOAD_ACCOUNT = function () { return loadAccount(); };
  function loadAccount() {
    accountLoading++;
    return call({ type: 'account' }).then(function (r) { accountFails = 0; applyAccount(r); if (!busy()) P.render(); })
      .catch(function (e) { accountFails++; console.warn('[桥接] 账户：' + msg(e)); })
      .then(function () { accountLoading--; });
  }

  /* ---------- 多个出口（docs/01 §5.2「多个出口」） ---------- */
  // 账户信息的 exits → 原型的 EXITS（一个出口一张卡）、S.exits（old：老套餐，我们分配的 + 自己加的；soft：单独软件、免费试用，
  // 只有自己加的；''：全套、旧服务端、单独软件还没接出口，没有出口一栏）、S.exit（当前那个是第几个）。
  // 配置包里只有当前出口的线路：信号格只画当前出口的（看当前线路），别的出口不画；出口 IP 服务端没给的用当前线路的。
  // 切换、添加、换链接、删除都经守护程序找服务端，本机不改线路、不自己选出口；服务端的说明原样显示。
  var EXITS = P.EXITS;
  function shownExits() { return S.exits === 'soft' ? EXITS.filter(function (e) { return e.own; }) : EXITS; }
  function curExit() { return S.exits ? shownExits()[S.exit] || null : null; }
  function exitOf(e) {
    var o = { id: e.id, own: !!e.own, sub: !!e.sub, n: e.lines || 0, current: !!e.current, raw: { name: e.name || '', location: e.location || '', ip: e.exit_ip || '', err: e.exit_ip_error || '' } };
    // 服务器经这个出口测不通时的原因（「连不上出口地址」「出口连上了但不转发流量」…），出口一通就没了。
    o.err = esc(o.raw.err);
    // 本机直连：这台电脑直接连这个出口（卖家节点只允许国内连接时用），不经香港中转。
    o.direct = !!e.direct;
    // 服务器最近 6 分钟内经这个出口测通了（换出口、切回时用）。
    o.healthy = !!(e.health && e.health.ok && e.health.at && Date.now() / 1000 - e.health.at < 360);
    o.name = o.raw.name;
    o.city = esc(o.raw.location || '—');
    Object.defineProperty(o, 'ip', { enumerable: true, get: function () { return esc(o.raw.ip || (o.current && curIp !== '-' ? curIp : o.direct ? '选用后显示 IP' : '—')); } });
    Object.defineProperty(o, 'sig', { enumerable: true, get: function () { var r = ROUTES[S.route]; return o.current && S.status === 'ok' && r && typeof r.sig === 'number' ? r.sig : null; } });
    return o;
  }
  function markCurrent(id) {
    EXITS.forEach(function (e) { e.current = e.id === id; });
    var i = -1;
    shownExits().forEach(function (e, k) { if (e.current) i = k; });
    S.exit = i < 0 ? 0 : i;
  }
  function applyExits(a) {
    var x = a.exits, list = (x && x.list) || [], k = suiteKind();
    EXITS.length = 0;
    list.forEach(function (e) { EXITS.push(exitOf(e)); });
    S.exits = !list.length || k === 'suite' ? '' : (k === 'soft' || k === 'trial') && list.every(function (e) { return e.own; }) ? 'soft' : 'old';
    var cur = list.filter(function (e) { return e.current; })[0];
    markCurrent(cur ? cur.id : null);
    // 「添加出口」：服务端说能加才有，自己加的到了上限（默认 5 个）也没有。
    P.set({ exitMaxOwn: x && x.can_add ? (x.max_own || 5) : 0 });
    // 自动换过出口、原来那个又测通了：通知一次（不自动切回，免得网站看到的 IP 来回变）。
    var F = S.failover;
    if (F) {
      var back = EXITS.filter(function (e) { return e.name === F.from && !e.current && e.healthy; })[0];
      if (back) notifyOnce('recovered.' + F.at, '「' + F.from + '」恢复了', '在「线路」页点「切回」就能换回去');
    }
  }
  function exitName(e) { return esc(e.raw.name || '出口'); }
  A.pickExit = function (i) {
    var L = shownExits(), e = L[+i], was = curExit();
    if (!e || S.busy.route) return;
    if (e === was) { P.toast('正在用「' + exitName(e) + '」'); return; }
    S.busy.route = { text: '正在切换到出口「' + exitName(e) + '」' }; P.render();
    call({ type: 'select_exit', id: e.id }).then(function (r) {
      delete S.busy.route; routeAt = Date.now();
      var ip = (r && r.exit_ip) || e.raw.ip, loc = (r && r.location) || e.raw.location;
      if (ip) e.raw.ip = ip;
      if (loc) { e.raw.location = loc; e.city = esc(loc); }
      markCurrent(e.id);
      P.toast('已切到「' + exitName(e) + '」' + (ip ? '，出口 IP 变为 ' + esc(ip) : '') + (loc && (!was || was.raw.location !== loc) ? '，所在地变成' + esc(loc) : ''));
      histAt = 0; loadAccount(); poll();
    }).catch(function (err) { delete S.busy.route; P.render(); P.toast(esc(msg(err)), 'info'); });
  };
  // 添加出口、更换链接（原型的对话框；idx 有值是换这个出口的链接）。名字没填照原型起一个「我的出口 N」。
  A.exitAddGo = function () {
    var D = S.dialog;
    if (!D || D.type !== 'addexit' || D.busy) return;
    var re = D.idx != null ? shownExits()[D.idx] : null, links = field('exitlinks');
    if (!links) { D.err = '请粘贴链接或订阅地址'; P.render(); return; }
    var req = { type: 'own_exit', links: links };
    if (re) req.exit_id = re.id;
    else { req.add = true; req.name = field('exitname') || '我的出口 ' + (EXITS.filter(function (e) { return e.own; }).length + 1); if (field('exitdirect') === true) req.direct = true; }
    D.busy = true; D.err = ''; P.render();
    call(req).then(function (r) {
      if (S.dialog === D) S.dialog = null;
      var skipped = (r && r.skipped) || [], n = (r && r.lines) || 0;
      var sk = skipped.length ? '，跳过 ' + skipped.length + ' 条（' + esc(skipped[0]) + (skipped.length > 1 ? ' 等' : '') + '）' : '';
      P.toast(re ? '「' + exitName(re) + '」的链接换好了：' + n + ' 条线路' + sk : '出口加好了：' + n + ' 条线路' + sk + '，点它就能切过去');
      loadAccount(); poll();
    }).catch(function (err) { if (S.dialog === D) { D.busy = false; D.err = esc(msg(err)); P.render(); } });
  };
  // 出口连不上时自动换：开 / 关
  A.autoExit = function () {
    var to = S.autoExit === false;
    call({ type: 'set_auto_exit', on: to }).then(function (r) {
      S.autoExit = !!(r && r.on); P.render();
      P.toast(S.autoExit ? '已打开：出口连不上时自动换到能用的出口并通知你' : '已关闭：出口连不上时直接断网提醒，IP 不会自己变');
    }).catch(function (e) { P.toast('没改成：' + msg(e), 'info'); });
  };
  A.modeChoose = function (which) {
    var listed = which === 'claude';
    S.dialog = null; P.render();
    call({ type: 'choose_mode', listed: listed }).then(function () {
      S.listed = listed; P.render();
      P.toast(listed ? '已改为只让 Claude 走出口，其他软件照常上网' : '保持全部走出口');
      if (typeof loadRoute === 'function') loadRoute();
    }).catch(function (e) { modeAsked = false; P.toast('没改成：' + msg(e), 'info'); });
  };
  A.autoExitOn = function () { if (S.autoExit === false) A.autoExit(); };
  // 本机直连 ⇄ 经香港中转：正在用的出口改了走法，防护会重新建一次线路（几秒，期间真实 IP 照样挡住）。
  A.exitDirect = function (i) {
    var e = shownExits()[+i];
    if (!e || !e.own || S.busy.route) return;
    var to = !e.direct;
    S.busy.route = { text: to ? '正在把「' + exitName(e) + '」改为本机直连' : '正在把「' + exitName(e) + '」改为经香港中转' }; P.render();
    call({ type: 'set_exit_direct', id: e.id, direct: to }).then(function () {
      delete S.busy.route; e.direct = to; P.render();
      P.toast(to ? '「' + exitName(e) + '」改为本机直连：这台电脑直接连它，不经香港' : '「' + exitName(e) + '」改为经香港中转');
      histAt = 0; loadAccount(); poll();
    }).catch(function (err) { delete S.busy.route; P.render(); P.toast(esc(msg(err)), 'info'); });
  };
  // 删除自己加的出口：照原型先确认；删的是正在用的，服务端先切到另一个（回新的当前出口）。
  A.exitDel = function (i) {
    var e = shownExits()[+i];
    if (!e || !e.own || S.busy.route) return;
    var cur = e === curExit();
    P.confirmFirst(['删除出口「' + exitName(e) + '」？', '这个出口的线路从列表里去掉，链接要用时再加。' + (cur ? '你正在用它，会先切到另一个出口。' : ''), '删除'], function () {
      S.busy.route = { text: '正在删除出口「' + exitName(e) + '」' }; P.render();
      call({ type: 'delete_exit', id: e.id }).then(function (r) {
        delete S.busy.route;
        var keep = cur ? null : curExit(), k = EXITS.indexOf(e);
        if (k >= 0) EXITS.splice(k, 1);
        var now = r && r.current != null ? EXITS.filter(function (x) { return x.id === r.current; })[0] : keep;
        markCurrent(now ? now.id : null);
        P.toast('已删除「' + exitName(e) + '」' + (cur && now ? '，切到了「' + exitName(now) + '」' : ''));
        histAt = 0; loadAccount(); poll();
      }).catch(function (err) { delete S.busy.route; P.render(); P.toast(esc(msg(err)), 'info'); });
    });
  };
  var offlineDismissed = 0;
  var dismissOrig = A.dismiss;
  A.dismiss = function (what) {
    if (what === 'banner' && bannerNow && bannerNow.offline) { offlineDismissed = bannerNow.days >= 30 ? 30 : 3; bannerNow = null; }
    else if (what === 'banner' && bannerNow) { dismissedBanner.push(bannerNow.id); store('cc.banner.dismissed', dismissedBanner.slice(-50)); bannerNow = null; }
    if (what === 'expire1d' && account) store('cc.expire1d.' + Math.min(account.software_expires_at, account.node_expires_at), true);
    return dismissOrig.apply(this, arguments);
  };

  A.activate = function () {
    var code = field('act-code'), account = field('act-contact'), contact = account && field('act-contact-kind') + ' ' + account, order = field('act-order'), e = {};
    if (!code) e.code = '请输入授权码';
    if (!contact) e.contact = '请填写联系方式，微信 / QQ / 邮箱都可以';
    if (!field('act-agree')) e.agree = '请先阅读并同意用户协议';
    S.actErr = e;
    if (Object.keys(e).length) { P.render(); return; }
    S.busy.act = { text: '正在验证授权码' }; P.render();
    var slowHint = setTimeout(function () { if (S.busy.act) { S.busy.act = { text: '网络较慢，正在激活，请稍等，不要重复点…' }; P.render(); } }, 10000);
    // 激活成功的提示按套餐写（账户信息拿回来才知道）：全套「线路开通中」，单独软件、免费试用「下一步接入你的出口」。
    var done = function (text) {
      activationEpoch++;
      clearTimeout(slowHint); S.busy.act = null; S.actErr = {}; S.screen = 'main'; S.page = 'overview'; S.status = 'prep';
      // 激活后直接进概览，不走引导；防护由客户自己点「开启防护」。
      store('cc.wizard.pending', false);
      P.render();
      poll();
      loadAccount().then(function () {
        var k = suiteKind();
        P.toast(text || (k === 'soft' || k === 'trial' ? '已激活，下一步接入你的出口' : '已激活，线路开通中'));
      });
    };
    var fail = function (err) {
      var m = msg(err); clearTimeout(slowHint); S.busy.act = null;
      // 联系方式的问题写在联系方式下面（common::contact 的提示都以这几个词开头）。
      S.actErr = /^(联系方式|微信|QQ|邮箱|Telegram)/.test(m) ? { contact: m } : { code: m };
      P.render();
    };
    call({ type: 'activate', code: code, contact: contact, order_no: order }).then(function () { done(''); }).catch(function (err) {
      // 授权码激活过：重装系统的情况，按原授权码自助恢复再试一次。
      if (!/已经激活过/.test(msg(err))) { fail(err); return; }
      S.busy.act = { text: '正在恢复原来的账户' }; P.render();
      call({ type: 'restore', code: code }).then(function () { done('已恢复原来的账户'); }).catch(fail);
    });
  };

  // 解绑后在断网页用原授权码接回来（docs/01 §5.1）。
  A.restoreUse = function () {
    var v = field('restore-code');
    if (!v) { S.dialog.err = '请输入原来的授权码'; P.render(); return; }
    call({ type: 'restore', code: v }).then(function () {
      S.dialog = null; P.toast('已恢复原来的账户'); poll(); loadAccount();
    }).catch(function (err) { if (S.dialog) { S.dialog.err = msg(err); P.render(); } });
  };

  A.accessUse = function () {
    var v = field('access');
    if (!/^CC1-\S+/.test(v)) { S.dialog.err = v ? '接入码应以 CC1- 开头，请向客服确认' : '请粘贴客服发来的接入码'; P.render(); return; }
    call({ type: 'access_code', code: v }).then(function () {
      S.dialog = null; P.toast('接入码已保存，正在重试连接'); poll();
    }).catch(function (err) { if (S.dialog) { S.dialog.err = msg(err); P.render(); } });
  };

  // 接入自带出口（docs/01 §5.2）：经管理通道交给服务端读、登记、推回线路；读链接、下订阅要几秒，期间按钮换成进行中。
  A.ownExitUse = function () {
    var v = field('ownexit');
    if (!v) { S.dialog.err = '请粘贴卖家给你的链接或订阅地址'; P.render(); return; }
    S.dialog.busy = true; S.dialog.err = ''; P.render();
    var req = { type: 'own_exit', links: v };
    if (field('ownexitdirect') === true) req.direct = true;
    call(req).then(function (r) {
      var skipped = (r && r.skipped) || [];
      S.dialog = null;
      P.toast('出口接上了：' + ((r && r.lines) || 0) + ' 条线路' + (skipped.length ? '，跳过 ' + skipped.length + ' 条（' + esc(skipped[0]) + (skipped.length > 1 ? ' 等' : '') + '）' : ''));
      poll(); loadAccount();
    }).catch(function (err) { if (S.dialog) { S.dialog.busy = false; S.dialog.err = msg(err); P.render(); } });
  };
  // 升级到全套、重新开通全套：一键登录官网，打开升级商品（付款在网页上，客户端里不花钱，docs/01 §5.7、§4 第 8 步）。
  A.upgrade = function () { if (S.dialog && S.dialog.type === 'switchCode') S.dialog = null; openWeb('upgrade'); };
  // 续费（docs/01 §5.7）：先一键登录官网自助续费（余额或在线付款，付完自动顺延，不用提交申请）；线下付过钱的走续费面板。
  A.renewWeb = function () { S.tray = false; if (S.dialog && S.dialog.type === 'renew') S.dialog = null; openWeb('renew'); };
  // Pro 全套升到 Max 5x：一键登录官网，打开升档、预选这台电脑（补差价，到期日不变）。
  A.tierUp = function () { openWeb('tier_up'); };

  /* ---------- 分流：只让名单里的网站走出口 ---------- */
  S.splitRoute = { listed: false, domains: [], loaded: false };
  function loadRoute() {
    return call({ type: 'route_mode' }).then(function (r) {
      S.splitRoute = { listed: !!r.listed, domains: r.domains || [], loaded: true }; if (!busy()) P.render();
    }).catch(function () {});
  }
  function saveRoute(listed, domains, done) {
    var req = { type: 'set_route_mode', listed: listed }; if (domains) req.domains = domains;
    return call(req).then(function (r) {
      S.splitRoute = { listed: !!r.listed, domains: r.domains || [], loaded: true };
      var el = document.querySelector('[data-keep="routedomains"]'); if (el) el.value = S.splitRoute.domains.join('\n');  // 显示整理后的名单
      P.toast(done); P.render(); whenApplied(); })
      .catch(function (e) { P.toast(msg(e)); });
  }
  // 名单保存后线路会重建：等它重新连好，告诉客户刷新网页即可，并自动重新检查一遍。
  var applyTimer = null;
  function whenApplied() {
    clearInterval(applyTimer); var t0 = Date.now(), seenDown = false;
    applyTimer = setInterval(function () {
      if (S.status !== 'ok') seenDown = true;
      if ((seenDown || Date.now() - t0 > 8000) && S.status === 'ok') {
        clearInterval(applyTimer); P.toast('已生效：名单里的网站现在走你的出口，刷新网页即可');
        if (A.checkAgain) A.checkAgain();
      } else if (Date.now() - t0 > 150000) { clearInterval(applyTimer); P.toast('线路还在重新连接，稍后会自动生效', 'info'); }
    }, 1000);
  }
  A.routeListed = function () {
    var on = !S.splitRoute.listed;
    (on ? withOwner('开启后，只有名单里的网站走你的出口；其他网站和应用直连，会看到你的真实 IP。确认后继续。', '没有开启') : Promise.resolve(true)).then(function (ok) {
      if (ok) saveRoute(on, null, on ? '已开启：只有名单里的网站走出口，线路正在重建' : '已关闭：所有流量走出口，线路正在重建');
    });
  };
  A.routeSave = function () {
    var raw = field('routedomains') || '';
    // 粘贴整条网址也行：去掉 http(s):// 和路径；「1.1.1.0/24」这样的 IP 网段保留斜杠后面的位数。
    var list = raw.split(/[\s,，、;；]+/).map(function (x) {
      x = x.trim().replace(/^https?:\/\//, '');
      if (/^[0-9a-fA-F.:]+\/\d{1,3}$/.test(x)) return x;
      x = x.replace(/\/.*$/, '');
      return (x.match(/:/g) || []).length === 1 ? x.replace(/:\d+$/, '') : x;   // 只给「网址:端口」去端口，IPv6 地址原样保留
    }).filter(Boolean);
    if (!list.length) { P.toast('至少留一个网站'); return; }
    saveRoute(S.splitRoute.listed, list, '名单已保存，线路正在重建');
  };
  A.routeReset = function () { saveRoute(S.splitRoute.listed, ['claude.ai', 'claude.com', 'anthropic.com', 'claudeusercontent.com', 'challenges.cloudflare.com'], '已恢复默认名单'); };

  A.prepPaused = function () {
    var on = !S.prepPaused;
    (on ? Promise.resolve(true) : withOwner('关掉后开通前照常上网，你的真实 IP 会暴露。确认是你本人后继续。', '没有关掉')).then(function (ok) {
      if (ok) act({ type: 'block_before_ready', on: on }, on ? '已开启：开通前只能访问局域网' : '已关闭：开通前照常上网');
    });
  };

  /* ---------- 工单 ---------- */
  // 服务器状态：open 待处理（客户刚提交/追问）、replied 已回复、resolved 已处理（后台标记，等客户确认）、closed 已关闭。
  var TST = { open: 'open', pending: 'open', replied: 'wait', waiting_user: 'wait', resolved: 'done', closed: 'closed' };
  var seen = store('cc.tickets.seen') || {};
  S.ticketImages = false;   // 工单列表拿回来才知道能不能发图（图床配好了没有）
  var rated = {};

  function applyTickets(items, images) {
    S.ticketImages = !!images;
    S.tickets = items.map(function (t) {
      var last = t.messages[t.messages.length - 1];
      var id = String(t.id);
      // 以前的服务端不给每条消息的图，只给张数：写在第一条后面。
      var legacy = t.attachments && !t.messages.some(function (m) { return (m.images || []).length; });
      return {
        id: id, title: esc(t.title), st: TST[t.state] || 'open', renewal: t.renewal, rated: rated[id] || null,
        unread: !!(last && last.from_admin && last.at > (seen[id] || 0)),
        lastAt: last ? last.at : t.updated_at,
        msgs: t.messages.length ? t.messages.map(function (m, i) {
          var ids = m.images || [];
          return { who: m.from_admin ? '客服' : '你', t: when(m.at), x: m.body + (i === 0 && legacy ? '（附截图 ' + t.attachments + ' 张）' : ''),
            ids: ids, imgs: ids.map(function (g) { return IMG[id + ':' + g] || ''; }) };
        }) : [{ who: '你', t: when(t.created_at), x: '续费申请' }],
        shots: legacy ? t.attachments : t.messages.length ? (t.messages[0].images || []).length : 0
      };
    });
    loadImages();
    if (S.tickets.length && !S.tickets.some(function (t) { return t.id === S.ticketSel; })) S.ticketSel = S.tickets[0].id;
    if (!S.tickets.length && S.page === 'tickets' && !S.newTicket) S.newTicket = { step: 'type', type: null, answer: null, shots: 0 };
    S.tickets.forEach(function (t) { if (t.unread) notifyOnce('reply.' + t.id + '.' + t.lastAt, '工单有新回复', t.title); });
  }
  function loadTickets() {
    return call({ type: 'tickets' }).then(function (r) { applyTickets(r.items || [], r.images); if (!busy()) P.render(); })
      .catch(function (e) { if (S.page === 'tickets') P.toast('工单没拿到：' + msg(e), 'info'); });
  }
  function markSeen(id) {
    var t = S.tickets.filter(function (x) { return x.id === id; })[0];
    if (t) { seen[id] = t.lastAt; store('cc.tickets.seen', seen); t.unread = false; }
  }
  var goOrig = A.go;
  A.go = function (p) {
    if (p === 'check') loadCheckHist();
    if (p === 'help') loadHelp();
    if (p === 'overview' || p === 'routes') histAt = 0;
    if (p === 'tickets') {
      if (!S.tickets.length) S.newTicket = { step: 'type', type: null, answer: null, shots: 0 };
      else markSeen(S.ticketSel);
      loadTickets();
    }
    return goOrig.apply(this, arguments);
  };
  var ticketOrig = A.ticket;
  A.ticket = function (id) { var r = ticketOrig.apply(this, arguments); markSeen(id); loadImages(); return r; };

  /* ---------- 工单里的图（docs/01 §5.9） ---------- */
  // 只取正在看的那张工单的图：经守护程序分块取（本机通信一行放不下整张），拼成 data: 地址（窗口只允许本地和 data: 的图），取过的记住。
  var IMG = {}, imgBusy = false;
  function loadImages() {
    if (imgBusy) return;
    var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0];
    if (!t) return;
    var want = null;
    t.msgs.some(function (m) { return (m.ids || []).some(function (g) { if (!IMG[t.id + ':' + g]) { want = g; return true; } return false; }); });
    if (want == null) return;
    imgBusy = true;
    var key = t.id + ':' + want, bin = '', mime = 'image/png';
    function next(off) {
      return call({ type: 'ticket_image', ticket: +t.id, id: want, offset: off }).then(function (r) {
        mime = r.mime || mime;
        var b = atob(r.data || '');
        bin += b;
        return off + b.length < (r.total || 0) && b.length ? next(off + b.length) : null;
      });
    }
    next(0).then(function () {
      IMG[key] = 'data:' + mime + ';base64,' + btoa(bin);
    }, function () {
      // 取不到（图床暂时不通）：这次先不再试这张，重开窗口再试。
      IMG[key] = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="160" height="100"><rect width="160" height="100" fill="#eceef3"/><text x="80" y="56" font-size="13" text-anchor="middle" fill="#8a93a6">图片暂时看不了</text></svg>');
    }).then(function () {
      imgBusy = false;
      S.tickets.forEach(function (x) { x.msgs.forEach(function (m) { if (m.ids) m.imgs = m.ids.map(function (g) { return IMG[x.id + ':' + g] || ''; }); }); });
      if (!busy()) P.render();
      loadImages();
    });
  }
  var ntCancelOrig = A.ntCancel;
  if (ntCancelOrig) A.ntCancel = function () { if (!S.tickets.length) { S.newTicket = null; A.go('overview'); return; } return ntCancelOrig.apply(this, arguments); };

  A.ntSubmit = function () {
    var N = S.newTicket, d = field('nt-desc');
    if (!d) { S.actErr = { nt: '请简单描述一下问题' }; P.render(); return; }
    var T = P.TTYPES[N.type];
    var body = (T.q && (!T.shot || S.ticketImages === false) && N.answer ? '【' + T.q + ' ' + N.answer + '】' : '') + d;
    var files = N.files || [];
    call({ type: 'new_ticket', kind: N.type + 1, title: T.n, body: body, attach_diag: N.diag !== false, attachments: files }).then(function (r) {
      // 先放一条进列表（服务端的列表拿回来之前页面也要有东西画）。
      S.tickets.unshift({ id: String(r.id), title: esc(T.n), st: 'open', msgs: [{ who: '你', t: '刚刚', x: body }] });
      S.newTicket = null; S.actErr = {}; S.ticketSel = String(r.id);
      P.toast('工单 #' + r.id + ' 已提交');
      loadTickets().then(function () {
        // 工单建好后截图一张张发：没传上的告诉用户。
        var t = S.tickets.filter(function (x) { return x.id === String(r.id); })[0];
        var lost = t ? files.length - t.shots : 0;
        if (lost > 0) P.toast('有 ' + lost + ' 张截图没传上，可以在回复里说明，或重新提交附上', 'info');
      });
    }).catch(function (e) { S.actErr = { nt: msg(e) }; P.render(); });
  };

  /* ---------- 工单截图 ---------- */
  // 本机通信一行最多 64 KB：每块 30000 字节（base64 后 4 万字符）。格式、大小服务端还会再查。
  var SHOT_MAX = 5 * 1024 * 1024, CHUNK = 30000, SHOT_TYPES = ['image/png', 'image/jpeg', 'image/webp'];
  var shotSeq = 0;
  function b64(bytes) { var s = ''; for (var i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]); return btoa(s); }
  function uploadShot(bytes) {
    var id = 'shot-' + Date.now() + '-' + (++shotSeq), off = 0;
    function next() {
      if (off >= bytes.length) return Promise.resolve(id);
      var chunk = bytes.subarray(off, off + CHUNK);
      return call({ type: 'upload_chunk', upload: id, offset: off, data: b64(chunk) }).then(function () { off += chunk.length; return next(); });
    }
    return next();
  }
  // 选图或粘贴（file 给了就用它）→ 核对格式和大小 → 分块交给守护程序 → 回（编号, 本机预览地址）。
  function pickShot(file) {
    return new Promise(function (resolve, reject) {
      function take(f) {
        if (!f) { reject(null); return; }
        if (SHOT_TYPES.indexOf(f.type) < 0) { reject(new Error('只能发 PNG、JPEG、WebP 图片')); return; }
        if (f.size > SHOT_MAX) { reject(new Error('图片不能超过 5 MB')); return; }
        S.busy.shot = { text: '正在添加图片' }; P.render();
        var bytes;
        f.arrayBuffer().then(function (buf) { bytes = new Uint8Array(buf); return uploadShot(bytes); }).then(function (id) {
          S.busy.shot = null;
          resolve({ id: id, url: 'data:' + f.type + ';base64,' + b64(bytes) });
        }, function (e) { S.busy.shot = null; P.render(); reject(e); });
      }
      if (file) { take(file); return; }
      var input = document.createElement('input');
      input.type = 'file'; input.accept = SHOT_TYPES.join(',');
      input.onchange = function () { take(input.files && input.files[0]); };
      input.click();
    });
  }
  function shotFail(e) { if (e) P.toast('图片没加上：' + msg(e), 'info'); }
  A.ntShot = function (file) {
    var N = S.newTicket;
    if (!N || (N.files || []).length >= 3 || S.busy.shot) return;
    pickShot(file).then(function (r) {
      N.files = (N.files || []).concat([r.id]); N.previews = (N.previews || []).concat([r.url]);
      N.shots = N.files.length; P.render();
    }, shotFail);
  };
  A.ntShotDel = function (i) {
    var N = S.newTicket;
    if (!N) return;
    var keep = function (_, k) { return k !== +i; };
    N.files = (N.files || []).filter(keep); N.previews = (N.previews || []).filter(keep);
    N.shots = N.files.length; P.render();
  };
  // 回复里的图：界面上的预览在 S.replyShots[工单]，交给守护程序的编号在 replyFiles[工单]。
  var replyFiles = {};
  A.replyShot = function (file) {
    var tid = S.ticketSel, files = replyFiles[tid] = replyFiles[tid] || [];
    if (files.length >= 3 || S.busy.shot) return;
    pickShot(file).then(function (r) {
      files.push(r.id); S.replyShots[tid] = (S.replyShots[tid] || []).concat([r.url]); P.render();
    }, shotFail);
  };
  A.replyShotDel = function (i) {
    var tid = S.ticketSel, keep = function (_, k) { return k !== +i; };
    replyFiles[tid] = (replyFiles[tid] || []).filter(keep);
    S.replyShots[tid] = (S.replyShots[tid] || []).filter(keep); P.render();
  };
  A.reply = function () {
    var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0];
    if (!t) return;
    var key = 'reply-' + t.id, v = field(key), files = replyFiles[t.id] || [];
    if (!v && !files.length) return;
    call({ type: 'ticket_reply', id: +t.id, body: v, attachments: files }).then(function () {
      var el = document.querySelector('[data-keep="' + key + '"]'); if (el) el.value = '';
      replyFiles[t.id] = []; S.replyShots[t.id] = [];
      P.toast('已发送');
      loadTickets().then(function () {
        // 回复发了以后图一张张传：没传上的告诉用户。
        var x = S.tickets.filter(function (y) { return y.id === t.id; })[0], last = x && x.msgs[x.msgs.length - 1];
        var lost = files.length - (last && last.ids ? last.ids.length : 0);
        if (files.length && lost > 0) P.toast('有 ' + lost + ' 张图没传上，可以再发一次', 'info');
      });
      loadAccount();
    }).catch(function (e) { P.toast('没发出去：' + msg(e), 'info'); });
  };
  A.rate = function (r) {
    var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0];
    if (!t) return;
    call({ type: 'ticket_rate', id: +t.id, solved: r === '已解决' }).then(function () {
      rated[t.id] = r; P.toast('谢谢反馈'); loadTickets();
    }).catch(function (e) { P.toast(msg(e), 'info'); });
  };
  A.renewProgress = function () {
    var t = S.tickets.filter(function (x) { return x.renewal; })[0];
    if (!t) { A.go('tickets'); return; }
    S.newTicket = null; S.ticketSel = t.id; markSeen(t.id); A.go('tickets');
  };
  // Windows 原型的「查看进度」（续费处理中再点「续费」、快到期的号点「续费」也到这里）：看真的那张续费工单，
  // 不用原型的（它会塞一张演示工单和演示时间）。
  if (A.renewView) A.renewView = A.renewProgress;

  /* ---------- 续费申请 ---------- */
  // 选项来自原型的 renewOpts()（在 App 里是上面的 CC_RENEW_OPTS：全套或老套餐的软件，和流量）。
  // macOS 记在 S.renew.sel（名字），Windows 记在 S.dialog.pick（键）；两边的选项统一成 { k, n, what }。
  function renewOpts() {
    return P.renewOpts().map(function (o) {
      return Array.isArray(o) ? { k: o[0], n: o[1], what: o[2] } : { k: o.n, n: o.n, what: o.what };
    });
  }
  function renewPicked() {
    var sel = S.renew.sel || [], pick = (S.dialog && S.dialog.pick) || {};
    return renewOpts().filter(function (o) { return sel.indexOf(o.n) >= 0 || pick[o.k]; });
  }
  A.renewSubmit = function () {
    var picked = renewPicked();
    if (!picked.length) { S.dialog.err = '请至少选择一项'; P.render(); return; }
    // 按类各提交一条申请（全套或软件、流量）；同一类里选的写在备注前面，客服按它处理。
    var groups = [], by = {};
    picked.forEach(function (o) {
      if (!by[o.what]) { by[o.what] = { what: o.what, names: [] }; groups.push(by[o.what]); }
      by[o.what].names.push(o.n);
    });
    var note = field('renew-note');
    var names = picked.map(function (o) { return o.n; });
    var pick = {}; picked.forEach(function (o) { pick[o.k] = true; });
    S.renew.busy = true; P.render();
    var chain = Promise.resolve();
    groups.forEach(function (g) {
      var req = { type: 'renewal', what: g.what, order_no: g.names.join('、') + (note ? '；' + note : '') };
      chain = chain.then(function () { return call(req); });
    });
    chain.then(function () {
      S.renew = { phase: 'processing', at: hhmm(new Date()), items: names, pick: pick, sel: null, busy: false };
      S.dialog = null; P.toast('续费申请已提交'); loadAccount(); loadTickets();
    }).catch(function (e) { S.renew.busy = false; if (S.dialog) S.dialog.err = msg(e); P.render(); });
  };

  /* ---------- 时区 ---------- */
  // 常见时区的中文名和城市（守护程序只给 IANA 名；后台登记了显示名的用后台的）。没在表里的照 IANA 名显示。
  var ZONES = {
    'Asia/Shanghai': ['北京时间', ''], 'Asia/Chongqing': ['北京时间', ''], 'Asia/Harbin': ['北京时间', ''], 'Asia/Urumqi': ['北京时间', ''], 'PRC': ['北京时间', ''],
    'Asia/Hong_Kong': ['香港时间', ''], 'Asia/Macau': ['澳门时间', ''], 'Asia/Taipei': ['台北时间', ''],
    'Asia/Tokyo': ['日本标准时间', '东京'], 'Asia/Seoul': ['韩国标准时间', '首尔'], 'Asia/Singapore': ['新加坡时间', ''], 'Asia/Kuala_Lumpur': ['马来西亚时间', '吉隆坡'],
    'Asia/Bangkok': ['泰国时间', '曼谷'], 'Asia/Ho_Chi_Minh': ['越南时间', '胡志明市'], 'Asia/Saigon': ['越南时间', '胡志明市'], 'Asia/Jakarta': ['印尼西部时间', '雅加达'], 'Asia/Manila': ['菲律宾时间', '马尼拉'],
    'Asia/Kolkata': ['印度标准时间', ''], 'Asia/Calcutta': ['印度标准时间', ''], 'Asia/Dubai': ['海湾标准时间', '迪拜'], 'Asia/Riyadh': ['沙特时间', '利雅得'], 'Asia/Jerusalem': ['以色列时间', ''], 'Asia/Tel_Aviv': ['以色列时间', ''],
    'Europe/Istanbul': ['土耳其时间', '伊斯坦布尔'], 'Asia/Istanbul': ['土耳其时间', '伊斯坦布尔'], 'Europe/Moscow': ['莫斯科时间', ''],
    'Europe/London': ['英国时间', '伦敦'], 'Europe/Dublin': ['爱尔兰时间', '都柏林'], 'Europe/Lisbon': ['葡萄牙时间', '里斯本'],
    'Europe/Paris': ['中欧时间', '巴黎'], 'Europe/Berlin': ['中欧时间', '柏林'], 'Europe/Madrid': ['中欧时间', '马德里'], 'Europe/Rome': ['中欧时间', '罗马'], 'Europe/Amsterdam': ['中欧时间', '阿姆斯特丹'],
    'Europe/Brussels': ['中欧时间', '布鲁塞尔'], 'Europe/Vienna': ['中欧时间', '维也纳'], 'Europe/Zurich': ['中欧时间', '苏黎世'], 'Europe/Stockholm': ['中欧时间', '斯德哥尔摩'], 'Europe/Oslo': ['中欧时间', '奥斯陆'],
    'Europe/Copenhagen': ['中欧时间', '哥本哈根'], 'Europe/Prague': ['中欧时间', '布拉格'], 'Europe/Warsaw': ['中欧时间', '华沙'], 'Europe/Budapest': ['中欧时间', '布达佩斯'],
    'Europe/Athens': ['东欧时间', '雅典'], 'Europe/Helsinki': ['东欧时间', '赫尔辛基'], 'Europe/Kiev': ['东欧时间', '基辅'], 'Europe/Kyiv': ['东欧时间', '基辅'], 'Europe/Bucharest': ['东欧时间', '布加勒斯特'],
    'America/New_York': ['美国东部时间', '纽约'], 'America/Toronto': ['北美东部时间', '多伦多'], 'America/Detroit': ['美国东部时间', '底特律'], 'US/Eastern': ['美国东部时间', ''],
    'America/Chicago': ['美国中部时间', '芝加哥'], 'US/Central': ['美国中部时间', ''], 'America/Denver': ['美国山地时间', '丹佛'], 'America/Phoenix': ['美国山地时间', '凤凰城'], 'US/Mountain': ['美国山地时间', ''],
    'America/Los_Angeles': ['太平洋时间', '洛杉矶'], 'America/Vancouver': ['太平洋时间', '温哥华'], 'US/Pacific': ['太平洋时间', ''], 'America/Anchorage': ['阿拉斯加时间', ''], 'Pacific/Honolulu': ['夏威夷时间', ''],
    'America/Mexico_City': ['墨西哥中部时间', '墨西哥城'], 'America/Sao_Paulo': ['巴西利亚时间', '圣保罗'], 'America/Argentina/Buenos_Aires': ['阿根廷时间', '布宜诺斯艾利斯'], 'America/Santiago': ['智利时间', '圣地亚哥'],
    'Australia/Sydney': ['澳大利亚东部时间', '悉尼'], 'Australia/Melbourne': ['澳大利亚东部时间', '墨尔本'], 'Australia/Brisbane': ['澳大利亚东部时间', '布里斯班'], 'Australia/Perth': ['澳大利亚西部时间', '珀斯'],
    'Pacific/Auckland': ['新西兰时间', '奥克兰'], 'Africa/Johannesburg': ['南非时间', '约翰内斯堡'], 'Africa/Cairo': ['埃及时间', '开罗'], 'Africa/Lagos': ['西非时间', '拉各斯'],
    'UTC': ['协调世界时', ''], 'Etc/UTC': ['协调世界时', ''], 'GMT': ['格林尼治时间', ''], 'Etc/GMT': ['格林尼治时间', '']
  };
  function zoneName(iana) { var z = iana && ZONES[iana]; return z ? z[0] : ''; }
  function zoneCity(iana) { var z = iana && ZONES[iana]; return z ? z[1] : ''; }
  var zone = null, zonePrompted = {}, zoneMismatchSince = {};
  function applyZone(z) {
    zone = z;
    // 出口所在地：后台登记的 > 当前线路的地区 > 按出口时区猜的城市；都没有就不提。
    P.TZN.city = z.location || (ROUTES[S.route] && ROUTES[S.route].desc) || zoneCity(z.exit) || '';
    // 出口时区：后台登记的显示名 > 常见时区的中文名 > IANA 名；配置包里还没有就是空（界面说「还不知道」）。
    P.TZN.exit = z.exit ? (z.exit_label || zoneName(z.exit) || z.exit) : '';
    var paren = [zoneCity(z.system), z.system_offset].filter(Boolean).join('，');
    P.TZN.sys = z.system ? (zoneName(z.system) || z.system) + (paren ? '（' + paren + '）' : '') : '—';
    S.tzChanged = !!z.matches;
    if (results.C1) results.C1 = { id: 'C1', verdict: !z.exit ? 'skipped' : z.matches ? 'pass' : 'warn', detail: !z.exit ? (z.lookup_loading ? '正在查询出口所在地的时区' : z.lookup_error || '尚未取得出口时区，暂时无法比对') : z.matches ? '电脑时区已和出口所在地一致（' + z.exit + '）' : '电脑时区和出口所在地（' + z.exit + '）不一致' };
    if (S.wizard && S.wizard.tz !== 'kept') S.wizard.tz = z.matches ? 'changed' : S.wizard.tz;
    // 已经一致了（后台刚自动改好）：还开着的「时区不一致」窗口自己关掉。
    if (z.matches && S.dialog && S.dialog.type === 'tz') S.dialog = null;
    // 不一致、这个出口没点过「暂不」：后台会在几秒内自动改成出口所在地，过 20 秒还不一致（自动改没成）才弹一次。
    if (z.exit && !z.matches) { if (!zoneMismatchSince[z.exit]) zoneMismatchSince[z.exit] = Date.now(); } else if (z.exit) delete zoneMismatchSince[z.exit];
    if (z.exit && !z.matches && !z.dismissed && z.can_change && !zonePrompted[z.exit] && !S.dialog && S.screen === 'main' && Date.now() - zoneMismatchSince[z.exit] >= 20000) {
      zonePrompted[z.exit] = true;
      S.dialog = { type: 'tz' };
    }
  }
  window.CC_RELOAD_ZONE = function () { return loadZone(); };
  function loadZone() {
    return call({ type: 'timezone' }).then(function (z) { applyZone(z); if (!busy()) P.render(); }).catch(function () {});
  }
  A.tzDo = function () {
    call({ type: 'set_timezone' }).then(function () {
      S.dialog = null; if (S.wizard) S.wizard.tz = 'changed';
      P.toast('电脑时区已改为' + P.TZN.exit); loadZone();
    }).catch(function (e) { S.dialog = null; P.toast(msg(e), 'info'); });
  };
  var tzLaterOrig = A.tzLater;
  A.tzLater = function () {
    call({ type: 'dismiss_timezone' }).catch(function () {});
    return tzLaterOrig.apply(this, arguments);
  };
  A.tzRestore = A.wizTzRestore = function () {
    call({ type: 'restore_timezone' }).then(function () {
      if (S.wizard) S.wizard.tz = 'kept';
      P.toast('已恢复原来的时区'); loadZone();
    }).catch(function (e) { P.toast(msg(e), 'info'); });
  };

  /* ---------- 直连规则 ---------- */
  // 公网地址（不是私网、回环、链路本地、运营商 NAT 段）：列表里标红「直接看到真实 IP」。
  function isPublic(cidr) {
    var m = /^(\d+)\.(\d+)\./.exec(cidr); if (!m) return false;
    var a = +m[1], b = +m[2];
    return !(a === 10 || a === 127 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254) || (a === 100 && b >= 64 && b <= 127));
  }
  function loadRules() {
    return call({ type: 'direct_rules' }).then(function (r) {
      S.lan = (r.items || []).map(function (x) { return { ip: x.cidr.replace(/\/32$/, ''), cidr: x.cidr, note: x.note, pub: isPublic(x.cidr) }; });
      if (!busy()) P.render();
    }).catch(function () {});
  }
  // 高级：先二次确认，确认后在线提交（守护程序再校验，服务端签进新配置包）。
  A.advAdd = function () {
    var ip = field('advip'); S.advOpen = true;
    if (!ip) { S.advMsg = '请输入公网地址'; P.render(); return; }
    S.advMsg = null;
    // 归属地由服务端的库查（经管理通道）；境外的红字警告：境外网站看到的会是你的真实 IP 和这个地址的对比。
    var D = { type: 'public', ip: ip, note: field('advnote'), geo: '正在查询' };
    S.dialog = D; P.render();
    call({ type: 'geo', ip: ip }).then(function (r) {
      if (S.dialog !== D) return;
      D.geo = !r.label ? '暂时查不到'
        : r.overseas ? '<span class="c-danger">' + esc(r.label) + '（境外地址）</span>' : esc(r.label);
      P.render();
    }).catch(function () { if (S.dialog === D) { D.geo = '暂时查不到'; P.render(); } });
  };
  A.advConfirm = function () {
    var D = S.dialog;
    call({ type: 'add_direct_rule', rule: D.ip, note: D.note, confirm_public: true }).then(function () {
      S.dialog = null;
      ['advip', 'advnote'].forEach(function (k) { var el = document.querySelector('[data-keep="' + k + '"]'); if (el) el.value = ''; });
      P.toast('已添加公网地址 ' + D.ip); loadRules();
    }).catch(function (e) { S.dialog = null; S.advOpen = true; S.advMsg = msg(e); P.render(); });
  };
  A.lanDel = function (i) {
    var l = S.lan[+i]; if (!l) return;
    call({ type: 'remove_direct_rule', cidr: l.cidr }).then(function () {
      S.lanEdit = -1; S.lanMsg = null; P.toast('已删除 ' + l.ip); loadRules();
    }).catch(function (e) { P.toast(msg(e), 'info'); });
  };
  A.lanNoteSave = function (i) {
    var l = S.lan[+i]; if (!l) return;
    call({ type: 'set_direct_note', cidr: l.cidr, note: field('lanedit') }).then(function () {
      S.lanEdit = -1; loadRules();
    }).catch(function (e) { P.toast(msg(e), 'info'); });
  };

  /* ---------- 时间、后台运行 ---------- */
  A.syncTime = function () {
    call({ type: 'sync_time' }).then(function () { P.toast('已按标准时间校准电脑时间'); }).catch(function (e) { P.toast(msg(e), 'info'); });
  };
  A.bgOpen = function () {
    T.core.invoke('open_login_items').then(function () {
      P.toast('已打开系统设置：在「登录项与扩展」里允许本软件在后台运行');
    }).catch(function (e) { P.toast('没打开：' + msg(e), 'info'); });
  };

  /* ---------- 联系方式 ---------- */
  A.contactSave = function () {
    var v = field('contact');
    if (!v) { P.toast('联系方式不能为空，微信 / QQ / 邮箱都可以', 'info'); return; }
    v = field('contact-kind') + ' ' + v;
    call({ type: 'set_contact', contact: v }).then(function () {
      S.contact = v; S.editingContact = false; P.toast('联系方式已保存'); loadAccount();
    }).catch(function (e) { P.toast('没保存上：' + msg(e), 'info'); });
  };

  /* ---------- 帮助（docs/01 §5.13） ---------- */
  // 常见问题和使用指南：守护程序存在本机（断网也有），进帮助页时取。没有官网地址的指南不显示。
  function loadHelp() {
    return call({ type: 'help' }).then(function (r) {
      P.FAQ.length = 0; P.GUIDES.length = 0; guideUrls = [];
      (r.faq || []).forEach(function (f) { P.FAQ.push([esc(f.q), esc(f.a)]); });
      (r.guides || []).filter(function (g) { return g.url; }).forEach(function (g) { P.GUIDES.push([esc(g.title), esc(g.text)]); guideUrls.push(g.url); });
      if (!busy()) P.render();
    }).catch(function () {});
  }
  // 指南在官网上：界面只给序号，App 壳自己向守护程序要地址、校验后交给默认浏览器（open_guide）。
  A.guideOpen = function (i) {
    if (!guideUrls[+i]) return;
    T.core.invoke('open_guide', { index: +i }).then(function () {
      P.toast('已在浏览器中打开', 'external');
    }).catch(function (e) { P.toast('没打开：' + msg(e), 'info'); });
  };

  /* ---------- 网页版个人中心 ---------- */
  // 去向（App 壳加在一键登录链接后面，官网登录后打开这台电脑的购买弹窗）：refill 补号（docs/01 §5.12）；
  // upgrade 单独软件升级到全套（§5.2），全套到期以后的「重新开通全套」也是它（§4 第 8 步）；renew 自助续费；tier_up 升到 Max 5x。
  var REFILL_OK = '已在网页上打开「补号」，买好后新号会出现在这里';
  function openWeb(to) {
    if (S.busy.web) return;
    S.busy.web = true;
    T.core.invoke('open_web_center', to ? { to: to } : {}).then(function () {
      S.busy.web = false;
      P.toast(to === 'refill' ? REFILL_OK : to === 'refund_calc' ? '已在网页上打开「封号退款计算器」，按这台电脑的记录填好了' : to === 'buy_soft' ? '已在网页上打开「单独软件」，付款后这台电脑直接转成永久使用'
        : to === 'renew' ? '已在网页上打开「续费」，付款后自动顺延，不用再提交申请' : to === 'tier_up' ? '已在网页上打开「升到 Max 5x」，补差价后升档，到期日不变' : to === 'upgrade' ? (suiteKind() === 'soft' ? '已在网页上打开「升级到全套」，付款后自动换成独享线路' : '已在网页上打开「重新开通全套」，付款后自动恢复独享线路')
        : '已交给默认浏览器，没弹出来请检查默认浏览器设置', 'external');
    }).catch(function (e) { S.busy.web = false; P.toast('没打开：' + msg(e), 'info'); });
  }
  // 消息里订单、商品、官网页面的跳转按钮也走这里（原型把跳转目标接在后面）：都打开个人中心。
  A.openWeb = function () { openWeb(null); };
  A.aiRefill = function () { openWeb('refill'); };
  // 「看怎么算的」（docs/01 §5.12「保障状态」）：官网的封号退款计算器，按这台电脑的真实记录填好。
  A.aiCalc = function () { openWeb('refund_calc'); };
  // 免费试用的「购买」：官网打开单独软件的购买弹窗、预选这台电脑，付款后直接转正（docs/01 §3.1「免费试用」）。
  A.buySoft = function () { S.tray = false; if (S.dialog && S.dialog.type === 'switchCode') S.dialog = null; openWeb('buy_soft'); };
  // 换授权码（docs/01 §3.1「试用换成买的」）：填买来的码，还是这台电脑；单独软件的码转正，全套的码换成全套，试用码作废。
  // 单独软件电脑的「填全套码升级」也是它：服务端只收全套码（填别的回 400 说明，照常显示），原来的码不作废。
  A.switchCodeGo = function () {
    var c = field('switch-code');
    if (!c) { S.dialog.err = '先填授权码'; P.render(); return; }
    S.dialog.busy = true; S.dialog.err = ''; S.busy.switchCode = { text: '正在换授权码', done: false }; P.render();
    call({ type: 'switch_code', code: c }).then(function (r) {
      delete S.busy.switchCode; S.dialog = null;
      P.toast(r && r.suite ? '已换成全套，线路开通后自动连上，Claude 账号会发到「我的账号」' : '已换成单独软件，永久使用');
      loadAccount();
    }).catch(function (e) {
      delete S.busy.switchCode;
      if (S.dialog) { S.dialog.busy = false; S.dialog.err = msg(e); }
      P.render();
    });
  };

  /* ---------- 帮我退出 ---------- */
  // 由界面以当前用户的身份请对方正常退出（不强制结束，docs/01 §5.3）；对方退出后下一次冲突检测就不再提示。
  // 有的软件关窗口只是缩到托盘、菜单栏（Windows 上的 FlClash：界面缩到托盘，内核还由它的系统服务跑着）：
  // 请求完过几秒当场再查一次，还在就弹窗教用户自己从图标退出，不让人以为点了没反应。
  var QUIT_RECHECK_MS = 5000;
  function quitHint(name) {
    return /Mac/.test(navigator.platform || navigator.userAgent)
      ? '已经请它退出，但它还在运行。请在屏幕上方菜单栏里点 ' + name + ' 的图标，选「退出」；菜单栏里没有，就在程序坞里右键它，选「退出」。'
      : '已经请它退出，但它关窗口时只是缩到了屏幕右下角，还在运行。请在任务栏右下角找到 ' + name + ' 的图标（看不到就先点「^」展开），点右键，选「退出」。';
  }
  var PROXY_UNDO_MS = 120000, proxyOffAt = 0, proxyUndoHidden = false;
  A.proxyOff = function () {
    call({ type: 'proxy_off' }).then(function () {
      proxyOffAt = Date.now(); proxyUndoHidden = false; P.toast('已关掉系统代理'); poll();
    }).catch(function (e) { P.toast('没能关掉：' + msg(e), 'info'); });
  };
  A.proxyUndo = function () {
    call({ type: 'proxy_undo' }).then(function () { proxyOffAt = 0; S.proxyUndo = false; P.toast('系统代理已照原样改回'); poll(); })
      .catch(function (e) { P.toast('没能改回去：' + msg(e), 'info'); });
  };
  var dismissProto = A.dismiss;
  A.dismiss = function (what) { if (what === 'proxyUndo') proxyUndoHidden = true; dismissProto(what); };
  // Windows（退出方式是映像名的）：请不走就写明「XX 没退掉」，给「强制退出」（docs/03 E4，2026-11-12 定）：
  // FlClash、雷神加速器的托盘程序不理正常退出，以前点了没有任何提示。强制要用户确认、要管理员权限，连同它的内核进程一起结束。
  var forceTarget = null;
  function stuckPopup(c, name) {
    forceTarget = c;
    P.showPopup({ id: 'quit.' + c.id, who: name, lv: 'action', title: name + ' 没退掉',
      body: '已经请它正常退出，但它没有退（关窗口只是缩到了右下角，或者它要管理员权限才能关）。可以点「强制退出」，也可以在任务栏右下角它的图标上点右键，选「退出」。',
      acts: [['secondary', '我自己退', 'popAck'], ['danger', '强制退出', 'quitForce']] });
  }
  A.quitApp = function () {
    var c = conflictNow;
    if (!c) return;
    var name = c.name || '其他网络软件';
    if (!c.quit) { P.toast('这一项不能帮你退出，请手动退出' + P.CONFL.name, 'info'); return; }
    T.core.invoke('quit_conflict', { id: c.id }).then(function () {
      S.busy.quit = { text: '已请求 ' + P.CONFL.name + ' 退出', done: true }; P.render();
      setTimeout(function () {
        call({ type: 'check_conflicts' }).then(function (r) {
          S.busy.quit = null;
          if ((r.items || []).some(function (x) { return x.id === c.id; })) {
            if (c.quit.how === 'windows_image') stuckPopup(c, name);
            else P.showPopup({ id: 'quit.' + c.id, lv: 'action', title: name + ' 还在运行', body: quitHint(name) });
          }
          poll();
        }).catch(function () { S.busy.quit = null; poll(); });
      }, QUIT_RECHECK_MS);
    }).catch(function (e) { P.toast('没能请它退出：' + msg(e), 'info'); });
  };
  A.quitForce = function () {
    var c = forceTarget;
    if (!c) return;
    var name = c.name || '其他网络软件', n = esc(name);
    A.popAck();
    P.confirmFirst(['强制退出 ' + n + '？', '强制退出可能丢掉 ' + n + ' 里没保存的内容，它的后台程序会一起结束。接下来 Windows 会请你确认管理员权限。', '强制退出'], function () {
      S.busy.quit = { text: '正在强制退出 ' + n }; P.render();
      T.core.invoke('force_quit_conflict', { id: c.id }).then(function (r) {
        S.busy.quit = null; forceTarget = null;
        var left = (r && r.left) || [];
        if (left.length) {
          P.showPopup({ id: 'quit.' + c.id, who: name, lv: 'action', title: name + ' 还是没退掉',
            body: '强制退出也没能结束它（' + left.join('、') + '）。请在任务栏右下角它的图标上点右键，选「退出」；还不行就在任务管理器里结束它，或者重启电脑。' });
        } else P.toast(n + ' 已强制退出');
        // 当场再查一遍冲突软件：横幅马上收起，不等下一轮（守护程序 60 秒查一次）。
        call({ type: 'check_conflicts' }).then(poll, poll);
      }).catch(function (e) {
        S.busy.quit = null; P.render();
        // 「它已经退出了」：在确认的这会儿自己退了，也算好结果。
        P.toast(/已经退出/.test(msg(e)) ? n + ' 已经退出了' : '没有强制退出：' + msg(e), 'info');
        poll();
      });
    });
  };

  /* ---------- 网络恢复工具 ---------- */
  // 以管理员身份跑网络急救：解除防护规则、恢复系统网络设置。系统弹出输入密码的框，用户取消就回到说明页。
  A.rescueGo = function () {
    var R = S.rescue;
    if (!R) return;
    R.confirm = false; R.step = 'run';
    S.busy.rescue = { text: '正在请求管理员权限' }; P.render();
    T.core.invoke('run_rescue').then(function () {
      S.busy.rescue = null;
      if (S.rescue === R) { R.admin = true; R.done = 3; }
      P.render();
    }).catch(function (e) {
      S.busy.rescue = null;
      if (S.rescue === R) R.step = 'intro';
      P.render(); P.toast('网络没恢复：' + msg(e), 'info');
    });
  };
  A.demo = function () {};

  /* ---------- 消息、公告列表、我的账号 ---------- */
  var MSGS = P.MSGS, ANNS = P.ANNS, AIACC = P.AIACC;
  MSGS.length = 0; ANNS.length = 0; AIACC.length = 0;
  S.ai = false; S.aiGuardOpen = null; S.aiLogin = null; S.aiShown = null; S.aiReported = {};
  S.popup = null; S.popQueue = [];
  var LINK_LABEL = { ticket: '查看工单', renewal: '去续费', order: '查看订单', product: '去看看', page: '打开页面', refill: '补号' };
  var msgList = [], annList = [], inboxSeen = null, aiSeen = null, msgUnread = 0;
  var popped = store('cc.popped') || [], popAcked = store('cc.popack') || [], shownNow = [];
  var jumpTarget = null;
  function plainText(t) { return String(t || '').replace(/\*\*/g, ''); }
  // 跳转按钮：[种类, 按钮上的字, 目标]。补号（refill）、重新开通全套 / 升级到全套（upgrade）和「我的账号」的按钮一样去购买弹窗；
  // 以前发的「再买一个」（{kind: product, target: ai}）按现在能不能补号换成补号或重新开通全套（旧服务端没说的当补号，官网也是），
  // 按钮上的字不变。
  function linkOf(l) {
    if (!l) return null;
    if (l.kind === 'product' && l.target === 'ai') return [account && account.suite && !account.suite.can_refill ? 'upgrade' : 'refill', '再买一个', ''];
    if (l.kind === 'upgrade') return ['upgrade', suiteKind() === 'soft' ? '升级到全套' : '重新开通全套', ''];
    return [l.kind, LINK_LABEL[l.kind] || '查看', l.target || ''];
  }

  function applyInbox(a) {
    msgUnread = a.messages_unread || 0;
    var mk = (a.messages_latest || 0) + '/' + msgUnread;
    if (mk !== inboxSeen) { inboxSeen = mk; loadMessages(); }
    if (a.ai_version != null && a.ai_version !== aiSeen) { aiSeen = a.ai_version; loadAi(); }
    // 号都没了、也没有在准备的才离开「我的账号」。
    if (!a.ai_accounts && S.ai) { AIACC.length = 0; S.ai = false; }
    if (!S.ai && !S.aiPrep && S.page === 'accounts') S.page = 'overview';
    annList = a.announcements || [];
    ANNS.length = 0;
    annList.forEach(function (n) {
      ANNS.push({ id: 'N-' + n.id, kind: n.maintenance ? 'maint' : n.important ? 'important' : 'normal', title: n.title, body: n.body, t: day(n.starts_at) });
    });
    queuePopups();
  }

  function loadMessages() {
    return call({ type: 'messages' }).then(function (r) {
      msgList = r.items || [];
      msgUnread = r.unread || 0;
      MSGS.length = 0;
      msgList.forEach(function (m) {
        MSGS.push({ id: String(m.id), lv: m.level === 'action' ? 'action' : 'normal', title: m.title, body: m.body, t: when(m.created_at), link: linkOf(m.link), read: !!m.read_at, withdrawn: !!m.withdrawn });
      });
      queuePopups();
      if (!busy()) P.render();
    }).catch(function () {});
  }
  function markRead(ids, all) {
    return call(all ? { type: 'mark_read', all: true } : { type: 'mark_read', ids: ids }).then(loadMessages).catch(function () {});
  }

  // 打开主界面时弹：需要处理、还没读的消息（弹过一次就不再弹），勾了弹窗的公告（只弹一次的弹一次；每次都弹的每次打开 App 弹到点「知道了」）。
  function queuePopups() {
    if (S.screen !== 'main') return;
    msgList.forEach(function (m) {
      var k = 'm' + m.id;
      if (m.level !== 'action' || m.read_at || m.withdrawn || popped.indexOf(k) >= 0) return;
      popped.push(k);
      P.showPopup({ id: k, msgId: m.id, lv: 'action', title: m.title, body: m.body, link: linkOf(m.link) });
      notifyOnce('msg.' + m.id, m.title, plainText(m.body).slice(0, 40));
    });
    annList.forEach(function (n) {
      var k = 'a' + n.id;
      if (!n.popup || shownNow.indexOf(k) >= 0) return;
      if (n.popup_mode === 'until_ack' ? popAcked.indexOf(k) >= 0 : popped.indexOf(k) >= 0) return;
      shownNow.push(k); popped.push(k);
      P.showPopup({ id: k, kind: n.maintenance ? 'maint' : n.important ? 'important' : 'normal', title: n.title, body: n.body, link: n.link ? ['page', '查看', n.link] : null });
    });
    store('cc.popped', popped.slice(-200));
  }
  var popAckOrig = A.popAck;
  A.popAck = function () {
    var p = S.popup;
    if (p && p.msgId) markRead([p.msgId]);
    if (p && /^a/.test(p.id) && popAcked.indexOf(p.id) < 0) { popAcked.push(p.id); store('cc.popack', popAcked.slice(-100)); }
    return popAckOrig.apply(this, arguments);
  };
  var popGoOrig = A.popGo;
  A.popGo = function () {
    var p = S.popup;
    jumpTarget = p && p.link ? p.link[2] : null;
    return popGoOrig.apply(this, arguments);
  };
  var msgOpenOrig = A.msgOpen;
  A.msgOpen = function (id) {
    var m = msgList.filter(function (x) { return String(x.id) === id; })[0];
    var r = msgOpenOrig.apply(this, arguments);
    if (m && !m.read_at) { m.read_at = Date.now() / 1000; markRead([m.id]); }
    return r;
  };
  A.msgReadAll = function () {
    MSGS.forEach(function (m) { m.read = true; }); P.render();
    markRead(null, true).then(function () { P.toast('已全部标为已读'); });
  };
  // 跳转：工单 → 那一张工单；续费 → 官网自助续费（Claude 账号的到期提醒也是：号跟着全套一起续）；
  // 订单、商品、官网页面在原型里就是「在网页上打开」。目标：弹窗里点的记在 jumpTarget，列表里点的原型接在后面（arg）。
  A.msgGo = function (kind, arg) {
    var target = jumpTarget != null ? jumpTarget : arg;
    jumpTarget = null;
    if (target == null) {
      var m = msgList.filter(function (x) { return String(x.id) === S.msgOpen; })[0];
      target = m && m.link ? m.link.target : null;
    }
    if (kind === 'renewal') { A.renewWeb(); return; }
    S.newTicket = null;
    A.go('tickets');
    if (target) { S.ticketSel = String(target); markSeen(String(target)); loadImages(); P.render(); }
  };

  // 我的账号（docs/01 §5.12）：列表只有账号邮箱，没有邮箱密码；登录走「登录 Claude」。
  function findAi(id) { return AIACC.filter(function (a) { return a.id === String(id); })[0]; }
  // 服务端的状态 → 原型的写法：使用中 active、已到期收回 reclaimed、已停用 stopped，停用的退过款是 refunded（「已停用 · 已退款 ¥X」）。
  // 认不出的当已停用：登录信息服务端也只给使用中的号。
  function aiSt(x) {
    var s = x.state || 'active';
    if (s === 'active' || s === 'reclaimed') return s;
    return x.refund_cents > 0 ? 'refunded' : 'stopped';
  }
  // 离到期还有几天（不满一天算一天；7 天以内卡上提示「X 月 X 日到期」和「续费」）。每次取账户信息时重算，App 一直开着跨了天也对。
  function aiDays() {
    var now = Date.now() / 1000;
    AIACC.forEach(function (a) { a.days = a.until ? Math.ceil((a.until - now) / 86400) : null; });
  }
  // 保障状态（docs/01 §5.12）：「现在被封可退 ¥X」。号上有金额（在用、封号可退）才显示；金额每天变，号的列表只在版本变了才重新要，
  // 所以在用的号优先用随心跳更新的 suite.refund_now_cents（更新）。没有金额的不显示标签和「看怎么算的」。
  function aiRefund() {
    var fresh = account && account.suite && account.suite.refund_now_cents;
    AIACC.forEach(function (a) {
      a.refundNow = a.refundCents == null ? null : money(fresh != null ? fresh : a.refundCents);
    });
  }
  // 号已经收回（服务端回 gone，守护程序换成固定的这句话，并且已经重新拉了列表）。
  var AI_GONE = /这个号已经收回/;
  // 灰卡片标签下面那行小字（docs/01 §5.12）：按收回、停用的原因（服务端的 end_reason）写，标签还是原型那三种。没退款的不提金额；
  // 钱可能退回余额，统一写「已退回」。没给原因的照状态：已到期收回写到期那行，已停用照原型写确认被封那两行。
  var GONE_WHY = ['expired', 'refund', 'clawback', 'banned', 'void'];
  window.CC_AI_GONE = function (a) {
    var why = GONE_WHY.indexOf(a.reason) >= 0 ? a.reason : a.st === 'reclaimed' ? 'expired' : 'banned';
    var at = a.endAt, back = a.refund ? '¥' + a.refund + ' 已退回，' : '';
    if (why === 'banned') return at + '确认被封，' + (back ? back + '软件和线路照常用到到期' : '没有退款，原因见消息') + '；登录信息已收起';
    if (why === 'refund') return at + '退款收回，' + back + '软件和线路照常用到到期；登录信息已收起';
    if (why === 'clawback') return at + '停用（违规使用，不退款），原因见消息；登录信息已收起';
    if (why === 'void') return at + '核实时登录信息对不上，账号作废，原因见消息；登录信息已收起';
    return at + '到期收回，登录信息已收起，这个号不能再续';
  };
  function loadAi() {
    return call({ type: 'ai_accounts' }).then(function (r) {
      // 显示着的登录信息（只在内存里）：列表重拉以后同一个号还是在用的密码号就接着显示，别的丢掉。
      var keep = S.aiShown && findAi(S.aiShown);
      AIACC.length = 0;
      (r.items || []).forEach(function (x) {
        // 收回、停用的时间；已到期收回的没给就用到期日。
        var st = aiSt(x), end = x.ended_at || (st === 'reclaimed' ? x.paid_until : null);
        // 怎么登录：密码号 password 卡上是「登录信息」一块，别的（和没给的老服务端）都是一键登录。
        var login = x.login === 'password' ? 'password' : 'link';
        AIACC.push({ id: String(x.id), plat: esc(x.platform_name || x.platform), tier: esc(x.tier), email: esc(x.email),
          since: x.issued_at ? day(x.issued_at) + '发放' : '', login: login, hasAux: !!x.has_aux, pw: '', aux: '', auxpw: '',
          paid: x.paid_until ? day(x.paid_until) : '', until: x.paid_until || null,
          st: st, refund: st === 'refunded' ? money(x.refund_cents) : '', endAt: end ? day(end) : '', reason: x.end_reason || null,
          refundCents: st === 'active' && x.refund_now_cents != null ? x.refund_now_cents : null });
      });
      aiDays(); aiRefund();
      S.ai = AIACC.length > 0;
      // 正在登录的号收回、停用了（到期那一刻、核实被封）：不再等。
      var on = S.aiLogin && findAi(S.aiLogin.id);
      if (S.aiLogin && !(on && on.st === 'active')) A.aiLoginCancel();
      // 显示着登录信息的号收回、停用了，或者不再是密码号：马上收起。
      var shown = S.aiShown && findAi(S.aiShown);
      if (S.aiShown && !(shown && shown.st === 'active' && shown.login === 'password' && keep)) A.aiHide();
      else if (shown) { shown.pw = keep.pw; shown.aux = keep.aux; shown.auxpw = keep.auxpw; }
      if (!S.ai && !S.aiPrep && S.page === 'accounts') S.page = 'overview';
      if (!busy()) P.render();
    }).catch(function () {});
  }
  // 登录 Claude（docs/01 §5.12）：壳开始一次（它先打开 Claude 登录页），之后每秒问一次进度；等到了壳自己打开登录链接，
  // 这里只收到状态。还剩几秒、进度条原地改，不整页重画。
  var loginPoll = null;
  function loginStopPoll() { clearInterval(loginPoll); loginPoll = null; }
  function loginState(id, ui) {
    var st = ui && ui.state;
    if (st === 'waiting') {
      var left = ui.left_secs || 0, fresh = !(S.aiLogin && S.aiLogin.id === id && S.aiLogin.phase === 'wait');
      S.aiLogin = { id: id, phase: 'wait', left: left, hold: true };
      var t = document.querySelector('[data-ailogin-left]'), b = document.querySelector('[data-ailogin-bar]');
      if (fresh || !t) P.render();
      else { t.textContent = Math.floor(left / 60) + ':' + ('0' + left % 60).slice(-2); if (b) b.style.width = Math.round(left * 100 / 120) + '%'; }
      return true;
    }
    loginStopPoll();
    if (st === 'paused') { S.aiLogin = null; P.render(); P.toast('保护暂停着，恢复防护以后再登录', 'info'); return false; }
    S.aiLogin = { id: id, phase: 'wait', left: 0 };
    A.aiLoginDone(st === 'opened' ? 'ok' : st === 'timeout' ? 'timeout' : 'error');
    return false;
  }
  function loginFailed(e) {
    loginStopPoll();
    S.aiLogin = null; P.render();
    var m = msg(e);
    P.toast(m, 'info');
    // 已经收回、停用的号：重新要列表，卡片变灰。
    if (AI_GONE.test(m)) loadAi();
  }
  A.aiLogin = function (id) {
    id = String(id);
    if (S.status === 'paused' || S.busy.aiLogin) return;
    loginStopPoll();
    S.busy.aiLogin = true;
    S.aiLogin = { id: id, phase: 'wait', left: 120, hold: true }; P.render();
    T.core.invoke('ai_login', { id: +id }).then(function (ui) {
      S.busy.aiLogin = false;
      if (loginState(id, ui)) {
        loginPoll = setInterval(function () {
          if (!S.aiLogin || S.aiLogin.id !== id || S.aiLogin.phase !== 'wait') { loginStopPoll(); return; }
          T.core.invoke('ai_login_poll', { id: +id }).then(function (u) { if (S.aiLogin && S.aiLogin.id === id) loginState(id, u); }).catch(loginFailed);
        }, 1000);
      }
    }).catch(function (e) { S.busy.aiLogin = false; loginFailed(e); });
  };
  A.aiLoginCancel = function () {
    loginStopPoll();
    S.aiLogin = null; P.render();
    T.core.invoke('ai_login_cancel').catch(function () {});
  };
  // 密码号「显示登录信息」（docs/01 §5.12）：经守护程序现取，取到了再交给原型显示（它自己计时、5 分钟收起）；收起时把内存里的也清掉。
  // 暂停着不取（按钮本来就灰着）。号已经收回、或者变回一键登录了：说那句话、收起、重新要列表。
  var AI_NOT_PW = /一键登录，没有邮箱密码/;
  function forgetSecrets() { AIACC.forEach(function (a) { a.pw = ''; a.aux = ''; a.auxpw = ''; }); }
  var aiShowOrig = A.aiShow, aiHideOrig = A.aiHide;
  A.aiShow = function (id) {
    id = String(id);
    if (S.status === 'paused' || S.busy.aiShow) return;
    S.busy.aiShow = true;
    call({ type: 'ai_secrets', id: +id }).then(function (r) {
      S.busy.aiShow = false;
      var a = findAi(id);
      if (!a || a.st !== 'active' || a.login !== 'password' || S.status === 'paused') return;
      a.pw = esc(r.email_password || ''); a.aux = esc(r.aux_email || ''); a.auxpw = esc(r.aux_password || '');
      aiShowOrig(id);
    }).catch(function (e) {
      S.busy.aiShow = false;
      var m = msg(e);
      P.toast(m, 'info');
      if (AI_GONE.test(m) || AI_NOT_PW.test(m)) { A.aiHide(); loadAi(); }
    });
  };
  A.aiHide = function () { forgetSecrets(); return aiHideOrig.apply(this, arguments); };
  // 原型自己到点收起时也把内存里的清掉；保护暂停了（显示着的时候）马上收起。
  setInterval(function () {
    if (S.aiShown && S.status === 'paused') A.aiHide();
    else if (!S.aiShown && AIACC.some(function (a) { return a.pw || a.auxpw; })) forgetSecrets();
  }, 1000);
  A.aiReportGo = function () {
    var id = S.dialog && S.dialog.id;
    if (!id) return;
    S.dialog = null; P.render();
    call({ type: 'ai_report', id: +id }).then(function () {
      S.aiReported[id] = true; S.aiGuardOpen = id;
      P.toast('已收到，正在核实账号状态');
      loadTickets(); loadMessages();
    }).catch(function (e) { P.render(); P.toast(msg(e), 'info'); });
  };

  /* ---------- 菜单栏 / 托盘、系统通知、外观 ---------- */
  var SHORT = {
    traffic: '本月流量已用完', route: '线路暂时不可用', node: '线路处理中', checking: '正在确认原因',
    unbound: '本设备已解绑', service: '服务维护中', component: '防护组件未就绪', banned: '账户已停用', rotated: '独享线路更新中',
    rules: '防护规则没装上', settings: '防护设置待更新', trialExpired: '免费试用已到期', taken: '别的代理软件占着网络'
  };
  // 到期：全套和老套餐写法不一样（「全套已到期」「软件已到期」），跟断网页一致。
  function shortOf(r) { return r === 'expired' ? P.PM.expired.short : SHORT[r]; }
  function mmss(s) { s = Math.max(0, s | 0); return ('0' + Math.floor(s / 60)).slice(-2) + ':' + ('0' + s % 60).slice(-2); }
  // 菜单栏显示网速（只有 macOS 画得出来）：默认开，记在本机。
  var traySpeed = store('cc.traySpeed') !== false;
  function trayState() {
    var st = S.status, title = { ok: '已保护', starting: '启动中', prep: '准备中', down: '防护没有运行' }[st] || '';
    if (st === 'pm') title = '已断网 · ' + (shortOf(S.reason) || '真实 IP 没有暴露');
    if (st === 'paused') title = '防护已暂停 · ' + mmss(S.pause ? S.pause.left : 0);
    var r = ROUTES[S.route], city = P.TZN.city || '';
    var line = r && r.n !== '-' ? '线路 ' + r.n + (r.tag ? ' · ' + r.tag : '') : '—';
    // 有几个出口的（docs/01 §5.2「多个出口」）：线路前面写当前出口的名字（「我自己的 VPS · 线路 1」）。
    var ex = curExit();
    if (ex && line !== '—') line = (ex.raw.name || '出口') + ' · ' + line;
    // 菜单顶上的两三行：第一行状态（带地点），第二行灰字，第三行出口位置（只在已保护、启动中有）。
    var head = title, sub = '', third = '';
    // 已保护：状态和连续保护时长、线路和延迟、出口 IP 和地点（地点只写一次）。
    if (st === 'ok') { head = '已保护' + (S.guard ? ' · 已连续 ' + S.guard.dur : ''); sub = line + (r && typeof r.ms === 'number' ? ' · ' + r.ms + ' ms' : ''); third = '出口 ' + curIp + (city ? ' · ' + city : ''); }
    else if (st === 'paused') { head = '防护已暂停 · ' + mmss(S.pause ? S.pause.left : 0) + ' 后恢复'; sub = '你访问的网站能看到你的真实 IP'; }
    else if (st === 'pm') { sub = '真实 IP 没有暴露'; }
    else if (st === 'down') { sub = '真实 IP 可能暴露'; }
    else if (st === 'starting') { sub = '正在建立独享线路'; third = '出口 ' + curIp + (city ? ' · ' + city : ''); }
    else if (st === 'prep') { head = S.ownExit === 'waiting' ? '准备中 · 还没接出口' : '准备中 · 线路开通中'; sub = S.prepPaused ? '已先断网，只能访问局域网' : S.ownExit === 'waiting' ? '接上之前网站能看到你的真实 IP' : '开通前网站能看到你的真实 IP'; }
    // 免费试用到期、退出过：不接管网络，菜单只有「购买单独软件…」「退出软件」（壳按 trial_stopped 搭）。
    var stopped = S.screen === 'trialStopped';
    if (stopped) { title = head = '免费试用已到期'; sub = '现在没有保护，电脑照常上网'; third = ''; }
    // 实时网速：macOS 画在菜单栏图标右边（菜单里能关），Windows 写进托盘提示。
    var rate = st === 'ok' && S.rate ? { up: '↑ ' + P.fmtRate(S.rate.up), down: '↓ ' + P.fmtRate(S.rate.down) } : null;
    return {
      title: title,
      tooltip: title + (rate ? '\n' + rate.down + '　' + rate.up : '') + (msgUnread ? ' · ' + msgUnread + ' 条未读消息' : ''),
      speed: rate && traySpeed ? rate : null,
      speed_on: traySpeed,
      head: head, sub: sub, third: third,
      // 全套到期没续（docs/01 §4 第 8 步）：菜单给「接入你的出口」「重新开通全套」，不给续费。
      kind: stopped ? 'trial_stopped' : st, reason: st === 'pm' ? (S.reason === 'expired' && suiteKind() !== 'old' ? 'lapsed' : S.reason) : '',
      routes: ROUTES.map(function (x, i) { return { i: i, name: '线路 ' + x.n + (x.tag ? ' · ' + x.tag : ''), ms: typeof x.ms === 'number' ? x.ms : null, cur: i === S.route }; }).filter(function (x) { return !/线路 - /.test(x.name); }),
      auto: S.routeMode !== 'manual',
      unread: msgUnread,
      exit: st === 'paused' ? '你的真实 IP' : st === 'pm' ? '已断网' : st === 'down' ? '不确定' : (city ? city + ' · ' : '') + curIp,
      line: line,
      paused: st === 'paused',
      no_pause: stopped || st === 'prep' || st === 'starting' || st === 'down',
      // 红点：未读消息、工单新回复、没关的重要公告合在一起。
      dot: msgUnread > 0 || S.tickets.some(function (t) { return t.unread; }) || (!!bannerNow && !!bannerNow.important)
    };
  }
  // 菜单上点的动作（壳排成队列，这里取走照常发请求）。要开主窗口的那几项壳自己先把窗口叫出来。
  var TRAY = {
    refresh: function () { A.trayRefresh(); },
    resume: function () { A.resume(); },
    switch: function () { A.pmSwitch(); },
    restart: function () { A.restart(); },
    daemon_start: function () { A.daemonStart(); },
    check: function () { A.go('check'); A.checkStart(); },
    // 流量用完买流量还是续费面板（macOS 原型叫 renewOpen，Windows 原型叫 renew）；到期续费去官网。
    renew: function () { A.go('overview'); if (S.status === 'pm' && S.reason === 'traffic') (A.renewOpen || A.renew)(); else A.renewWeb(); },
    ownexit: function () { A.go('overview'); A.dialog('ownexit'); },
    upgrade: function () { A.upgrade(); },
    // 免费试用：买单独软件（打开官网）；到期断网时「退出软件」先在主窗口里确认、要电脑密码。
    buy_soft: function () { A.buySoft(); },
    trialquit: function () { A.go('overview'); A.dialog('trialQuit'); },
    contact: function () { A.go('overview'); A.contact(); },
    messages: function () { A.go('messages'); },
    // 菜单栏的暂停也要先确认（docs/01 §4 第 6 条）：壳已经把主窗口叫出来，这里打开暂停弹窗、选好时长。
    pauselogin10: function () { openPauseDialog(10, true); },
    quitall: function () { A.dialog('quitAll'); },
    speed: function () { traySpeed = !traySpeed; store('cc.traySpeed', traySpeed); syncTray(); }
  };
  [10, 30, 60].forEach(function (m) {
    TRAY['pause' + m] = function () { openPauseDialog(m, false); };
  });
  function openPauseDialog(min, login) {
    if (S.screen !== 'main') return;
    S.dialog = { type: 'pause', min: min, login: login };
    P.render();
  }
  function trayDo(a) {
    var m = /^route:(.+)$/.exec(a);
    if (m) { if (m[1] === 'auto') A.routeMode('auto'); else A.pickRoute(+m[1]); return; }
    if (TRAY[a]) TRAY[a]();
  }
  function syncTray() {
    T.core.invoke('tray_update', { state: trayState() }).catch(function () {});
    T.core.invoke('tray_take').then(function (list) { (list || []).forEach(trayDo); }).catch(function () {});
  }
  // 系统通知：同一件事只发一次（记在本机）；设置里关了「通知」就不发。
  var notified = store('cc.notified') || {};
  function notifyOnce(key, title, body) {
    if (notified[key]) return;
    notified[key] = Date.now();
    var keys = Object.keys(notified);
    if (keys.length > 200) keys.sort(function (a, b) { return notified[a] - notified[b]; }).slice(0, keys.length - 200).forEach(function (k) { delete notified[k]; });
    store('cc.notified', notified);
    if (S.notify) T.core.invoke('notify', { title: title, body: body || '' }).catch(function () {});
  }
  function notifyNotices(list) {
    list.forEach(function (n) { if (n.notify) notifyOnce('notice.' + n.id, n.title, n.body); });
  }
  var lastStatus = null, lineWaiting = false;
  function notifyStatus() {
    var st = S.status;
    if (lastStatus && st !== lastStatus) {
      if (st === 'pm') notifyOnce('st.' + Date.now(), '已断网，真实 IP 没有暴露', shortOf(S.reason) || '正在自动处理');
      else if (st === 'ok' && lastStatus === 'pm') notifyOnce('st.' + Date.now(), '已恢复保护', '真实 IP 已隐藏');
    }
    // 线路从准备中（等我们开通，不是没激活、等接自己的出口、试用停用）经启动中到保护中：说一声线路开通了。
    if (st === 'prep') lineWaiting = S.screen === 'main' && S.ownExit !== 'waiting' && suiteKind() !== 'soft' && suiteKind() !== 'trial';
    else if (st === 'ok') {
      if (lineWaiting) { notifyOnce('line.' + Date.now(), '独享线路开通了，已开始保护', '真实 IP 已隐藏'); P.toast('独享线路开通了，已开始保护'); }
      lineWaiting = false;
    } else if (st !== 'starting') lineWaiting = false;
    lastStatus = st;
  }
  // 外观、通知开关：记在本机。外观还要同步给窗口（App 壳的 set_theme）：macOS 的侧栏材质、红绿灯和 Windows 的 Mica
  // 是系统按窗口的外观画的，不同步的话深色界面配浅色侧栏。跟随系统时交回系统（null）。
  function syncTheme() {
    var t = S.appearance === 'light' || S.appearance === 'dark' ? S.appearance : null;
    T.core.invoke('set_theme', { theme: t }).catch(function () {});
  }
  ['appearance'].forEach(function (k) {
    var v = store('cc.' + k);
    if (v) S[k] = v;
    var orig = A[k];
    if (orig) A[k] = function (x) { store('cc.' + k, x); var r = orig.apply(this, arguments); syncTheme(); return r; };
  });
  syncTheme();
  var nv = store('cc.notify');
  if (nv !== null) S.notify = !!nv;
  var notifyOrig = A.notify;
  if (notifyOrig) A.notify = function () { var r = notifyOrig.apply(this, arguments); store('cc.notify', !!S.notify); return r; };
  var pollOrig = poll;
  poll = function () { return pollOrig().then(function () { notifyStatus(); syncTray(); }); };

  applyLines({});
  poll();
  // 每秒查一次状态：守护程序发现绕过（docs/03 L8，Windows 要求 3 秒内）后界面最多再等 1 秒就弹窗。
  setInterval(poll, 1000);
  loadAccount();
  setInterval(loadAccount, 30000);
  setInterval(function () { if (!accountLoading && (accountFails || !account)) loadAccount(); }, 3000);
  // 续费处理中：结果早一点显示出来（这是问守护程序，不连服务端）。
  setInterval(function () { if (account && account.renewal && account.renewal !== 'done') loadAccount(); }, 5000);
  loadZone(); loadRules(); loadRoute();
  setInterval(function () { if (account && account.activated) { loadZone(); loadRules(); } }, 30000);
  // 工单近乎实时：开着工单页每 5 秒取一次，其他时候每 30 秒（客服一回复就弹通知）。
var ticketsAt = 0;
setInterval(function () {
  if (!(account && account.activated)) return;
  var every = S.page === 'tickets' ? 5000 : 30000;
  if (Date.now() - ticketsAt >= every) { ticketsAt = Date.now(); loadTickets(); }
}, 1000);
})();

