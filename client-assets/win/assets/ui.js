/* LEYRA Windows 客户端 · 高保真可点击原型。状态集中在 S，render() 按 S 重绘。 */
(function () {
  'use strict';
  var H = window.CCHahaeasy, I = H.icon;

  /* ---------------- 假数据 ---------------- */
  /* 出口所在地、出口时区、电脑时区；认出来的冲突软件；套餐、流量、本机设备和防护时长：浏览器里是演示值，App 里由守护程序给（空字符串表示还不知道，界面就不提） */
  var TZN = { city: '东京', exit: '日本标准时间', sys: '北京时间' };
  /* 冲突软件（01 §5.3「说出是谁」）：kind 是 named 查出了是谁（帮我退出）/ stale 代理软件退了、系统代理还开着（帮我关掉）/
     unknown 查不出（写证据和去哪找，发给客服）。msg 是守护程序给的那句话。 */
  var CONFL = { name: '某某加速器', kind: 'named', msg: '检测到 某某加速器（游戏加速器）正在接管网络（网卡 utun4），它会和本软件冲突，请先退出它。', evidence: '' };
  var CONFL_DEMO = {
    named: { name: 'Tailscale', kind: 'named', msg: '检测到 Tailscale（VPN）正在接管网络（网卡 utun4），它会和本软件冲突，请先退出它。', evidence: '' },
    stale: { name: '', kind: 'stale', msg: '之前的代理软件已经退出，但系统代理还开着（HTTP 127.0.0.1:7890、HTTPS 127.0.0.1:7890），它会和本软件冲突。点「帮我关掉」恢复，关掉后可以撤销。', evidence: 'HTTP 127.0.0.1:7890、HTTPS 127.0.0.1:7890' },
    unknown: { name: '', kind: 'unknown', msg: '有程序建了虚拟网卡 utun4，正在接管网络，它会和本软件冲突，请先退出它。看看屏幕右上角菜单栏的图标和「系统设置 → VPN」。', evidence: 'utun4' }
  };
  var INFO = { plan: '全套 · Pro', used: '38.2 GB', limit: '200 GB', pct: 19.1, reset: '11 月 18 日', device: 'ThinkPad X1 · Windows 11', bound: '10 月 18 日', maintEnd: '02:00', uptime: '3 天 4 小时', daily: [1.4, 0.9, 2.6, 1.8, 0.4, 0.3, 3.1, 2.2, 1.7, 4.0, 2.5, 1.1], dailyFrom: '10 月 18 日', usedGB: 38.2, limitGB: 200, resetLeft: 18 };
  var ACCOUNT = 'A-20481', IP = '203.0.113.24', CODE = 'K7QM-29XF-H4TD-8NWP', CHECK_ID = 'CK-20261027-0412';
  /* 售后渠道：[显示名（带备注）, 号码, 类型, 名称, 备注]；类型是 QQ 群号的那条另外固定显示在「消息 → 公告」顶上 */
  var CHANNELS = [];
  /* 出口（01 §5.2「多个出口」）：一个出口是一个链接或一份订阅，里面有几条线路；不包赔的电脑（老套餐、单独软件、免费试用）可以有几个出口，自己切换。
     全套包赔，只有我们给的一个出口，没有这一层。own：用户自己加的（我们分配的不能删、不能改链接） */
  var EXITS = [
    { name: '洛杉矶-住宅', own: false, city: '美国·洛杉矶', ip: '203.0.113.24', n: 4, ms: 68, sig: 4 },
    { name: '洛杉矶-外部-02', own: false, city: '美国·洛杉矶', ip: '198.51.100.43', n: 3, ms: 74, sig: 4 },
    { name: '我自己的 VPS', own: true, city: '日本·东京', ip: '192.0.2.77', n: 2, ms: 96, sig: 3 },
    { name: '朋友给的订阅', own: true, city: '新加坡', ip: '192.0.2.201', n: 3, ms: 88, sig: 3, sub: true }
  ], EXIT_MAX_OWN = 5;
  var ROUTES = [
    { n: 1, tag: '稳定', desc: '日常使用', ms: 68, ok: 100, sig: 4, speed: { mbps: 86, ago: 180, measured: true } },
    { n: 2, tag: '适合发图和上传', desc: '上传图片、视频更快', ms: 112, ok: 98, sig: 3, speed: { mbps: 42, ago: 600, measured: false } },
    { n: 3, tag: '网络受限时用', desc: '前两条被干扰时使用', ms: 146, ok: 95, sig: 2 },
    { n: 4, tag: '备用', desc: '其他线路都不好时使用', ms: 152, ok: 95, sig: 2 }
  ];
  var STAGES = [['正在准备检查', 0.4], ['正在检查防护程序', 0.4], ['正在检查电脑环境', 0.4], ['正在检查防护规则', 0.4], ['正在检查真实 IP 是否隐藏', 0.4], ['正在核对出口位置和时区', 0.4], ['正在检查账户和服务', 0.4], ['正在生成报告', 0.4]];
  var GROUPS = [
    ['环境', ['系统版本', '防护程序权限', '系统时间', '其他网络软件', '系统防火墙可用', '安全软件和网络过滤', '能建立独享线路', '磁盘空间', '服务器连接', '开机自动运行']],
    ['防护', ['防护规则', '系统防火墙已开启', '防护组件', '独享线路已接入', '所有流量走独享线路', '网址解析', '浏览器保护设置', '暂停状态']],
    ['真实 IP 检查', ['出口 IP', '绕开独享线路的连接', '绕开独享线路的网址查询', '网址解析不经过国内', '浏览器看到的 IP', '浏览器绕开独享线路', '另一种网络地址', '绕开独享线路的软件', '1 分钟网络观察', '浏览器实测']],
    ['一致性', ['时区']],
    ['服务', ['授权和到期', '配置是否最新', '线路质量', '线路速度', '专用通道', '最近一次同步']]
  ];
  var BUILTIN_LAN = [['192.168.0.x', '常见家用路由器'], ['192.168.1.x', '常见家用路由器'], ['192.168.31.x', '常见家用路由器'], ['192.168.100.x', '常见光猫管理页']];
  var PM = {
    checking: { short: '正在确认原因' },
    expired: { short: '全套已到期', get desc() { return '全套已于 ' + S.exp.date + '到期。为了不暴露真实 IP，网络已断开。接入你自己的出口，或重新开通全套。'; }, btns: [['primary', '接入你的出口', 'dialog:ownexit'], ['secondary', '重新开通全套', 'upgrade']] },
    traffic: { short: '本月流量已用完', get desc() { return '本月 ' + INFO.limit + ' 流量已用完。为了不暴露真实 IP，网络已断开。加购流量或 ' + INFO.reset + '重置后自动恢复。'; }, btns: [['primary', '联系售后', 'contact']] },
    route: { short: '线路暂时不可用', desc: '线路暂时连不上，正在自动重试。为了不暴露真实 IP，网络保持断开。如果 15 分钟内没恢复，客服会自动收到提醒。', desc2: '在酒店、机场等需要网页登录的 Wi-Fi 上？可先暂停防护完成登录，线路连上后自动恢复；也可以改连手机热点。', btns: [['primary', '刷新连接', 'pmRefresh'], ['secondary', '换一条线路', 'pmSwitch']], pause: '暂停防护去登录' },
    exits: { short: '所有出口都连不上', desc: '你添加的出口现在都连不上，为了不暴露真实 IP 网络已断开。软件每半分钟再试一次，有出口恢复就自动接上。', desc2: '也可以去「线路」换个链接、添加新出口，或找卖家确认节点是否正常。', btns: [['primary', '去线路页', 'go:routes'], ['secondary', '添加出口', 'exitAdd']] },
    exitoff: { short: '出口连不上', desc: '正在用的出口连不上，为了不暴露真实 IP 网络已断开。你关闭了「出口连不上时自动换」，所以没有切到其他出口，网站看到的 IP 不会变。', desc2: '可以去「线路」手动换一个出口，或者打开自动换。', btns: [['primary', '去线路页', 'go:routes'], ['secondary', '打开自动换', 'autoExitOn']] },
    node: { short: '线路处理中', desc: '线路暂时连不上，客服已收到提醒、正在处理。为了不暴露真实 IP，网络已断开。' },
    unbound: { short: '本设备已解绑', desc: '本设备已解绑，本机已停止服务，网络已断开。还要在这台电脑上用，输入原来的授权码恢复；要直接上网可以暂停防护（会暴露真实 IP）。', btns: [['primary', '用原授权码恢复', 'dialog:restoreCode'], ['secondary', '联系客服', 'contact']] },
    service: { short: '服务维护中', get desc() { return '服务维护中' + (INFO.maintEnd ? '，预计 ' + INFO.maintEnd + ' 结束' : '') + '。维护期间网络断开，结束后自动恢复。'; } },
    component: { short: '防护组件未就绪', desc: '防护组件没能启动，网络已断开。可以点「重新启动防护」；还不行请联系客服。', btns: [['primary', '重新启动防护', 'restart']] },
    banned: { short: '账户已停用', desc: '这个账户已停用。为了不暴露真实 IP，网络已断开。有疑问请联系客服。', btns: [['primary', '联系客服', 'contact']] },
    rotated: { short: '独享线路更新中', desc: '独享线路正在更新。为了不暴露真实 IP，网络暂时断开。更新完成后自动恢复，一般不超过 1 分钟。' },
    rules: { short: '防护规则没装上', desc: '防护规则没能装上，正在自动重试。为了不暴露真实 IP，网络保持断开。还不行请联系客服。', btns: [['primary', '重新启动防护', 'restart']] },
    trialExpired: { short: '免费试用已到期', get desc() { return '免费试用已于 ' + S.trialEnd.date + '到期。为了不暴露真实 IP，网络已断开。'; }, btns: [['primary', '购买', 'buySoft'], ['secondary', '换授权码', 'dialog:switchCode'], ['secondary', '退出软件', 'dialog:trialQuit']], noPause: true },
    settings: { short: '防护设置待更新', desc: '这次收到的防护设置没通过检查。为了不暴露真实 IP，网络已断开。请联系客服重新下发。', btns: [['primary', '联系客服', 'contact']] }
  };
  var TTYPES = [
    { n: '无法上网', help: [['刷新连接', 'refreshAny'], ['换一条线路', 'go:routes'], ['网络恢复工具', 'rescueOpen']], q: '从什么时候开始？换线路后好了吗？' },
    { n: '担心真实 IP 暴露', help: [['安全检查', 'runCheck'], ['更换时区', 'tzHelp']], q: '是在哪里看到了提示？' },
    { n: '速度慢或不稳定', help: [['换一条线路', 'go:routes'], ['刷新连接', 'refreshAny']], q: '一直慢，还是某个时段慢？' },
    { n: '某个网站或软件不可用', help: [['换一条线路', 'go:routes']], q: '是哪个网站或软件？' },
    { n: '安装升级或安全软件拦截', help: [['安装说明', 'dialog:install']], q: '请附上弹窗截图', shot: true },
    { n: '局域网打印机或公司内网', help: [['局域网地址都已内置，看看设置', 'go:settings']], q: '设备或系统的地址是？' },
    { n: '账户续费流量', help: [['在个人中心查看到期和流量', 'go:profile']], q: null },
    { n: '换电脑重装激活', help: [['重装可用原授权码自助恢复', 'screen:activate']], q: '是换了电脑，还是重装系统？' },
    { n: '退款 / 封号包赔', help: [['退款规则说明', 'dialog:refund'], ['我的账号里点「账号登不上了」', 'go:accounts']], q: '原因' },
    { n: '建议或其它', help: [], q: null }
  ];
  /* 免责声明（docs/附录-免责声明.md，01 §4 第 3 步）：第一次打开、以后改了版本，要一字不差地输入 CONSENT_TEXT 才能用。
     改正文要把 DISCLAIMER_VER 加一（和附录的版本号一致），客户端会重新请用户确认。 */
  var DISCLAIMER_VER = 1;
  var CONSENT_TEXT = '我同意以上服务条款，承诺完全遵守并合规使用';
  var DISCLAIMER = [["使用说明", "客户端连接你配置的自有服务器。界面上的状态以本机实际检查结果为准。"], ["状态说明", "暂停防护或防护组件不可用时，请留意界面的网络状态提示。"], ["设备凭据", "连接文件属于当前设备，请不要向其他人分享。"]];
  var TERMS = [["数据与服务器", "设备记录、消息和工单由你配置的服务器保存。"], ["操作影响", "连接、暂停、恢复和卸载可能改变本机的网络状态。"], ["服务说明", "客户套餐、计费、退款和数据保存政策由服务运营方另行制定并公布。"]];
  /* 配软件的 Claude 账号（01 §3.4、§5.12；演示：地址和密码都是 [示例] 假值）。一台电脑同一时间只有一个在用的号；到期收回、被封退款以后留一张灰卡片 30 天。
     st：active 使用中 / reclaimed 已到期收回 / refunded 已停用（已退款，refund 是金额）/ stopped 已停用（确认被封、包赔没通过，没退款）；days 是离到期还有几天，7 天以内卡上提示。
     保障状态（01 §3.4、§5.12，2026-11-09 改）：refundNow 是「现在被封可退」的金额（服务端按 KYC + 订阅、没用完的天数现算，每天变；
     演示 Max 5x 首月 30 天用了 10 天：1,030 × 20 ÷ 30 = 686.67）。客户端判断不了五条不退的情况，这里不写。
     Claude 没有密码，登录靠发到账号邮箱的登录链接。两种交付（01 §3.4「两种交付方式」）：login 是 link 的取件链接号，用户只拿到账号邮箱，
     点「登录 Claude」后服务端替他取登录邮件，客户端在这台电脑的浏览器里直接打开链接（§5.12「登录 Claude」）；login 是 password 的密码号，
     卡上是「登录信息」一块：邮箱密码（有辅助邮箱的连辅助邮箱和它的密码）由守护程序经服务端现取，只在内存里，显示 AI_SHOW_SECS 秒后收起（§5.12「显示登录信息」） */
  function aiCase(kind) {
    var now = { id: 'AI-302', plat: 'Claude', tier: 'Max 5x', email: 'claude-2048@example.com', since: '10 月 20 日发放', paid: '11 月 20 日', days: 14, st: 'active', refundNow: '686.67', login: 'link' };
    // 密码号（Outlook）：pw、aux、auxpw 是点了「显示登录信息」才现取的，这里是 [示例] 假值
    if (kind === 'password' || kind === 'passwordnoaux') { now.email = 'claude-2048@outlook.com'; now.login = 'password'; now.pw = '[示例] Mail-Pass-2048'; if (kind === 'password') { now.aux = 'backup-2048@example.com'; now.auxpw = '[示例] Aux-Pass-2048'; } }
    var old = { id: 'AI-301', plat: 'Claude', tier: 'Pro', email: 'claude-0193@example.com', since: '10 月 5 日发放', paid: '11 月 5 日', st: 'reclaimed', endAt: '11 月 5 日' };
    if (kind === 'soon') { now.since = '10 月 11 日发放'; now.paid = '11 月 11 日'; now.days = 5; now.refundNow = '171.67'; }
    if (kind === 'reclaimed') return [old];
    if (kind === 'refunded') { old.since = '10 月 20 日发放'; old.paid = '11 月 20 日'; old.st = 'refunded'; old.endAt = '10 月 31 日'; old.refund = '153.33'; return [old]; }
    if (kind === 'prepline' || kind === 'prepqueued') return [];
    if (kind === 'stopped' || kind === 'prepissuing') { old.since = '10 月 20 日发放'; old.paid = '11 月 20 日'; old.st = 'stopped'; old.endAt = '10 月 31 日'; return [old]; }
    if (kind === 'rebought') { now.since = '11 月 6 日发放'; now.paid = '12 月 6 日'; now.days = 30; return [now, old]; }
    return [now];
  }
  var AIACC = aiCase('ok');
  function aiLive(a) { return !a.st || a.st === 'active'; }
  function aiActive() { return S.ai && AIACC.some(aiLive); }
  /* 原型演示条「我的账号」的几种样子，和切过去那一刻会收到的消息（插在昨天那条后面，按时间排） */
  var AI_DEMOS = [['ok', '正常'], ['password', '密码号'], ['passwordnoaux', '密码号 · 没有辅助邮箱'], ['soon', '快到期'], ['reclaimed', '已到期收回'], ['refunded', '已退款'], ['stopped', '被封没退款'], ['rebought', '重新开通后的新号'], ['prepline', '准备中 · 等线路'], ['prepqueued', '准备中 · 排队'], ['prepissuing', '补号付了款 · 发放中']];
  var AI_MSGS = {
    soon: [{ id: 'M-AI-1', lv: 'normal', title: 'Claude 账号 11 月 11 日到期', body: '到期后收回，不能再续，要用请在到期前续费。', t: '11 月 4 日', link: ['renewal', '去续费', 'AI-302'] }],
    reclaimed: [{ id: 'M-AI-2', lv: 'normal', title: 'Claude 账号已到期收回', body: '登录信息已收起。想接着用，可以重新开通全套。', t: '昨天 14:20', link: ['upgrade', '重新开通全套'] }],
    refunded: [{ id: 'M-AI-3', lv: 'normal', title: '封号退款：已退款 ¥153.33', body: '已退款 ¥153.33，原路退回。想接着用，可以补号。', t: '10 月 31 日', link: ['refill', '补号'] }],
    stopped: [{ id: 'M-AI-4', lv: 'normal', title: '封号退款：未通过', body: '未通过：被封前一天在暂停防护期间登录过这个号，按使用规则不退款。想接着用，可以补号。', t: '10 月 31 日', link: ['refill', '补号'] }],
    rebought: [{ id: 'M-AI-2', lv: 'normal', title: 'Claude 账号已到期收回', body: '登录信息已收起。想接着用，可以重新开通全套。', t: '昨天 14:20', link: ['upgrade', '重新开通全套'] }]
  };
  var AI_ONLY = '只能在这台电脑上、保护开着时登录这个账号；登录链接由软件自动打开，不要转给别人或别的设备';
  /* 密码号的固定提示（01 §5.12） */
  var AI_ONLY_PW = '只能在这台电脑上、保护开着时登录这个账号和它的邮箱；不要在手机或其他设备上登录，不要改邮箱密码和安全设置';
  function aiOnly(a) { return a && a.login === 'password' ? AI_ONLY_PW : AI_ONLY; }
  /* 密码号的登录信息显示多久自动收起（秒） */
  var AI_SHOW_SECS = 300;
  /* 「登录 Claude」最多等登录邮件多久（秒），和守护程序那边一致 */
  var AI_LOGIN_SECS = 120;
  /* 消息（发给这个账户的，已读记在服务端）和公告（广播） */
  var MSGS = [
    { id: 'M-1005', lv: 'action', title: '封号退款：需要补充信息', body: '请在工单里说明**最后一次正常登录的时间**，以及登录前有没有暂停防护。', t: '今天 10:12', link: ['ticket', '去工单'], read: false },
    { id: 'M-1004', lv: 'normal', title: '工单有新回复', body: '你的工单「线路质量」有新回复，打开工单查看。', t: '今天 09:30', link: ['ticket', '查看工单'], read: false },
    { id: 'M-1003', lv: 'normal', title: '线路维护通知', body: '你所在的线路 11 月 3 日 02:00–03:00 维护，维护期间网络断开，结束后自动恢复。', t: '昨天 18:00', read: true },
    { id: 'M-1002', lv: 'normal', title: 'Claude 账号已发放', body: 'Claude · Pro 已发放。在「我的账号」里点「登录 Claude」，在登录页填账号邮箱、点「继续」，登录链接会在这台电脑上自动打开。', t: '10 月 20 日', read: true },
    { id: 'M-1001', lv: 'normal', title: '续费已完成', body: '有效期至 11 月 18 日。', t: '10 月 18 日', link: ['order', '查看订单'], read: true }
  ];
  var ANNS = [
    { id: 'N-12', kind: 'maint', title: '11 月 3 日 02:00–03:00 服务维护', body: '维护期间网络断开，结束后自动恢复。维护时间内不用提交工单。', t: '10 月 27 日' },
    { id: 'N-11', kind: 'normal', title: '线路 2 已优化', body: '上传图片和视频的速度提升，无需操作。', t: '10 月 25 日' }
  ];

  /* 最近事件（线路页）和概览的最近动态是同一份（01 §5.13）：切换线路、断网和恢复、暂停和恢复、安全检查结果、更新；只存在这台电脑上 */
  var EVENTS0 = [
    { time: '14:32', text: '安全检查一切正常' }, { time: '14:02', text: '换到线路 1 · 稳定' }, { time: '13:40', text: '防护恢复' },
    { time: '13:30', text: '暂停防护 10 分钟' }, { time: '昨天 23:40', text: '断网，3 秒后自动恢复' }, { time: '昨天 09:10', text: '更新到 1.0.0' }
  ];
  /* 帮助页（01 §5.13）：常见问题和官网「常见问题」同一份（后台「官网内容」改），使用指南是官网那几篇 */
  var FAQ = [
    ['怎么登录我们提供的 Claude 账号？', '在客户端「我的账号」点「登录 Claude」，在打开的登录页填上面的账号邮箱、点「继续」，登录链接到了软件会自动在这台电脑的浏览器里打开。只能在装了软件的那台电脑上、保护开着时登录。'],
    ['浏览器保护设置是什么？', '浏览器里有个通话用的功能，可能绕开软件的保护，让网站看到你的真实 IP。保护设置让浏览器也只经软件出去。Windows 上自动设置好；macOS 要在系统设置里点一次「安装」。'],
    ['断网了怎么办？', '断网是为了不暴露真实 IP。软件会写明原因：先点「刷新连接」，或者在软件里换一条试试；全套快到期就续费，到期了接上自己的出口或者升级到全套；酒店、机场要网页登录的 Wi-Fi，先暂停防护完成登录。还不行就提交工单。'],
    ['暂停防护和彻底退出有什么区别？', '暂停是临时的，选 10、30 或 60 分钟，到点自动恢复。彻底退出会撤掉防护、关掉软件，直到你再打开软件或重启电脑。两种期间网站都能看到你的真实 IP。'],
    ['打印机和 NAS 怎么用？', '家里和公司的局域网地址都已内置，打印机、NAS、路由器管理页直接能用，不用设置。连不上的，多半是设备换了地址，在电脑上重新添加一次。'],
    ['能在几台电脑上用？', '一个授权码只能用于一台电脑；换电脑请进售后 QQ 群联系客服。'],
    ['到期后会怎样？', '全套到期当天收回 Claude 账号、停掉加密通道，到期以后不能再续，想再用就升级到全套。软件已经买断，照样能用：接上你自己的出口之前保持断网，真实 IP 不会暴露。单独软件永久使用，不会到期。'],
    ['封号了怎么退？', '只针对全套里我们提供、配本软件用的 Claude 账号，被封时全套没到期。按使用规则被封，核实后按没用完的天数退会员的钱，软件和线路的钱不退；被封的号作废，想接着用可以补号。完整规则见条款。']
  ];
  var GUIDES = [["连接与首次使用", "导入服务器连接文件，核对设备和服务器信息。"], ["暂停防护", "暂停期间请留意网络状态提示。"], ["网络恢复", "遇到网络异常时使用设置中的网络恢复工具。"], ["重装系统后连接", "向管理员取得当前设备的连接文件后重新导入。"]];
  /* 诊断信息里有什么（01 §5.13），提交工单时默认附上 */
  var DIAG = ['软件版本和电脑系统', '防护状态和最近 24 小时的变化', '各条线路的质量和最近事件', '最近 3 次安全检查的结果', '冲突软件、局域网设置、网络类型（Wi-Fi、有线、热点）', '防护程序最近的运行记录（500 行，只在内存里，不存文件）'];

  /* ---------------- 状态 ---------------- */
  var S = {
    appearance: 'system', winW: 960,
    hist: { lines: [
      { name: '线路 1', pts: [[1680, 70], [1380, 66], [1080, 72], [780, 69], [480, 68], [180, 67], [0, 68]] },
      { name: '线路 2', pts: [[1680, 118], [1380, 110], [1080, 120], [780, 112], [480, null], [180, 116], [0, 112]] }
    ], events: EVENTS0.slice() }, rate: { up: 86000, down: 2400000 }, rateHist: (function () { var h = [], n = Date.now(); for (var i = 0; i < 30; i++) h.push({ t: n - (29 - i) * 2000, d: 1500000 + Math.round(900000 * Math.abs(Math.sin(i / 3.1)) + 300000 * Math.abs(Math.sin(i * 1.7))), u: 60000 + Math.round(40000 * Math.abs(Math.sin(i / 2.3))) }); return h; })(), speedTesting: false, speedAt: null,
    status: 'ok', reason: 'route', prepPaused: false,
    // 准备中页写的激活时间；Pro 全套能升到 Max 5x（服务端说能升才有按钮）；Claude 账号在准备（line 等线路 / queued 排队 / issuing 发放中）
    prepAt: '今天 14:20', tierUp: false, aiPrep: '',
    pause: null, starting: null,
    screen: 'main', page: 'overview', wizard: null,
    dialog: null, toast: null, warnToast: null,
    banner: null, alerts: { expire1d: false, update: false, conflict: false, expireSoon: false },
    routeMode: 'auto', route: 0,
    busy: {},
    check: { phase: 'idle' }, lastCheck: { at: '今天 14:32', kind: 'auto', problems: 0 },
    // 最近的检查（01 §5.8）：最近 10 次、30 天内，只存在这台电脑上
    checkHist: [
      { at: '今天 14:32', kind: 'auto', problems: 0, id: 'CK-20261106-0412' }, { at: '今天 09:05', kind: 'manual', problems: 0, id: 'CK-20261106-7QM2' },
      { at: '昨天 22:10', kind: 'deep', problems: 1, id: 'CK-20261105-H4TD' }, { at: '昨天 08:40', kind: 'auto', problems: 0, id: 'CK-20261105-29XF' },
      { at: '11 月 4 日 20:12', kind: 'manual', problems: 2, id: 'CK-20261104-8NWP' }, { at: '11 月 3 日 10:00', kind: 'auto', problems: 0, id: 'CK-20261103-K7QM' }
    ], guard: { dur: '3 小时 12 分', blocked: 23 }, timeSynced: false, conflictFixed: false,
    contact: '微信 demo_2048', editingContact: false,
    consentVer: DISCLAIMER_VER,
    tickets: null, ticketSel: null, tFilter: 'all', newTicket: null, replyShots: {}, lightbox: null, ticketImages: true,
    browsers: [{ n: 'Google Chrome', st: 'clean' }, { n: 'Microsoft Edge', st: 'none' }, { n: '夸克', st: 'lan' }],
    lan: [{ ip: '203.0.113.50', note: '公司内部系统', pub: true }], lanEdit: -1, lanMsg: null, advMsg: null, advOpen: false,
    notify: true, tzChanged: true, version: '1.0.0', newVersion: '1.1.0',
    tray: false, rescue: null, actErr: {}, notes: [],
    renew: { phase: null, at: null, pick: null, ticket: null }, renewDone: { node: false, traffic: false }, exp: { date: '11 月 18 日', days: 23 },
    // 单独软件 · 自带出口（01 §5.2）：'' 不是 / waiting 还没接出口 / connected 在用自己的出口 / upgraded 升级到了全套
    ownExit: '',
    exits: '', exit: 0,
    // 免费试用（01 §3.1）：见 trialDemo
    trial: '', trialEnd: { date: '11 月 20 日', days: 12 },
    ai: true, aiDemo: 'ok', aiGuardOpen: null, aiLogin: null, aiShown: null, aiReported: {},
    msgTab: 'msg', msgOpen: null, popup: null, popQueue: [],
    grpOpen: {}, faqOpen: {}, ctx: null,
    // 余额（01 §5.7）：这台电脑归到网站账户才有；null 是没归到
    balance: '128.00'
  };
  S.tickets = [
    { id: 'T-1032', title: '线路质量', st: 'wait', unread: true, msgs: [
      { who: '你', t: '昨天 21:14', x: '晚上发图片和视频很慢，线路 1 和线路 2 都试过。', imgs: [demoShot(1), demoShot(2)] },
      { who: '客服', t: '今天 09:30', x: '已查看你的安全检查结果和连接记录。晚上发图建议固定使用线路 2：在「线路」页点线路 2 即可固定。用一段时间后请回复是否改善。' }] },
    { id: 'T-0981', title: '局域网设备', st: 'done', rated: null, msgs: [
      { who: '你', t: '10 月 20 日 14:02', x: '打开软件后，家里的打印机找不到了，地址是 192.168.50.20。' },
      { who: '客服', t: '10 月 20 日 14:20', x: '192.168.50.20 在内置的局域网里，不用添加。打印不了是打印机换了地址，在系统设置里重新添加打印机就好。' },
      { who: '你', t: '10 月 20 日 14:31', x: '可以打印了。' }] },
    { id: 'T-1040', title: '无法上网', st: 'open', msgs: [
      { who: '你', t: '今天 08:50', x: '早上开机后一直显示断网，换了线路也不行。', diag: true }] },
    { id: 'T-0902', title: '账户续费流量', st: 'closed', msgs: [
      { who: '你', t: '10 月 12 日 19:02', x: '流量快用完了，怎么加？' },
      { who: '客服', t: '10 月 12 日 19:30', x: '在个人中心点「续费」，选「流量」，付款后提交就好。' }] }
  ];

  /* ---------------- 小工具 ---------------- */
  /* 英文、数字和中文之间留一个空格；值可能为空 */
  function lat(x) { x = x == null ? '' : String(x); return (/^[!-~]/.test(x) ? ' ' : '') + x + (/[!-~]$/.test(x) ? ' ' : ''); }
  /* 出口所在地后面带一个间隔点；还不知道出口所在地时是空 */
  function cityDot() { return TZN.city ? TZN.city + ' · ' : ''; }
  function tzWhere() { return TZN.city ? '你的出口在' + lat(TZN.city) + '（' + TZN.exit + '）' : '你的出口时区是' + lat(TZN.exit); }
  function esc(s) { return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function btn(kind, label, act, attrs) { if (!window.LEYRA_FEATURES.licenseCodes && (/switchCode|restoreCode|upgradeCode/.test(act) || /授权码/.test(label))) return ''; return '<button class="hx-btn hx-btn-' + kind + '" data-act="' + act + '"' + (attrs || '') + '>' + label + '</button>'; }
  function inline(text, done) { return '<div class="hx-inline" role="status">' + (done ? I('check', 16, 'c-ok') : '<span class="hx-spin" aria-hidden="true"></span>') + '<span>' + text + '</span></div>'; }
  function busy(key) { var b = S.busy[key]; return b ? inline(b.text, b.done) : ''; }
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function hhmm(d) { return pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function ymd(d) { return d.getFullYear() + '/' + pad(d.getMonth() + 1) + '/' + pad(d.getDate()); }
  /* Windows 11 标题栏按钮：10×10 画布，1px 描边 */
  function capIcon(k) {
    var p = { min: '<path d="M0.5 5h9"/>', max: '<rect x="0.5" y="0.5" width="9" height="9" rx="1.5"/>', close: '<path d="M0.5 0.5l9 9M9.5 0.5l-9 9"/>' }[k];
    return '<svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1" aria-hidden="true">' + p + '</svg>';
  }
  function copyBtn(text) { return '<button class="hx-btn hx-btn-text hx-btn-sm" data-hx-copy="' + text + '">' + I('copy', 16) + '<span>复制</span></button>'; }
  function channels() { return !CHANNELS.length ? '<p class="p-sub">还没有售后渠道，可以提交工单联系我们。</p>' : CHANNELS.map(function (c) { return '<div class="p-chan"><span>' + c[0] + '</span><span>' + c[1] + '</span>' + copyBtn(c[1]) + '</div>'; }).join(''); }
  function accountField() { return '<div class="hx-field"><span class="hx-label">账户编号</span><div class="hx-copyfield" data-ctx="copy:' + ACCOUNT + '"><input readonly value="' + ACCOUNT + '" aria-label="账户编号"><button data-hx-copy="' + ACCOUNT + '">' + I('copy', 16) + '<span>复制</span></button></div></div>'; }
  function fam() { return 'tech'; }
  function isDark() { return S.appearance === 'dark' || (S.appearance === 'system' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches); }
  function themeId() { return isDark() ? 'dark' : 'light'; }
  /* 线路名：有说明（稳定、适合发图……）才接「 · 说明」，外部节点的线路没有说明，只写「线路 N」。 */
  function lineName(r) { return '线路 ' + r.n + (r.tag ? ' · ' + r.tag : ''); }
  function routeName(i) { return lineName(ROUTES[i]); }

  /* 只在区域内容真正变化时淡入 */
  var lastKeys = {}, nextKeys = {};
  function fx(region, key) { nextKeys[region] = key; return lastKeys[region] === key ? '' : ' fx'; }

  /* 分阶段进行中：stages = [[文字, 秒, 是否结果]] */
  var runs = {};
  function run(key, stages, done) {
    var id = (runs[key] || 0) + 1; runs[key] = id; var i = 0;
    (function step() {
      if (runs[key] !== id) return;
      if (i >= stages.length) { delete S.busy[key]; if (done) done(); render(); return; }
      var s = stages[i++]; S.busy[key] = { text: s[0], done: !!s[2] }; render();
      setTimeout(step, (s[1] || 1.5) * 1000);
    })();
  }
  function cancelRun(key) { runs[key] = (runs[key] || 0) + 1; delete S.busy[key]; }

  var toastId = 0;
  function toast(text, icon) {
    var id = ++toastId; S.toast = { id: id, text: text, icon: icon || 'check' }; render();
    setTimeout(function () {
      if (!S.toast || S.toast.id !== id) return;
      var el = document.querySelector('[data-toast="' + id + '"]');
      if (el) el.style.opacity = '0';
      setTimeout(function () { if (S.toast && S.toast.id === id) { S.toast = null; render(); } }, 220);
    }, 2500);
  }

  /* ---------------- 状态切换 ---------------- */
  function clearTransient() { cancelRun('status'); cancelRun('resume'); cancelRun('tray'); S.starting = null; }
  function setStatus(st, reason) {
    clearTransient();
    S.status = st; if (reason) S.reason = reason;
    if (st !== 'paused') { S.pause = null; S.warnToast = null; }
    if (st === 'starting') S.starting = { stage: 0, t0: Date.now(), slow: false, hold: true };
    render();
  }
  function startStarting(opts) {
    clearTransient(); S.pause = null; S.warnToast = null;
    S.status = 'starting'; S.starting = { stage: 0, t0: Date.now(), slow: false, hold: !!(opts && opts.hold) };
    render();
    if (!S.starting.hold) finishStarting(1800);
  }
  function finishStarting(delay) {
    var st = S.starting; if (!st) return;
    setTimeout(function () {
      if (S.starting !== st) return; st.stage = 1; render();
      setTimeout(function () { if (S.starting !== st) return; S.starting = null; S.status = 'ok'; render(); }, 1600);
    }, delay || 0);
  }
  function startPause(min, login) {
    var from = { status: S.status === 'starting' ? 'ok' : S.status, reason: S.reason };
    clearTransient();
    var end = new Date(Date.now() + min * 60000);
    S.status = 'paused';
    S.pause = { left: min * 60, total: min * 60, kind: login ? 'wifi' : 'user', from: from, end: hhmm(end) };
    S.warnToast = '防护已暂停，你访问的网站能看到你的真实 IP';
    S.dialog = null; render();
  }
  function resume(auto) {
    if (S.status !== 'paused' || !S.pause) return;
    var from = S.pause.from;
    run('resume', [['正在恢复防护', 1.5]], function () {
      S.pause = null; S.warnToast = null;
      if (from && from.status === 'pm') { S.status = 'pm'; S.reason = from.reason; }
      else { S.status = 'ok'; }
    });
  }
  function lineConnected() {
    if (S.status === 'paused' && S.pause && S.pause.kind === 'wifi') {
      run('resume', [['正在恢复防护', 1.5]], function () { S.pause = null; S.warnToast = null; S.status = 'ok'; toast('线路已连上，防护已恢复'); });
    } else if (S.status === 'starting') { S.starting.hold = false; finishStarting(0); }
    else if (S.status === 'pm') { startStarting({ hold: false }); }
    else toast('当前状态无需模拟线路连接', 'info');
  }
  // 暂停弹窗里：正在运行的 Claude 程序，建议暂停前关闭（默认都勾上，点「关闭所选」）
  function claudeRunning(D) {
    var L = D.claude || []; if (!L.length) return '';
    var pick = D.pick || {};
    return '<div class="hx-card lx-claudeprocs"><b>以下 Claude 程序正在运行，建议暂停前关闭</b>' +
      L.map(function (g) { return '<label class="p-check"><input type="checkbox" data-act="claudePick:' + g.id + '"' + (pick[g.id] ? ' checked' : '') + '> ' + esc(g.name) + ' · ' + g.count + ' 个进程</label>'; }).join('') +
      '<div class="lx-cpacts">' + btn('secondary', D.closing ? '正在关闭…' : '关闭所选', 'claudeClose', D.closing || !L.some(function (g) { return pick[g.id]; }) ? ' disabled' : '') + '</div></div>';
  }
  function openPause(login) {
    S.tray = false;
    S.dialog = { type: 'pause', min: login ? 10 : 30, login: !!login };
    render();
  }

  /* ---------------- 安全检查引擎 ---------------- */
  function stagesFor(deep) { var s = STAGES.slice(); if (deep) s.splice(7, 0, ['正在观察 1 分钟内的网络连接', 60]); return s; }
  function startCheck(deep, ctx) {
    S.check = { phase: 'run', deep: !!deep, ctx: ctx || 'main', idx: 0, t: 0, fast: false, list: stagesFor(deep), sent: false };
    S.grpOpen = {};
    if ((ctx || 'main') === 'main') { S.screen = 'main'; S.page = 'check'; }
    render();
  }
  function stageDur(c, i) { return c.fast ? Math.min(c.list[i][1], 0.4) : c.list[i][1]; }
  function checkRemaining(c) { var r = Math.max(0, stageDur(c, c.idx) - c.t); for (var i = c.idx + 1; i < c.list.length; i++) r += stageDur(c, i); return Math.ceil(r); }
  function checkFrac(c) { var tot = 0, done = 0; for (var i = 0; i < c.list.length; i++) { var d = stageDur(c, i); tot += d; if (i < c.idx) done += d; } return Math.min(1, (done + Math.min(c.t, stageDur(c, c.idx))) / tot); }
  function fastForward() {
    var c = S.check; if (c.phase !== 'run') { toast('当前没有进行中的安全检查', 'info'); return; }
    c.fast = true; c.t = Math.min(c.t, 0.4); render();
  }
  function checkTick() {
    var c = S.check; if (c.phase !== 'run') return;
    var d = stageDur(c, c.idx);
    c.t += 0.2;
    if (c.t >= d && c.idx === c.list.length - 1 && window.CC_CHECK_PENDING && window.CC_CHECK_PENDING()) { c.t = d; patchProg(); return; }
    if (c.t >= d) {
      c.idx++; c.t = 0;
      if (c.idx >= c.list.length) {
        c.phase = 'closing'; render();
        setTimeout(function () { if (S.check === c) { c.phase = 'result'; render(); } }, 1500);
        return;
      }
      render(); return;
    }
    patchProg();
  }
  /* 浏览器实测（docs/01 §5.3）：在每个浏览器里打开一次本机测试页，看 WebRTC 暴露了什么。用户点了才查（会弹出浏览器窗口）。 */
  var BROWSER_TEXT = { clean: '正常：只看到线路出口', lan: '会让网站看到你的局域网地址，建议换 Chrome、Edge 或 Firefox', real: '暴露了真实 IP，先别用这个浏览器上网', none: '还没查过', unknown: '未取得完整结果，请检查线路后再试', testing: '正在检查，等浏览器窗口跑完…' };
  function browserSum() {
    var b = S.browsers, has = function (x) { return b.filter(function (i) { return i.st === x; }).map(function (i) { return i.n; }); };
    var real = has('real'), lan = has('lan'), none = has('none').length + has('unknown').length + has('testing').length;
    if (real.length) return { s: 'danger', d: real.join('、') + ' 暴露了真实 IP' };
    if (lan.length) return { s: 'warn', d: lan.join('、') + ' 会让网站看到你的局域网地址' };
    if (has('testing').length) return { s: 'skip', d: '正在浏览器中检查，等待实际结果回传' };
    if (has('unknown').length) return { s: 'skip', d: '浏览器检查未取得完整出口 IP，请检查线路后重试' };
    if (none === b.length) return { s: 'skip', d: '这次没查：要在浏览器里打开一个测试页' };
    return { s: 'ok', d: none ? '查过的正常，还有 ' + none + ' 个浏览器没查' : '正常' };
  }
  function checkItems() {
    if (window.CC_CHECK) return window.CC_CHECK(GROUPS);
    var c = S.check, wiz = c.ctx === 'wizard';
    return GROUPS.map(function (g) {
      var items = g[1].map(function (n) {
        var it = { n: n, s: 'ok', d: '正常' };
        if (!wiz && n === '浏览器实测') { var bw = browserSum(); it.s = bw.s; it.d = bw.d; it.act = ['检查浏览器', 'dialog:browsers']; }
        if (!wiz && n === '系统时间' && !S.timeSynced) { it.s = 'warn'; it.d = '本机时间慢 3 分钟，建议同步'; it.act = ['同步时间', 'syncTime']; }
        if (!wiz && n === '其他网络软件' && S.alerts.conflict && !S.conflictFixed) {
          if (S.busy.quit) { it.s = 'danger'; it.d = '已请求 ' + CONFL.name + ' 退出'; }
          else { it.s = 'danger'; it.d = CONFL.msg; it.act = CONFL.kind === 'stale' ? ['帮我关掉', 'proxyOff'] : CONFL.kind === 'unknown' ? ['发给客服', 'conflictTicket'] : ['帮我退出', 'quitApp']; }
        }
        return it;
      });
      var rank = { danger: 0, warn: 1, ok: 2, skip: 3 };
      items.sort(function (a, b) { return rank[a.s] - rank[b.s]; });
      return { n: g[0], items: items };
    });
  }
  function WK() { return counts(checkItems()); }
  function wkTotal(k) { return k.ok + k.warn + k.danger + k.skip; }
  /* 有问题的项（先需要处理、再提示），最多四条 */
  function badItems() { var out = []; checkItems().forEach(function (g) { g.items.forEach(function (it) { if (it.s === 'warn' || it.s === 'danger') out.push(it); }); }); out.sort(function (a, b) { return (a.s === 'danger' ? 0 : 1) - (b.s === 'danger' ? 0 : 1); }); return out.slice(0, 4); }
  function counts(groups) { var r = { ok: 0, warn: 0, danger: 0, skip: 0 }; groups.forEach(function (g) { g.items.forEach(function (i) { r[i.s]++; }); }); return r; }

  /* ---------------- 视图：状态图形区 ---------------- */
  /* 概览顶上的连接路径：这台电脑 → 独享线路 → 线路那头（写地区）→ 网站看到的 IP，状态都画在路上。
     光点动画用负的 animation-delay 接上当前时间，界面每次刷新不会从头开始。 */
  /* 概览状态区下面的数据格（01 §4 第 6 条，2026-11-08 改成 B「深色仪表」）：已保护时一排四格——实时网速（最宽，带最近 1 分钟的曲线）、
     出口（点了去线路页）、网站看到的 IP（点一下复制）、延迟；别的状态不放数据格，说明和按钮就够了。 */
  var RATE_KEEP = 30;
  function rateTick() {
    if (!S.rate) return;
    var h = S.rateHist, now = Date.now();
    if (h.length && now - h[h.length - 1].t < 1500) return;
    h.push({ t: now, d: S.rate.down, u: S.rate.up });
    while (h.length > RATE_KEEP) h.shift();
  }
  function spark() {
    var h = S.rateHist;
    if (h.length < 2) return '';
    var W = 200, Hh = 26, top = 1;
    h.forEach(function (p) { top = Math.max(top, p.d, p.u); });
    var x = function (i) { return (W - (h.length - 1 - i) * (W / (RATE_KEEP - 1))).toFixed(1); };
    var y = function (v) { return (Hh - 2 - v / top * (Hh - 5)).toFixed(1); };
    var line = function (k) { return h.map(function (p, i) { return x(i) + ',' + y(p[k]); }).join(' '); };
    var area = 'M' + x(0) + ',' + Hh + ' L' + line('d').replace(/ /g, ' L') + ' L' + x(h.length - 1) + ',' + Hh + ' Z';
    return '<svg class="p-spark" viewBox="0 0 ' + W + ' ' + Hh + '" preserveAspectRatio="none" aria-hidden="true"><path class="ar" d="' + area + '"></path>' +
      '<polyline class="dn" points="' + line('d') + '"></polyline><polyline class="up" points="' + line('u') + '"></polyline></svg>';
  }
  function heroCells() {
    var r = ROUTES[S.route] || {}, line = r.n != null && r.n !== '-' ? '线路 ' + r.n : '独享线路';
    var cell = function (k, v, s, cls, tag) { tag = tag || 'div'; return '<' + tag + ' class="p-cell' + (cls || '') + '"><span class="k">' + k + '</span><span class="v">' + v + '</span>' + (s ? '<span class="s">' + s + '</span>' : '') + '</' + tag.split(' ')[0] + '>'; };
    var speed = '<div class="p-cell speed"><span class="k">实时网速<span class="live">最近 1 分钟</span></span>' +
      (S.rate ? '<span class="v hx-num"><b class="dn">↓ ' + fmtRate(S.rate.down) + '</b><b class="up">↑ ' + fmtRate(S.rate.up) + '</b></span>' + spark() : '<span class="v">—</span>') + '</div>';
    return '<div class="p-cells">' + speed +
      cell('出口', curExit() ? esc(curExit().name) : TZN.city || '独享线路', (curExit() ? curExit().city + ' · ' : '') + line + (r.tag ? ' · ' + r.tag : ''), ' go', 'button type="button" data-act="go:routes"') +
      '<div class="p-cell"><span class="k">网站看到</span><button class="v p-ipcopy hx-num" data-hx-copy="' + IP + '" data-ctx="copy:' + IP + '" title="点一下复制" aria-label="出口 IP ' + IP + '，点一下复制">' + IP + '</button><span class="s">点一下复制</span></div>' +
      cell('延迟', typeof r.ms === 'number' ? '<span class="hx-num">' + r.ms + ' ms</span>' : '—', r.ok != null ? '成功率 ' + r.ok + '%' : '') + '</div>';
  }
  function statusKey() { return S.status + ':' + (S.status === 'pm' ? S.reason : S.status === 'paused' ? S.pause.kind : S.status === 'prep' ? S.prepPaused : ''); }
  function statusView() {
    var st = S.status, f = fam(), g, title, body = '', acts = '', a = ' fx-sg';
    var gk = fx('sg', statusKey());
    if (st === 'ok') {
      g = H.statusGraphic('ok', f); title = '已保护';
      // 防护记录（01 §5.3）：这次连续保护的时长和挡下的直连次数；守护程序没给就不显示。
      body = (S.listed ? '<p>Claude 经' + (byo() ? '你的出口' : '独享线路') + '访问，Claude 看不到你的真实 IP。其他软件照常直连，或走你自己的 VPN。</p>' : '<p>真实 IP 已隐藏，所有流量经' + (byo() ? '你的出口' : '独享线路') + '传输。</p>') + (S.guard ? '<span class="p-sub p-guard">已连续保护 ' + S.guard.dur + (S.guard.blocked != null ? ' · 挡下 ' + S.guard.blocked + ' 次直连' : '') + '</span>' : '');
      acts = S.busy.resume ? '' : btn('danger', I('lock', 14) + '暂停防护', 'pause') + btn('text', '安全检查', 'go:check');
    } else if (st === 'starting') {
      g = H.statusGraphic('neutral', f); title = S.starting.switching ? '正在切换出口' : '启动中';
      body = S.starting.switching ? '<p>当前出口连不上，正在切换到你添加的下一个出口。切换期间网络先断开，真实 IP 不会暴露。</p>' : inline(S.starting.stage === 0 ? '正在建立独享线路' : '正在校验线路完整性');
      if (S.starting.slow) body += '<p class="hint">在酒店、机场等需要网页登录的 Wi-Fi 上？可先暂停防护完成登录，线路连上后自动恢复</p>';
      acts = btn('danger', I('lock', 14) + '暂停防护', 'pauseLogin');
    } else if (st === 'pm') {
      var R = PM[S.reason], renewing = S.reason === 'traffic' && !!S.renew.phase;
      g = H.statusGraphic('danger', f); title = '已断网';
      if (S.reason === 'checking') body = '<p>网络已断开。</p>' + inline('正在确认原因');
      else {
        body = '<p>' + (renewing ? '网络已断开。续费申请已提交，开通后自动恢复' : R.desc) + '</p>' + (S.holding ? '<p class="hint">只有 Claude 暂时断开（真实 IP 不会给 Claude 看到），其他软件照常上网。</p>' : '') + (R.desc2 ? '<p class="hint">' + R.desc2 + '</p>' : '');
      }
      body += busy('status');
      acts = (S.busy.status ? '' : (renewing ? [['primary', '查看进度', 'renewView']] : (R.btns || [])).map(function (b) { return btn(b[0], b[1], b[2]); }).join('')) + (R.noPause ? '' : btn('danger', I('lock', 14) + (R.pause || '暂停防护'), R.pause ? 'pauseLogin' : 'pause'));
    } else if (st === 'down') {
      g = H.statusGraphic('danger', f); title = '防护没有运行';
      body = '<p>防护组件停了，现在不能确认真实 IP 是否隐藏。</p><p class="hint">点「恢复防护」重新启动，需要确认一次管理员权限；重启电脑也会恢复。</p>' + busy('daemon');
      acts = S.busy.daemon ? '' : btn('primary', '恢复防护', 'daemonStart') + btn('text', '联系售后', 'contact');
    } else if (st === 'paused' && S.pause && S.pause.kind === 'off') {
      g = H.statusGraphic('warn', f); title = '防护未开启';
      body = '<p>点「开启防护」后，所有流量走独享线路，网站看不到你的真实 IP。开启前网站能看到你的真实 IP。</p>';
      acts = S.busy.resume ? '' : btn('primary', I('shield', 14) + '开启防护', 'resume');
    } else if (st === 'paused') {
      var P = S.pause;
      g = H.statusGraphic('warn', f, { left: P.left, total: P.total, center: '<span class="hx-num-lg" style="color:var(--text);font-size: 28px;line-height: 36px" data-mmss>' + H.mmss(P.left) + '</span>' });
      title = '防护已暂停';
      body = '<p>暂停期间不走独享线路，你访问的网站能看到你的真实 IP。到 ' + P.end + ' 自动恢复，也可以随时点「恢复防护」。' + (P.kind === 'wifi' ? '线路连上后自动恢复防护。' : '') + '</p>';
      acts = S.busy.resume ? '' : btn('primary', '恢复防护', 'resume');
    } else if (S.ownExit === 'waiting') {
      // 自带出口还没接：不用等开通，贴上卖家给的链接或订阅地址就能用（01 §4 第 4 步）
      g = H.statusGraphic('neutral', f); title = '接入你的出口';
      body = '<p>这台电脑是「单独软件 · 自带出口」：贴上卖家给你的链接或订阅地址，接上就开启防护。接上之前还没有防护，网站能看到你的真实 IP，请先不要访问境外网站。</p>';
      acts = btn('primary', '接入你的出口', 'dialog:ownexit') + btn('text', '联系售后', 'contact');
    } else {
      // 线路开通中（01 §4 第 3 步）：写激活时间、一般多久、开通后会怎样；「开通前先断网」在这里也能开关（和设置里是同一个）
      g = H.statusGraphic('neutral', f); title = '准备中';
      body = '<p>' + (S.prepPaused ? '线路开通中。按你的设置已先断网，只能访问局域网。' : '线路开通中。开通前还没有防护，网站能看到你的真实 IP，请先不要访问境外网站。') + '</p>' +
        '<p class="hint">' + (S.prepAt ? S.prepAt + ' 激活。' : '') + '一般几小时内开通，夜里可能要到第二天上午。开通后自动连上，会弹提醒告诉你。</p>' +
        '<div class="p-prepsw"><span>开通前先断网<small>开通前只能访问局域网；关掉要确认是你本人</small></span>' + sw(S.prepPaused, 'prepPaused', '开通前先断网') + '</div>';
      acts = btn('text', '联系售后', 'contact');
    }
    body += busy('resume');
    // 概览顶上（01 §4 第 6 条，2026-11-08 改成 B「深色仪表」）：左边大状态环，右边标题、说明和按钮，已保护时下面一排数据格。
    var tone = st === 'ok' ? 'ok' : st === 'pm' || st === 'down' ? 'danger' : st === 'paused' ? 'warn' : 'neutral';
    if (st === 'pm') title += '<span class="p-pill ok">' + I('check', 14) + '真实 IP 没有暴露</span>';
    if (st === 'ok') rateTick();
    return '<section class="p-status p-hero ' + tone + '" aria-live="polite"><div class="p-ring' + gk + '">' + g + '</div><div class="p-heromain"><div class="p-herotop"><div class="txt"><h2>' + title + '</h2>' + body + '</div>' +
      (acts ? '<div class="acts">' + acts + '</div>' : '') + '</div>' + (st === 'ok' ? heroCells() : '') + '</div></section>';
  }
  /* 售后 QQ 群（01 §6.4，2026-11-08 定）：概览只放 QQ 群这一个渠道——后台售后渠道里类型是 QQ 群号的第一条；没配 QQ 群时退回第一条渠道。
     其它渠道在帮助页、断网页的「联系售后」里照常有。 */
  function qqChan() { return CHANNELS.filter(function (c) { return c[2] === 'qqgroup'; })[0] || CHANNELS[0] || null; }
  function qqBlock(short) {
    var c = qqChan();
    if (!c) return '<p class="p-sub">可以提交工单联系我们。</p>';
    var grp = c[2] === 'qqgroup', what = grp ? '售后 QQ 群' : c[3] || c[0];
    // short：卡片标题已经写了「售后 QQ 群」，这里只写群名和备注一行
    var who = short ? '<span class="nm">' + [grp ? c[3] : '', c[4]].filter(Boolean).join(' · ') + '</span>' : '<span class="k">' + what + (c[4] ? ' · ' + c[4] : '') + '</span>' + (grp && c[3] ? '<span class="nm">' + c[3] + '</span>' : '');
    return '<div class="p-qq"><div class="who">' + who + '</div>' +
      '<div class="no"><b class="hx-num" data-ctx="copy:' + c[1] + '">' + c[1] + '</b><button class="hx-btn hx-btn-secondary hx-btn-sm" data-hx-copy="' + c[1] + '">' + I('copy', 14) + (grp ? '复制群号' : '复制') + '</button></div></div>';
  }
  function qqCard() {
    var c = qqChan(); if (!c) return '';
    // 复制按钮放在标题行右边，群号独占一行，窄卡片里也完整显示。
    return '<div class="hx-card hx-data lx-qqcard"><div class="lab">' + I('chat', 16) + (c[2] === 'qqgroup' ? '售后 QQ 群' : esc(c[3] || '售后')) +
      '<button class="hx-btn hx-btn-text hx-btn-sm lx-qqcopy" data-hx-copy="' + c[1] + '" aria-label="复制群号 ' + c[1] + '">' + I('copy', 14) + '复制群号</button></div>' +
      '<div class="lx-qqrow"><b class="val hx-num" data-ctx="copy:' + c[1] + '">' + c[1] + '</b></div>' +
      '<div class="sub">' + [c[3], c[4]].filter(Boolean).join(' · ') + '</div></div>';
  }
  function supportCard(prep) {
    // 断网 / 准备中的概览里放在信息卡旁边：售后 QQ 群、账户编号、两个入口，和旁边两行卡一样高
    return '<div class="hx-card p-support"><div class="p-cardhead" style="margin:0"><h2>售后</h2></div>' +
      qqBlock() +
      (prep ? '' : '<div class="p-linkrow">' + btn('secondary', I('chat', 16) + '提交工单', 'newTicket') + btn('text', '连不上？输入接入码', 'dialog:access') + '</div>') + '</div>';
  }

  /* ---------------- 续费 ---------------- */
  var RENEW_ID = 'T-1052', RENEW_AT = '14:02'; /* 演示条直接切到「续费处理中」时使用的固定时间 */
  function baseLeft() { return S.exp.days; }
  /* 还在用自带出口（没升级）：永久使用、不续费，有「升级到全套」 */
  function byo() { return S.ownExit === 'waiting' || S.ownExit === 'connected'; }
  function suiteWord() { return byo() ? '授权' : window.CC_SUITE_WORD ? window.CC_SUITE_WORD() : '全套'; }
  /* Pro 全套升到 Max 5x：服务端说能升（suite.can_tier_up）才给，一键登录官网补差价，到期日不变 */
  function tierUpBtn() { return S.tierUp ? btn('text', '升到 Max 5x', 'tierUp') : ''; }
  /* 免费试用（01 §3.1「免费试用」、§4 第 8 步）：只试单独软件（接自己的出口），没有「升级到全套」。
     S.trial：'' 不是试用 / on 试用中 / expired 到期断网（还在保护时到期）/ stopped 停用（退出过或重启后再打开：不接管网络，只显示到期页） */
  function trialLeft() { return '还剩 ' + S.trialEnd.days + ' 天'; }
  function trialStoppedScreen() {
    return '<div class="p-bgperm"><div class="ic" style="background:var(--border);color:var(--text-2)">' + I('calendar', 32) + '</div>' +
      '<div><h1>免费试用已到期</h1><p>现在没有保护，电脑照常上网。买了单独软件再打开就能用，自己的出口和设置都留着；已经买好授权码的点「换授权码」填上。</p></div>' +
      '<div class="hx-row" style="justify-content:center">' + btn('primary', '购买单独软件', 'buySoft') + btn('secondary', '换授权码', 'dialog:switchCode') + btn('secondary', '退出', 'trialExit') + '</div></div>';
  }
  function trialDemo(kind) {
    S.dialog = null; S.tray = false; S.ai = false; S.aiPrep = ''; S.alerts.expire1d = false; if ('expireSoon' in S.alerts) S.alerts.expireSoon = false;
    S.screen = 'main'; S.ownExit = 'connected';
    if (kind === 'on12') { S.trial = 'on'; S.trialEnd = { date: '11 月 20 日', days: 12 }; S.page = 'profile'; setStatus('ok'); }
    else if (kind === 'on3') { S.trial = 'on'; S.trialEnd = { date: '11 月 11 日', days: 3 }; S.page = 'overview'; setStatus('ok'); }
    else if (kind === 'on1') { S.trial = 'on'; S.trialEnd = { date: '11 月 9 日', days: 1 }; S.page = 'overview'; setStatus('ok'); NOTES.trial1d = { title: '免费试用明天到期', body: '买了单独软件就能一直用', btns: [['secondary', '购买', 'buySoft']] }; notify('trial1d'); render(); }
    else if (kind === 'expired') { S.trial = 'expired'; S.trialEnd = { date: '11 月 8 日', days: 0 }; S.page = 'overview'; setStatus('pm', 'trialExpired'); }
    else if (kind === 'stopped') { S.trial = 'stopped'; S.trialEnd = { date: '11 月 8 日', days: 0 }; S.screen = 'trialStopped'; render(); }
    else { S.trial = ''; S.page = 'overview'; startStarting({ hold: false }); toast('已转成单独软件，永久使用，正在恢复防护'); }
  }
  /* 单独软件（自带出口）流量不限（01 §5.2），本月用量照样显示 */
  function unlimited() { return byo() || INFO.limit === '不限'; }
  function nodeDate() { return S.exp.date; }
  function nodeLeft() { return baseLeft() + (S.renewDone.node ? 30 : 0); }
  function nodeExpired() { return S.status === 'pm' && S.reason === 'expired'; }
  /* 续费的选项（docs/01 §3.1、§3.2）：只有「全套」（线路和 Claude 账号一起，按月顺延，不能分开续）和「流量」。
     每项是 [键, 名字, 交给守护程序的「续什么」]；续几个月看付款的订单，客服按订单改到期。 */
  function renewOpts() {
    if (window.CC_RENEW_OPTS) return window.CC_RENEW_OPTS();
    return [['all', '全套', 'node'], ['traffic', '流量', 'traffic']];
  }
  function renewFirst() { var pk = {}; pk[renewOpts()[0][0]] = true; return pk; }
  function renewDur(pick) { return !!pick.all; }
  function renewNames(pick) { return renewOpts().filter(function (o) { return pick[o[0]]; }).map(function (o) { return o[1]; }); }
  /* 续费先在网页上自助（余额或在线付款，付完自动顺延）；线下付过钱的走「已经线下付了钱？」提交申请（原来的续费面板） */
  function offlinePaid() { return S.renew.phase ? '' : ' · ' + btn('text', '已经线下付了钱？', 'renew', ' style="padding:0;min-height:0;height:auto"'); }
  function renewBtn() { return S.renew.phase ? btn('text', '查看进度', 'renewView') : btn('text', '续费', 'renewWeb'); }
  function renewTicket(pick, note, at) {
    S.tickets = S.tickets.filter(function (t) { return t.id !== RENEW_ID; });
    S.tickets.unshift({ id: RENEW_ID, title: '续费', st: 'open', msgs: [{ who: '你', t: '今天 ' + at, x: '续费申请：' + renewNames(pick).join('、') + '。' + (note ? '订单号或备注：' + note : '已付款，未填写订单号或备注。') }] });
  }
  function ensureRenew() {
    if (S.renew.ticket) return;
    var pick = renewFirst();
    S.renew = { phase: 'processing', at: RENEW_AT, pick: pick, ticket: RENEW_ID };
    renewTicket(pick, '', RENEW_AT);
  }
  function renewTicketObj() { return S.tickets.filter(function (t) { return t.id === RENEW_ID; })[0]; }

  /* ---------------- 页面：概览 ---------------- */
  /* 顶部提示一次只显示一条，按优先级：冲突软件 > 升级进行中 > 到期前 1 天 > 新版本 > 公告 */
  // 格子里只写渠道名（c[3]），备注（服务时间等，c[4]）放悬停提示：名称加备注太长时会挤到旁边的格子上
  function chanGrid() { return '<div class="p-changrid">' + CHANNELS.map(function (c) { return '<div title="' + c[0] + '"><span>' + (c[3] || c[0]) + '</span><b>' + c[1] + '</b><button class="hx-icon-btn" data-hx-copy="' + c[1] + '" aria-label="复制' + c[0] + '渠道">' + I('copy', 16) + '<span class="p-sronly">复制</span></button></div>'; }).join('') + '</div>'; }
  function bannerView() {
    var k = null;
    if (S.alerts.conflict && !S.conflictFixed) k = 'conflict';
    else if (S.proxyUndo) k = 'proxyUndo';
    else if (S.busy.update) k = 'update';
    else if (S.renew.phase === 'needinfo') k = 'renewInfo';
    else if (S.renew.phase === 'processing' && !(S.status === 'pm' && S.reason === 'traffic')) k = 'renewWait';
    else if (S.alerts.expire1d && (!byo() || S.licensePermanent === false)) k = 'expire1d';
    else if (S.alerts.expireSoon && !S.renewDone.node && !S.renew.phase && !nodeExpired() && (!byo() || S.licensePermanent === false)) k = 'renewSoon';
    else if (S.trial === 'on' && S.trialEnd.days <= 3) k = 'trial';
    else if (S.alerts.update) k = 'update';
    else if (S.banner) k = 'banner';
    if (!k) { fx('banner', ''); return ''; }
    var f = fx('banner', k + (k === 'banner' ? S.banner : ''));
    if (k === 'proxyUndo') return '<div class="p-banner' + f + '"><div class="hx-alert info" role="status" style="align-items:center">' + I('check', 20, 'c-ok') + '<span class="msg">已关掉系统代理，防护照常</span>' + btn('secondary', '撤销', 'proxyUndo') + btn('text', '知道了', 'dismiss:proxyUndo') + '</div></div>';
    if (k === 'conflict') {
      // 查出了是谁：帮我退出；系统代理没关：帮我关掉；查不出：发给客服（预填好证据）
      var cb = CONFL.kind === 'stale' ? btn('secondary', '帮我关掉', 'proxyOff') : CONFL.kind === 'unknown' ? btn('secondary', '发给客服', 'conflictTicket') : btn('secondary', '帮我退出', 'quitApp');
      return '<div class="p-banner' + f + '"><div class="hx-alert danger" role="alert">' + I('warning', 20) + '<span class="msg">' + CONFL.msg + '</span>' + (S.busy.quit ? '' : cb) + '</div>' + (S.busy.quit ? '<div style="padding:4px 14px">' + busy('quit') + '</div>' : '') + '</div>';
    }
    if (k === 'renewSoon') return '<div class="p-banner' + f + '"><div class="hx-alert warn" role="status" style="align-items:center">' + I('calendar', 20) + '<span class="msg">' + suiteWord() + ' ' + nodeDate() + '到期</span>' + btn('primary', '续费', 'renewWeb') + '</div></div>';
    if (k === 'renewWait') return '<div class="p-banner' + f + '"><div class="hx-alert info" role="status" style="align-items:center">' + I('clock', 20) + '<span class="msg">续费申请已提交，开通后自动生效</span>' + btn('secondary', '查看进度', 'renewView') + '</div></div>';
    if (k === 'renewInfo') return '<div class="p-banner' + f + '"><div class="hx-alert warn" role="status" style="align-items:center">' + I('info', 20) + '<span class="msg">续费申请需要补充信息</span>' + btn('primary', '查看', 'renewView') + '</div></div>';
    if (k === 'expire1d') return '<div class="p-alertbox p-banner' + f + '" role="status"><div class="top">' + I('warning', 20) + '<span class="msg">' + suiteWord() + '明天到期</span>' + btn('primary', '续费', 'renewWeb') + btn('text', '知道了', 'dismiss:expire1d') + '</div>' + qqBlock() + '</div>';
    if (k === 'trial') return '<div class="p-banner' + f + '"><div class="hx-alert warn" role="status" style="align-items:center">' + I('calendar', 20) + '<span class="msg">' + (S.trialEnd.days <= 1 ? '免费试用明天到期' : '免费试用' + trialLeft()) + '</span>' + btn('secondary', '换授权码', 'dialog:switchCode') + btn('primary', '购买', 'buySoft') + '</div></div>';
    if (k === 'update') return '<div class="p-banner' + f + '"><div class="hx-alert info" role="status" style="align-items:center">' + I('download', 20) + '<span class="msg">新版本 ' + S.newVersion + ' 已就绪，升级约需 10 秒，期间网络暂停</span>' + (S.busy.update ? '' : btn('primary', '立即更新', 'updateNow') + btn('secondary', '下次开机时更新', 'updateLater')) + '</div>' + (S.busy.update ? '<div style="padding:4px 14px">' + busy('update') + '</div>' : '') + '</div>';
    var B = (window.CC_BANNER && window.CC_BANNER(S.banner)) || { normal: ['info', '', '线路 2 已优化', '上传图片和视频的速度提升，无需操作。', 'N-11'], important: ['warning', ' important', '售后渠道已更新', '请在帮助页查看最新联系方式', 'N-11'], maint: ['clock', ' important', '11 月 3 日 02:00–03:00 服务维护', '维护期间网络断开，结束后自动恢复。', 'N-12'] }[S.banner];
    return '<div class="hx-announce' + B[1] + ' p-banner' + f + '" role="status">' + I(B[0], 20, B[1] ? 'c-warn' : 'hx-muted') + '<div class="txt"><b>' + B[2] + '</b><span>' + B[3] + '</span></div>' + (B[4] ? btn('text', '查看', 'annShow:' + B[4]) : '') + btn('text', '知道了', 'dismiss:banner') + '</div>';
  }
  function overview() {
    var st = S.status, r = ROUTES[S.route];
    var full = st === 'pm' && S.reason === 'traffic';
    var tr = unlimited() ? '' : full ? '<div class="hx-bar danger" role="progressbar" aria-label="本月流量" aria-valuenow="100" aria-valuemin="0" aria-valuemax="100"><i style="width:100%"></i></div>' : '<div class="hx-bar" role="progressbar" aria-label="本月流量" aria-valuenow="' + Math.round(INFO.pct) + '" aria-valuemin="0" aria-valuemax="100"><i style="width:' + INFO.pct + '%"></i></div>';
    // 保护项：已断网时线路那一点红（防护规则没装上、防护组件未就绪时规则那一点也红），其余照常绿
    var dotOf = function (n) {
      if (st === 'down') return 'danger';
      if (st === 'paused') return 'warn';
      if (st === 'prep' || st === 'starting') return 'neutral';
      if (st === 'pm' && (n === '独享线路' || (n === '防护规则' && (S.reason === 'rules' || S.reason === 'component')))) return 'danger';
      return '';
    };
    var dotNote = st === 'down' ? '恢复防护后继续检查' : st === 'paused' ? '暂停中' : st === 'prep' ? (S.ownExit === 'waiting' ? '接上出口后生效' : '线路开通后生效') : st === 'starting' ? '线路建立后生效' : S.rate ? '' : '每 60 秒自动检查';
    var exp = st === 'pm' && S.reason === 'expired';
    var bn = bannerView();
    var dots = '<div class="p-dots" role="list" aria-label="保护项">' + ['防护规则', '网址解析', '独享线路', '浏览器'].map(function (n) { return '<span class="hx-dotitem" role="listitem"><span class="hx-dot ' + dotOf(n) + '" aria-hidden="true"></span>' + n + '</span>'; }).join('') + '<span class="note">' + dotNote + '</span></div>';
    return bn + statusView().replace('class="p-status"', 'class="p-status' + (bn ? ' compact' : '') + '"').replace('</div></section>', dots + '</div></section>') +
      overviewCards(st, r, full, tr, exp);
  }
  /* 三张信息卡（出口 IP 只在路径图里写一次）和最近动态：已保护时三张一排、最近动态在下面；
     断网 / 准备中 / 防护没有运行时和售后卡并排、两行两张（窄窗口时上下排） */
  function overviewCards(st, r, full, tr, exp) {
    var side = st === 'pm' || st === 'prep' || st === 'down';
    var cRoute =
      '<button class="hx-card hx-data p-cardbtn" data-act="go:routes"><div class="lab">' + I('signal', 16) + '当前线路' + I('chevron', 16, 'go') + '</div><div class="val txt">' + (st === 'pm' ? '未连接' : r.n === '-' ? (st === 'down' ? '—' : '还没有线路') : '线路 ' + r.n) + '</div><div class="sub">' + (st === 'pm' ? '网络已断开' : r.n === '-' ? (st === 'down' ? '恢复防护后显示' : '开通后自动接入') : [r.tag, typeof r.ms === 'number' ? r.ms + ' ms' : ''].filter(Boolean).join(' · ')) + '</div></button>';
    var cTraffic = '<div class="hx-card hx-data"><div class="lab">' + I('download', 16) + '本月流量</div><div class="val">' + trafficVal(full) + '</div>' + tr + '<div class="sub"' + (full ? ' style="color:var(--danger)"' : '') + '>' + (full ? '已用完 · ' + INFO.reset + '重置' : '下次重置 ' + INFO.reset) + '</div></div>';
    var cAccount = '<button class="hx-card hx-data p-cardbtn" data-act="go:profile"><div class="lab">' + I('calendar', 16) + '账户' + I('chevron', 16, 'go') + '</div><div class="val txt">' + (exp ? '已到期' : S.trial ? (S.trial === 'on' ? trialLeft() : '已到期') : byo() && S.licensePermanent === true ? '永久使用' : S.exp.date === '—' ? '—' : '还有 ' + nodeLeft() + ' 天') + '</div><div class="sub">' + (exp ? suiteWord() + ' ' + S.exp.date + '已到期' : S.trial ? '免费试用 · ' + S.trialEnd.date + (S.trial === 'on' ? '到期' : '') : byo() && S.licensePermanent === true ? '一次买断 · 永久授权' : S.exp.date === '—' ? '到期日还没拿到' : suiteWord() + ' ' + nodeDate() + '到期') + '</div></button>';
    // 本月流量是服务器在转发时实际数的（经出口的上下行合计，按自然月清零），自己加的 VPS 出口也算。
    if (side) return '<div class="p-sec p-pmgrid">' + supportCard(st !== 'pm') + '<div class="p-datagrid two">' + cRoute + cTraffic + cAccount + activityCard(2, true) + '</div></div>';
    // 已保护、暂停、启动中：第一排当前线路、本月流量、账户；第二排最近动态（宽）和售后 QQ 群（窄，群号完整显示）。窄窗口上下排。
    var qq = qqCard();
    return '<div class="p-sec"><div class="p-datagrid three">' + cRoute + cTraffic + cAccount + '</div></div>' +
      '<div class="p-sec' + (qq ? ' p-ovwrow' : '') + '">' + activityCard(3) + qq + '</div>';
  }
  /* 最近动态（01 §5.13）：最近几条，「全部」跳线路页的最近事件；矮窗口里只显示 2 条 */
  function activityCard(n, compact) {
    var E = S.hist.events.slice(0, n);
    return '<div class="hx-card p-activity' + (compact ? ' compact' : '') + '"><div class="p-cardhead"><h2>最近动态</h2>' + btn('text', '全部', 'go:routes') + '</div>' +
      (E.length ? '<ul class="p-events">' + E.map(function (e) { return '<li><time>' + e.time + '</time><span>' + e.text + '</span></li>'; }).join('') + '</ul>'
        : '<p class="p-sub">还没有动态，切换线路、断网、检查时会记在这里</p>') + '</div>';
  }
  /* 流量卡的大数字：「38.2 / 200 GB」小字写上限；不限流量时只有已用 */
  function trafficVal(full) {
    var u = full ? INFO.limit : INFO.used;
    var m = /^([\d.]+) ([KMG]B)$/.exec(INFO.used);
    if (unlimited()) return m ? m[1] + '<small>' + m[2] + ' · 不限</small>' : INFO.used + '<small>不限</small>';
    if (/ GB$/.test(INFO.limit)) return u.replace(/ GB$/, '') + '<small>/ ' + INFO.limit + '</small>';
    return u + (INFO.limit === '不限' ? '<small>不限流量</small>' : '');
  }

  /* ---------------- 页面：线路 ---------------- */
  function sig(n) { var s = ''; for (var i = 0; i < 4; i++) s += '<i class="' + (i < n ? 'on' : '') + '"></i>'; return '<span class="hx-sig' + (n <= 2 ? ' warn' : '') + '" role="img" aria-label="信号 ' + n + ' 格，共 4 格">' + s + '</span>'; }
  /* 出口一栏（01 §5.2「多个出口」）：老套餐是我们分配的加自己加的，单独软件、免费试用只有自己加的 */
  function curExit() { return S.exits ? exitsShown()[S.exit] : null; }
  function exitsShown() { return S.exits === 'soft' ? EXITS.filter(function (e) { return e.own; }) : EXITS; }
  // 自动换过出口、原来那个又能用了：提示一下，点一下切回（不自动切回，免得 IP 来回变）。
  function recoveredNote(L, b) {
    var F = S.failover; if (!F) return '';
    var i = -1; L.forEach(function (e, k) { if (i < 0 && e.name === F.from && k !== S.exit && e.healthy) i = k; });
    return i < 0 ? '' : '<div class="hx-alert info lx-recovered" role="status">' + I('info', 20) + '<span class="msg">「' + esc(F.from) + '」恢复了。刚才它连不上，已自动换到「' + esc(F.to) + '」。</span>' + btn('secondary', '切回「' + esc(F.from) + '」', 'pickExit:' + i, b ? ' disabled' : '') + '</div>';
  }
  function exitsSec(b) {
    var L = exitsShown(), own = L.filter(function (e) { return e.own; }).length;
    return '<div class="p-sec"><div class="p-cardhead"><h2>出口</h2><span class="p-sub">点哪个用哪个，切换后出口 IP 跟着变，防护不断</span></div>' +
      recoveredNote(L, b) +
      '<div class="p-exits" role="radiogroup" aria-label="出口">' + L.map(function (e, i) {
        var cur = i === S.exit, last = S.exits === 'soft' && own <= 1;
        return '<div class="p-exit" aria-current="' + cur + '"><button class="pick" role="radio" aria-checked="' + cur + '" data-act="pickExit:' + i + '"' + (b ? ' disabled' : '') + '><span class="rad" aria-hidden="true"></span><span class="t">' +
          '<b>' + esc(e.name) + (cur ? ' <span class="hx-mark">当前</span>' : '') + '</b>' + (e.err ? '<small style="color:var(--danger)" title="服务器经这个出口访问网站没成功">' + I('warning', 14) + ' ' + e.err + '</small>' : '<small>' + e.ip + '</small>') +
          '<small>' + e.city + ' · ' + e.n + ' 条线路</small></span>' + (e.sig == null ? '' : sig(e.sig)) + '</button>' +
          // 自己加的出口：标签写走法（经香港中转 / 本机直连），可以一键换；服务器连不通它时「改本机直连」用醒目颜色
          '<div class="foot"><span class="tag' + (e.own ? ' own' : '') + '" title="' + (e.direct ? '这台电脑直接连这个出口（适合只允许国内连接的节点）' : '经香港服务器中转到这个出口') + '">' +
          (e.own ? (S.exits === 'soft' ? (e.direct ? '本机直连' : '经香港中转') : '你自己加的' + (e.direct ? ' · 本机直连' : '')) + (e.sub ? ' · 订阅' : '') : '我们分配的') + '</span>' +
          (e.own ? '<span class="acts">' + btn('text', e.direct ? '改经香港' : '改本机直连', 'exitDirect:' + i, (b ? ' disabled' : '') + (e.err && !e.direct ? ' style="color:var(--accent);font-weight:600"' : '') + ' title="' + (e.direct ? '改回经香港服务器中转' : '由这台电脑直接连这个出口，卖家的节点只允许国内连接时用') + '"') +
          btn('text', '更换链接', 'exitRelink:' + i, b ? ' disabled' : '') + btn('text', '删除', 'exitDel:' + i, b || last ? ' disabled title="至少留一个出口"' : '') + '</span>' : '') + '</div></div>';
      }).join('') +
      (own < EXIT_MAX_OWN ? '<button class="p-exit add" data-act="exitAdd"' + (b ? ' disabled' : '') + '><b>' + I('plus', 16) + '添加出口</b><small>粘贴链接或订阅，最多 ' + EXIT_MAX_OWN + ' 个</small></button>' : '') + '</div>' +
      '<p class="p-sub p-exitnote">你自己加的出口好不好用由出口本身决定' + (S.exits === 'old' ? '；我们分配的出口有问题找客服' : '') + '。' +
      (S.exits === 'soft' && !S.trial ? btn('text', '升级到全套', 'upgrade') : '') + '</p></div>';
  }
  // 还没导入自己的 IP：现在用服务器默认出口，给一个明显的「导入我的 IP」入口。
  function noExitsSec(b) {
    return '<div class="p-sec"><div class="p-cardhead"><h2>出口</h2><span class="p-sub">出口决定网站看到的 IP</span></div>' +
      '<div class="p-exits"><button class="p-exit add" data-act="exitAdd"' + (b ? ' disabled' : '') + '><b>' + I('plus', 16) + '导入我的 IP</b><small>粘贴 SOCKS5、HTTP 代理，或 ss、vmess、vless、trojan 链接、订阅地址</small></button></div>' +
      '<p class="p-sub p-exitnote">导入后自动建立线路并开启防护，网站看到的就是你的 IP。</p></div>';
  }
  function routesPage() {
    var b = !!S.busy.route, sameDesc = ROUTES.every(function (r) { return r.desc === ROUTES[0].desc; }), ex = S.exits ? exitsShown()[S.exit] : null;
    return '<div class="p-pagehead"><div class="t"><h1>线路</h1><span>' + (ex ? ex.city + ' · ' + ex.ip : cityDot() + IP) + '</span></div></div>' +
      (ex ? exitsSec(b) + '<div class="p-sec p-exitlines"><div class="p-cardhead"><h2>「' + esc(ex.name) + '」的线路</h2></div></div>' : noExitsSec(b)) +
      // 自带出口（01 §5.2）：线路是用户自己的，写明好不好用由他的出口决定；可以更换、升级
      (S.ownExit === 'connected' && !S.exits ? '<div class="p-sec"><div class="hx-alert" role="note" style="align-items:center">' + I('info', 20) + '<span class="msg">自带出口：好不好用由你的出口决定</span>' +
        btn('secondary', '更换出口', 'dialog:ownexit') + (S.trial ? '' : btn('text', '升级到全套', 'upgrade')) + '</div></div>' : '') +
      // 自动 / 固定是最常用的操作（01 §5.13）：两张可选的卡，不缩在右上角
      '<div class="p-sec p-modes" role="group" aria-label="线路选择">' + [['auto', '自动选线路（推荐）', '按线路质量自动换，不会来回跳'], ['manual', '固定一条', '点哪条用哪条，完全连不上才临时换']].map(function (m) {
        return '<button class="p-mode" aria-pressed="' + (S.routeMode === m[0]) + '" data-act="routeMode:' + m[0] + '"><span class="rad" aria-hidden="true"></span><span class="t"><b>' + m[1] + '</b><small>' + m[2] + '</small></span></button>';
      }).join('') + '</div>' +
      '<div class="p-sec">' +
      '<div class="hx-routes p-routes" role="listbox" aria-label="线路"><div class="p-routehd" aria-hidden="true"><span>线路</span><span>延迟</span><span>成功率</span><span>下载</span><span></span></div>' + ROUTES.map(function (r, i) {
        var cur = i === S.route, desc = sameDesc ? '' : r.desc;
        return '<button class="hx-route" role="option" aria-selected="' + cur + '" data-act="pickRoute:' + i + '" data-ctx="route:' + i + '"' + (b ? ' disabled' : '') + '><span class="nm"><b>' + lineName(r) + (cur ? ' <span class="hx-mark">当前</span>' : '') + '</b>' + (desc ? '<span>' + desc + '</span>' : '') + '</span><span class="met" aria-label="延迟">' + (r.ms == null ? '—' : r.ms + ' ms') + '</span><span class="met" aria-label="成功率">' + (r.ok == null ? '—' : r.ok + '%') + '</span>' + speedMet(r, cur) + sig(r.sig) + '</button>';
      }).join('') + '</div>' + busy('route') +
      '<div class="hx-row p-routeacts">' + btn('secondary', I('refresh', 16) + '刷新连接', 'routeRefresh', b ? ' disabled' : '') +
        btn('secondary', I('download', 16) + '测速', 'speedTest', b || S.speedTesting ? ' disabled' : '') +
        btn('secondary', '这条线路不好用', 'routeBad', b ? ' disabled' : '') + '<span class="p-sub">换到另一条线路，并把这条线路的情况告诉我们</span></div>' +
      (S.speedTesting ? inline('正在测线路 ' + ROUTES[S.route].n + ' 的下载速度：连接后采样 8 秒，慢速线路最多约 30 秒') : '') + '</div>' +
      '<p class="p-sub">' + (ex ? '当前出口连不上时，自动换到你添加的下一个出口；全都连不上才断网，不会暴露真实 IP。' : '线路全都连不上时自动断网，不会暴露真实 IP。') + '</p>' + histChart() + eventsCard();
  }

  /* ---------------- 线路页：速度、延迟曲线、最近事件 ---------------- */
  /* 下载一格只写一行（01 §4 第 6 步）：测的时间、峰值的说明放鼠标停上去的提示里；只测当前线路，别的线路没数据写「切换后可测」 */
  function speedMet(r, cur) {
    if (!r.speed) return '<span class="met" aria-label="下载">' + (cur ? '<small class="na">还没测</small>' : '<small class="na" title="测别的线路要先切过去，会打断正在用的连接">切换后可测</small>') + '</span>';
    var m = r.speed.mbps, v = (m >= 10 ? Math.round(m) : m.toFixed(1)) + ' Mbps', a = r.speed.ago < 60 ? '刚刚' : Math.floor(r.speed.ago / 60) + ' 分钟前';
    return '<span class="met" aria-label="下载" title="' + (r.speed.measured ? '测速 · ' + a : '最近 30 分钟的峰值 · ' + a) + '">' + v + (r.speed.measured ? '' : '<em class="pk">峰值</em>') + '</span>';
  }
  function fmtRate(b) { return b < 1024 ? b + ' B/s' : b < 1048576 ? Math.round(b / 1024) + ' KB/s' : (b / 1048576).toFixed(1) + ' MB/s'; }
  var HIST_SPAN = 1800, HIST_COLORS = ['var(--accent)', 'var(--ok)', 'var(--warn)', 'var(--text-2)'];
  function histChart() {
    var L = S.hist.lines, all = [], oldest = 0;
    L.forEach(function (l) { l.pts.forEach(function (p) { oldest = Math.max(oldest, p[0]); if (p[1] != null) all.push(p[1]); }); });
    if (!all.length) return '<div class="hx-card p-hist p-sec"><div class="hd"><h2>延迟</h2></div><p class="p-sub">测几轮之后这里显示曲线。</p></div>';
    // 刚启动时数据只有几分钟，横轴跟着数据走，不把几个点挤在最右边。
    var span = Math.min(HIST_SPAN, Math.max(300, Math.ceil(oldest / 300) * 300));
    var W = 640, H = 110, lo = Math.min.apply(null, all), hi = Math.max.apply(null, all), pad = Math.max(10, (hi - lo) * 0.3);
    lo = Math.max(0, Math.floor(lo - pad)); hi = Math.ceil(hi + pad);
    var x = function (ago) { return ((span - Math.min(ago, span)) / span * W).toFixed(1); };
    var y = function (ms) { return (H - 4 - (ms - lo) / (hi - lo) * (H - 8)).toFixed(1); };
    var body = L.map(function (l, i) {
      var c = HIST_COLORS[i % HIST_COLORS.length], segs = [], cur = [], fails = '';
      l.pts.forEach(function (p) {
        if (p[1] == null) { if (cur.length) segs.push(cur); cur = []; fails += '<rect x="' + x(p[0]) + '" y="0" width="2" height="' + H + '" fill="var(--danger)" opacity=".35"></rect>'; }
        else cur.push(x(p[0]) + ',' + y(p[1]));
      });
      if (cur.length) segs.push(cur);
      return fails + segs.map(function (s) { return s.length > 1 ? '<polyline fill="none" stroke="' + c + '" stroke-width="1.5" stroke-linejoin="round" vector-effect="non-scaling-stroke" points="' + s.join(' ') + '"></polyline>' : '<circle cx="' + s[0].split(',')[0] + '" cy="' + s[0].split(',')[1] + '" r="2" fill="' + c + '"></circle>'; }).join('');
    }).join('');
    var legend = L.map(function (l, i) { var last = null; l.pts.forEach(function (p) { if (p[1] != null) last = p[1]; }); return '<span><i style="background:' + HIST_COLORS[i % HIST_COLORS.length] + '"></i>' + l.name + (last == null ? '' : ' · ' + last + ' ms') + '</span>'; }).join('');
    return '<div class="hx-card p-hist p-sec"><div class="hd"><h2>延迟</h2><span>' + lo + '～' + hi + ' ms</span></div>' +
      '<svg viewBox="0 0 ' + W + ' ' + H + '" preserveAspectRatio="none" role="img" aria-label="各线路最近 ' + span / 60 + ' 分钟的延迟曲线">' + body + '</svg>' +
      '<div class="axis"><span>' + span / 60 + ' 分钟前</span><span>现在</span></div><div class="lg">' + legend + '</div></div>';
  }
  function eventsCard() {
    var E = S.hist.events.slice(0, 6);
    return '<div class="hx-card p-hist p-sec"><div class="hd"><h2>最近事件</h2><span>只保存在这台电脑上</span></div>' +
      (E.length ? '<ul class="p-events">' + E.map(function (e) { return '<li><time>' + e.time + '</time><span>' + e.text + '</span></li>'; }).join('') + '</ul>' : '<p class="p-sub">还没有事件。切换线路、断网、暂停时会记在这里。</p>') + '</div>';
  }

  /* ---------------- 页面：安全检查 ---------------- */
  function progView(c) {
    var R = 52, C = 2 * Math.PI * R, frac = checkFrac(c), cur = c.list[c.idx], audit = cur[1] === 60;
    var pct = progPct(c);
    var ring = '<div class="hx-prog-ring" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + pct + '"><svg width="120" height="120" viewBox="0 0 120 120" aria-hidden="true"><circle class="track" cx="60" cy="60" r="52" fill="none" stroke-width="3.5"/><circle class="arc" data-prog-arc="' + C.toFixed(2) + '" cx="60" cy="60" r="52" fill="none" stroke-width="3.5" stroke-linecap="round" stroke-dasharray="' + C.toFixed(1) + '" stroke-dashoffset="' + (C * (1 - frac)).toFixed(1) + '" transform="rotate(-90 60 60)"/></svg><div class="ctr"><span><span data-prog-pct>' + pct + '</span>%</span></div></div>';
    var sub = audit ? '<span class="hx-small hx-muted">还剩 <span data-audit-left>' + H.mmss(Math.max(0, stageDur(c, c.idx) - c.t)) + '</span> · 预计还需 <span data-prog-left>' + checkRemaining(c) + '</span> 秒</span><span class="audit">这段时间请正常使用电脑</span>'
      : '<span class="hx-small hx-muted">预计还需 <span data-prog-left>' + checkRemaining(c) + '</span> 秒</span>';
    return '<div class="hx-prog p-prog"><div class="hx-prog-top"><span class="hx-title">' + (c.deep ? '深度检查' : '安全检查') + '</span>' + btn('text', '取消', 'checkCancel') + '</div>' +
      '<div class="hx-prog-main">' + ring + '<div class="txt"><span class="hx-heading">' + (audit ? cur[0] : '正在检查') + '</span>' + sub + '</div></div></div>';
  }
  /* 总进度按预计时长走，到 99 停住：结果以守护程序回来的为准。 */
  function progPct(c) { return Math.min(99, Math.round(checkFrac(c) * 100)); }
  function closingView() { return '<div class="p-closing" role="status">' + I('check', 20, 'c-ok') + '<span>检查完成 · 检查编号 ' + CHECK_ID + '</span></div>'; }
  function cnt(ic, cls, n, l) { return '<div>' + I(ic, 20, cls) + '<span class="hx-num-lg">' + n + '</span><span class="hx-caption hx-muted">' + l + '</span></div>'; }
  function resultView() {
    var groups = checkItems(), k = counts(groups);
    var hh = S.check.hist != null ? S.checkHist[S.check.hist] : null, cid = hh ? hh.id : CHECK_ID;
    var head = '<div class="p-pagehead"><div class="t">' + btn('text', '‹ 返回', 'checkBack', ' class="hx-btn hx-btn-text p-back" style="align-self:flex-start"') + '<h1>安全检查 · ' + wkTotal(k) + ' 项</h1><span data-ctx="copy:' + cid + '">' + (hh ? hh.at + ' · ' + CHECK_KIND[hh.kind] + ' · ' : '') + '检查编号 ' + cid + '</span></div><div class="hx-row">' +
      btn('secondary', I('refresh', 16) + '重新检查', 'checkAgain') + '</div></div>';
    return head + (S.busy.send ? '<div class="p-sec">' + busy('send') + '</div>' : '') +
      '<div class="p-ratio"><div class="bar" role="img" aria-label="' + k.ok + ' 项正常，' + k.warn + ' 项提示，' + k.danger + ' 项需要处理，' + k.skip + ' 项这次没查">' + [['ok', k.ok], ['warn', k.warn], ['danger', k.danger], ['skip', k.skip]].map(function (x) { return x[1] ? '<i class="' + x[0] + '" style="flex:' + x[1] + '"></i>' : ''; }).join('') + '</div>' +
      '<div class="nums"><span><b class="c-ok">' + k.ok + '</b>正常</span><span><b class="c-warn">' + k.warn + '</b>提示</span><span><b class="c-danger">' + k.danger + '</b>需要处理</span>' + (k.skip ? '<span><b>' + k.skip + '</b>这次没查</span>' : '') + '</div></div>' +
      '<div class="hx-checks">' + groups.map(function (g) {
        var bad = g.items.filter(function (it) { return it.s === 'warn' || it.s === 'danger'; }).length, skip = g.items.filter(function (it) { return it.s === 'skip'; }).length, open = S.grpOpen[g.n] != null ? S.grpOpen[g.n] : true;  // 检查结果默认全部展开；客户自己收起的才收起
        return '<details class="p-grp" data-grp="' + g.n + '"' + (open ? ' open' : '') + '><summary><b>' + g.n + '</b><span>' + (bad ? bad + ' 项要看' : skip ? (g.items.length - skip) + ' 项正常' : g.items.length + ' 项全部正常') + (skip ? ' · ' + skip + ' 项这次没查' : '') + '</span>' + I('chevron', 16) + '</summary>' + g.items.map(function (it) {
          return '<div class="hx-check"><span class="hx-dot ' + (it.s === 'ok' ? '' : it.s === 'skip' ? 'neutral' : it.s) + '" role="img" aria-label="' + ({ ok: '正常', warn: '提示', danger: '需要处理', skip: '这次没查' })[it.s] + '"></span><div class="t"><b>' + it.n + '</b><span>' + it.d + '</span></div>' + (it.act ? btn('secondary', it.act[0], it.act[1]) : '') + '</div>';
        }).join('') + '</details>';
      }).join('') + '</div>';
  }
  function checkPage() {
    var c = S.check;
    if (c.ctx === 'main' && c.phase === 'run') return progView(c);
    if (c.ctx === 'main' && c.phase === 'closing') return closingView();
    if (c.ctx === 'main' && c.phase === 'result') return resultView();
    var L = S.lastCheck, acts = btn('primary', '开始检查', 'checkStart') + btn('secondary', '深度检查', 'checkDeep');
    // 守护程序记着最近一次检查（手动、深度或后台自动）：打开就显示结果；还没查过才显示介绍。
    var top = L ? '<div class="hx-card p-lastcheck' + (L.problems ? ' warn' : '') + '"><div class="ic">' + I(L.problems ? 'warning' : 'shield', 28) + '</div>' +
        '<div class="t"><h2>' + (L.problems ? '上次检查有 ' + L.problems + ' 项要处理' : '上次检查一切正常') + '</h2><p>' + L.at + ' · ' + CHECK_KIND[L.kind] + '</p></div>' +
        '<div class="hx-row">' + acts + '</div></div>'
      : '<div class="hx-card p-checkintro"><div class="ic">' + I('pulse', 32) + '</div><div><h2>查一查有没有泄露</h2><p>普通检查几秒，深度检查约 1 分钟，期间照常用电脑。</p></div>' +
        '<div class="hx-row" style="justify-content:center">' + acts + '</div></div>';
    var bg = '<div class="hx-card p-kvcard p-bgchecks"><div class="p-cardhead"><h2>后台一直在查</h2></div>' +
      (S.guard && S.guard.blocked != null ? [['直连拦截', '这次连续保护挡下 ' + S.guard.blocked + ' 次']] : []).concat([['绕过防护的软件', '实时'], ['防护规则', '每分钟，出问题自动修好'], ['泄露', '每 6 小时，换网络、唤醒后也查']]).map(function (r) {
        return '<div class="p-kv"><span>' + r[0] + '</span><div class="v">' + r[1] + '</div></div>';
      }).join('') + '</div>';
    // 最近的检查（01 §5.8）：最近 10 次，点开看那一次的结果
    var CH = (S.checkHist || []).slice(0, 10);
    var hist = !CH.length ? '' : '<div class="hx-card p-sec p-chist"><div class="p-cardhead"><h2>最近的检查</h2><span>只保存在这台电脑上</span></div>' + CH.map(function (h, i) {
      return '<button class="p-chrow" data-act="checkHist:' + i + '"><span class="hx-dot ' + (h.problems ? 'warn' : '') + '" aria-hidden="true"></span><time>' + h.at + '</time><span class="k">' + CHECK_KIND[h.kind] + '</span>' +
        '<span class="r' + (h.problems ? ' warn' : '') + '">' + (h.problems ? h.problems + ' 项要处理' : '一切正常') + '</span>' + I('chevron', 16, 'go') + '</button>';
    }).join('') + '</div>';
    return '<div class="p-pagehead"><div class="t"><h1>安全检查</h1></div></div>' + top + hist + bg;
  }
  var CHECK_KIND = { auto: '后台自动检查', manual: '安全检查', deep: '深度检查' };

  /* ---------------- 页面：个人中心 ---------------- */
  function entry(ic, t, sub, act, n) { return '<button class="p-entry" data-act="' + act + '">' + I(ic, 20) + '<span class="t"><span>' + t + '</span>' + (sub ? '<small>' + sub + '</small>' : '') + '</span>' + (n ? '<span class="hx-badge" aria-label="' + n + ' 条未读">' + n + '</span>' : '') + I('chevron', 16, 'go') + '</button>'; }
  /* 个人中心：本期每天用量（01 §5.7），今天那根算到现在；不到 2 天不画。有上限、满 3 天才估到重置那天用多少。 */
  function gbText(v) { return v >= 10 ? Math.round(v) + ' GB' : v >= 1 ? v.toFixed(1) + ' GB' : Math.round(v * 1024) + ' MB'; }
  function dayLabel(from, i) {
    var m = /(\d+) 月 (\d+) 日/.exec(from || ''); if (!m) return '';
    var x = new Date(new Date().getFullYear(), +m[1] - 1, +m[2] + i);
    return (x.getMonth() + 1) + ' 月 ' + x.getDate() + ' 日';
  }
  function dailyCard() {
    var d = INFO.daily || [];
    if (d.length < 2) return '';
    var max = Math.max.apply(null, d.concat([0.01])), full = d.slice(0, -1);
    var avg = full.reduce(function (a, b) { return a + b; }, 0) / full.length;
    var est = INFO.limitGB && !unlimited() && full.length >= 3 && INFO.resetLeft != null ? INFO.usedGB + avg * INFO.resetLeft : null;
    // 到重置那天之前还没到的天也占位（浅色短线），整个周期铺满，看得出本月走到哪了。
    var future = INFO.resetLeft != null ? Math.max(0, Math.min(31 - d.length, INFO.resetLeft - 1)) : 0;
    // 鼠标停上去、键盘移过去都显示「11 月 3 日 · 2.4 GB」（01 §5.7）
    var bars = d.map(function (v, i) {
      var tip = (i === d.length - 1 ? '今天（到现在）' : dayLabel(INFO.dailyFrom, i)) + ' · ' + gbText(v);
      return '<i tabindex="0" role="img" aria-label="' + tip + '" data-tip="' + tip + '"' + (i === d.length - 1 ? ' class="today"' : '') + ' style="height:' + Math.max(3, Math.round(v / max * 100)) + '%"></i>';
    }).join('') + new Array(future + 1).join('<i class="future"></i>');
    return '<div class="hx-card p-sec p-daily"><div class="p-cardhead"><h2>每天用量</h2><span>日均 ' + gbText(avg) +
      (est != null ? ' · <span class="' + (est > INFO.limitGB ? 'warn' : '') + '">按现在的用量，到 ' + INFO.reset + '约用 ' + Math.round(est) + ' GB</span>' : '') + '</span></div>' +
      '<div class="bars" role="img" aria-label="本期每天的流量">' + bars + '</div><div class="axis"><span>' + INFO.dailyFrom + '</span><span>' + (future ? INFO.reset + '重置' : '今天') + '</span></div></div>';
  }
  function profilePage() {
    var exp = S.status === 'pm' && S.reason === 'expired';
    var renewTag = S.renew.phase === 'needinfo' ? '<span class="hx-tag warn">需要补充信息</span>'
      : S.renew.phase === 'processing' ? '<span class="hx-tag neutral">续费处理中</span>' : '';
    var renewAct = renewBtn();
    function tile(ic, lab, val, sub, act, cls) {
      return '<div class="hx-card hx-data p-tile' + (cls || '') + '"><div class="lab">' + I(ic, 16) + lab + (act ? '<span class="act">' + act + '</span>' : '') + '</div>' +
        '<div class="val' + (/^[\d.]/.test(val) ? '' : ' txt') + '">' + val + '</div>' + (sub ? '<div class="sub">' + sub + '</div>' : '') + '</div>';
    }
    function days(d) { return d + '<small>天</small>'; }
    // 到期卡（01 §5.7）：全套一个到期日，线路和 Claude 账号一起，旁边「续费」；单独软件买断、永久使用，旁边「升级到全套」
    var tiles = S.trial ? (S.trial === 'on' ? tile('calendar', '免费试用', trialLeft(), S.trialEnd.date + '到期', btn('text', '购买', 'buySoft') + btn('text', '换授权码', 'dialog:switchCode'))
        : tile('calendar', '免费试用', '已到期', S.trialEnd.date + '已到期', btn('text', '购买', 'buySoft') + btn('text', '换授权码', 'dialog:switchCode'), ' danger'))
      : byo() && S.licensePermanent === true ? tile('calendar', '授权', '永久使用', '一次买断，永久使用')
      : exp ? tile('calendar', suiteWord(), '已到期', suiteWord() + ' ' + S.exp.date + '已到期', (suiteWord() === '全套' ? btn('text', '重新开通全套', 'upgrade') : renewAct), ' danger')
      : S.exp.date === '—' ? tile('calendar', '账户', '—', '到期日还没拿到') : tile('calendar', suiteWord(), days(nodeLeft()), suiteWord() + ' ' + nodeDate() + '到期' + (renewTag ? ' · ' + renewTag : '') + offlinePaid(), renewAct + tierUpBtn());
    var nocap = unlimited(), full = !nocap && INFO.pct >= 100, pct = Math.min(100, INFO.pct);
    tiles += '<div class="hx-card hx-data p-tile' + (full ? ' danger' : '') + '"><div class="lab">' + I('download', 16) + '本月流量' + (nocap ? '' : '<span class="act pct">' + (pct >= 10 ? Math.round(pct) : pct.toFixed(1)) + '%</span>') + '</div>' +
      '<div class="val">' + trafficVal(full) + '</div>' +
      (nocap ? '' : '<div class="p-meter" role="progressbar" aria-label="本月流量" aria-valuenow="' + Math.round(pct) + '" aria-valuemin="0" aria-valuemax="100"><i style="width:' + pct + '%"></i></div>') +
      '<div class="sub">' + INFO.reset + '重置</div></div>';
    var contact = S.editingContact
      ? '<div class="p-kv"><span>联系方式</span><div class="v"><div class="p-cpick">' + contactKind('contact-kind', S.contact) + '<input class="hx-input" data-keep="contact" id="f-contact" value="' + esc(contactValue(S.contact)) + '" placeholder="' + CK_HINT[ckindOf('contact-kind', S.contact)] + '" aria-label="联系方式"></div></div><div class="hx-row">' + btn('primary', '保存', 'contactSave') + btn('secondary', '取消', 'contactCancel') + '</div></div>'
      : '<div class="p-kv"><span>联系方式</span><div class="v">' + esc(S.contact) + '</div>' + btn('text', '修改', 'contactEdit') + '</div>';
    // 余额（01 §5.7）：这台电脑归到网站账户才显示，只读，「充值」打开网页版个人中心
    if (S.balance != null) tiles += '<div class="hx-card hx-data p-tile"><div class="lab">' + I('wallet', 16) + '余额<span class="act">' + btn('text', '充值', 'openWeb') + '</span></div>' +
      '<div class="val"><span class="cur">¥</span>' + S.balance + '</div><div class="sub">网页版个人中心里使用</div></div>';
    // 消息、我的账号、工单、售后渠道在侧栏有自己的入口（01 §5.7），这页不再重复
    return '<div class="p-pagehead"><div class="t"><h1>个人中心</h1></div></div>' +
      '<div class="p-sec p-tiles">' + tiles + '</div>' + dailyCard() +
      '<div class="p-sec hx-card p-kvcard">' +
      '<div class="p-kv"><span>账户编号</span><div class="v hx-num" data-ctx="copy:' + ACCOUNT + '">' + ACCOUNT + '</div>' + copyBtn(ACCOUNT) + '</div>' +
      '<div class="p-kv"><span>套餐</span><div class="v">' + (S.trial ? '免费试用' : byo() ? '单独软件' : INFO.plan) + '</div><span></span></div>' +
      '<div class="p-kv"><span>本机设备</span><div class="v">' + INFO.device + '<span class="muted">' + INFO.bound + '绑定</span></div><span></span></div>' + contact + '</div>' +
      '<div class="p-sec hx-card" style="padding:0 16px">' + entry('external', '网页版个人中心', '余额充值、订单、邀请奖励', 'openWeb') + '</div>';
  }

  /* ---------------- 页面：我的账号 ---------------- */
  /* 登录 Claude（01 §5.12）：点了先在浏览器打开 Claude 登录页；守护程序经服务端等登录邮件（最多 AI_LOGIN_SECS 秒），
     到了就在这台电脑的浏览器里直接打开登录链接（链接不显示、不落盘）。S.aiLogin：{ id, phase: wait 等待 / ok 已打开 / timeout 没等到 / error 取不到, left 剩几秒 } */
  var aiLoginTimer = null;
  function aiLoginStop() { clearInterval(aiLoginTimer); aiLoginTimer = null; S.aiLogin = null; }
  function mmssLeft(s) { return Math.floor(s / 60) + ':' + ('0' + s % 60).slice(-2); }
  function loginBlock(a) {
    var L = S.aiLogin && S.aiLogin.id === a.id ? S.aiLogin : null, paused = S.status === 'paused';
    var go = btn('primary', I('external', 16) + '登录 Claude', 'aiLogin:' + a.id, ' style="min-height:32px"' + (paused ? ' disabled aria-disabled="true"' : ''));
    if (!L || paused) return '<div class="p-subhead"><b>登录</b><span>' + I('lock', 14) + '登录链接由软件在这台电脑上自动打开</span>' + go + '</div>';
    if (L.phase === 'wait') return '<div class="p-login"><div class="hx-alert info" role="status"><span class="hx-spin" aria-hidden="true"></span><span class="msg">在登录页填上面的邮箱、点「继续」，登录链接到了会自动打开<br><span class="hint">正在等登录邮件，还剩 <b data-ailogin-left>' + mmssLeft(L.left) + '</b></span></span>' +
      copyBtn(a.email) + btn('secondary', '取消', 'aiLoginCancel', ' style="min-height:32px"') + '</div><div class="p-loginbar" aria-hidden="true"><i data-ailogin-bar style="width:' + Math.round(L.left * 100 / AI_LOGIN_SECS) + '%"></i></div></div>';
    if (L.phase === 'ok') return '<div class="p-login"><div class="hx-alert ok" role="status">' + I('check', 18) + '<span class="msg">已在浏览器打开，登录好了就可以用</span></div></div>';
    if (L.phase === 'timeout') return '<div class="p-login"><div class="hx-alert warn" role="status">' + I('clock', 18) + '<span class="msg">没收到登录邮件<br><span class="hint">在登录页点「重新发送邮件」后再点这里</span></span>' + btn('primary', '再试一次', 'aiLogin:' + a.id, ' style="min-height:32px"') + '</div></div>';
    return '<div class="p-login"><div class="hx-alert danger" role="status">' + I('warning', 18) + '<span class="msg">暂时取不到登录邮件，已通知客服</span>' + btn('secondary', '知道了', 'aiLoginCancel', ' style="min-height:32px"') + '</div></div>';
  }
  /* 密码号的登录信息（01 §5.12「显示登录信息」）：邮箱密码、辅助邮箱和它的密码由守护程序经服务端现取、只在内存里；
     显示 AI_SHOW_SECS 秒后自动收起，服务端每次记时间、设备和保护状态；保护暂停着时「显示登录信息」灰着 */
  var aiHideTimer = null;
  function secretRow(label, value, shown) {
    return '<div class="p-kv"><span>' + label + '</span>' + (shown ? '<div class="v hx-num" data-ctx="copy:' + value + '">' + value + '</div>' + copyBtn(value) : '<div class="v p-mask" aria-label="' + label + '已隐藏">••••••••</div><span></span>') + '</div>';
  }
  function secretsHead(a, shown) {
    var paused = S.status === 'paused';
    return '<div class="p-subhead"><b>登录信息</b><span>' + (shown ? I('clock', 14) + Math.round(AI_SHOW_SECS / 60) + ' 分钟后自动收起' : I('lock', 14) + '显示时我们会记录时间和设备') + '</span>' +
      (shown ? btn('secondary', '收起', 'aiHide', ' style="min-height:32px"') : btn('primary', I('eye', 16) + '显示登录信息', 'aiShow:' + a.id, ' style="min-height:32px"' + (paused ? ' disabled aria-disabled="true"' : ''))) + '</div>';
  }
  function accCard(a) {
    if (!aiLive(a)) return accGone(a);
    var open = S.aiGuardOpen === a.id, rep = S.aiReported[a.id];
    var pwd = a.login === 'password', shown = pwd && S.aiShown === a.id && S.status !== 'paused';
    var soon = a.days != null && a.days <= 7;
    // 保障状态（01 §5.12）：现在被封可退多少（服务端现算，每天变）、「看怎么算的」
    var tag = rep ? '<span class="hx-tag neutral">正在核实账号状态</span>' : '<span class="hx-tag ok">现在被封可退 ¥' + a.refundNow + '</span>';
    var box = !open ? '' : '<div class="p-guardbox">' + (rep ? '<b>正在核实账号状态</b><span>已收到你的反馈，客服核实后会通过消息告诉你结果。</span>' + btn('text', '查看进度', 'go:tickets', ' style="align-self:flex-start;padding:0"')
      : '<b>现在被封可退 ¥' + a.refundNow + '</b><span>按使用规则登录和使用，被封了核实后按没用完的天数退 KYC 和订阅的钱，每天会少一点；软件、通道和开卡费不退。号不换，想接着用可以补号。</span><span class="how">' + aiOnly(a) + '。</span>' + btn('text', '查看完整规则', 'dialog:agreement', ' style="align-self:flex-start;padding:0"')) + '</div>';
    // 保护暂停着（01 §5.12「暂停时登录」）：「登录 Claude」灰着，上方提示暂停期间别登录
    var paused = S.status === 'paused' ? '<div class="hx-alert warn p-accpause" role="note" style="align-items:center">' + I('pause', 18) + '<span class="msg">保护暂停着，恢复防护以后再登录和使用，暂停期间登录或使用被封不退</span>' + btn('secondary', '恢复防护', 'resume', ' style="min-height:32px"') + '</div>' : '';
    return '<div class="hx-card p-acc"><div class="hd"><div class="nm"><b>' + a.plat + ' · ' + a.tier + '</b><span>' + a.since + '</span></div>' +
      // 没有可退金额（服务端没给：号还没发、全套到期等）就不显示保障标签
      (rep || a.refundNow ? '<div class="p-guardwrap"><button class="p-guard" data-act="aiGuard:' + a.id + '" aria-expanded="' + open + '" aria-label="保障状态，点开看说明">' + tag + I('chevron', 16) + '</button>' +
      (rep ? '' : btn('text', '看怎么算的', 'aiCalc:' + a.id)) + '</div>' : '') + '</div>' + (rep || a.refundNow ? box : '') +
      (soon ? '<div class="hx-alert warn p-accsoon" role="status">' + I('calendar', 18) + '<span class="msg">' + a.paid + '到期，到期后收回，不能再续</span>' + btn('secondary', '续费', 'aiRenew:' + a.id, ' style="min-height:32px"') + '</div>' : '') +
      paused + (pwd ? secretsHead(a, shown) : loginBlock(a)) +
      '<div class="p-kv"><span>账号邮箱</span><div class="v hx-num" data-ctx="copy:' + a.email + '">' + a.email + '</div>' + copyBtn(a.email) + '</div>' +
      (pwd ? secretRow('邮箱密码', a.pw, shown) + (a.hasAux || a.aux ? '<div class="p-kv"><span>辅助邮箱</span>' + (shown ? '<div class="v hx-num" data-ctx="copy:' + a.aux + '">' + a.aux + '</div>' + copyBtn(a.aux) : '<div class="v p-mask" aria-label="辅助邮箱已隐藏">••••••••</div><span></span>') + '</div>' + secretRow('辅助邮箱密码', a.auxpw, shown) : '') : '') +
      (a.paid ? '<div class="p-kv"><span>有效期至</span><div class="v">' + a.paid + '<small class="hx-muted" style="display:block">和全套一起按月续，到期当天收回，收回以后不能再续</small></div><span></span></div>' : '') +
      (pwd ? '<div class="p-kv"><span>怎么登录</span><div class="v"><ol class="p-steps"><li>在 Claude 登录页填账号邮箱、点「继续」</li><li>用上面的邮箱密码在这台电脑的浏览器里登录账号邮箱，收 Claude 发来的登录邮件</li><li>在这台电脑的浏览器里打开邮件里的登录链接</li></ol></div><span></span></div>' : '') +
      (rep ? '' : '<div class="acts">' + btn('text', '账号登不上了', 'aiReport:' + a.id) + '</div>') + '</div>';
  }
  /* 到期收回、被封退款以后：卡片变灰，登录信息收起、不能再显示。没有在用的号时给按钮（都是「在网页上打开」一键登录后到购买弹窗）：
     被封的、全套还没到期给「补号」；到期收回的（或被封以后全套也到期了）给「重新开通全套」，打开升级到全套 */
  function accGone(a) {
    var refunded = a.st === 'refunded', stopped = a.st === 'stopped', refill = window.CC_CAN_REFILL ? window.CC_CAN_REFILL(a) : (refunded || stopped) && !nodeExpired() && !byo();
    return '<div class="hx-card p-acc is-gone"><div class="hd"><div class="nm"><b>' + a.plat + ' · ' + a.tier + '</b><span>' + a.since + '</span></div>' +
      '<span class="hx-tag neutral">' + (refunded ? '已停用 · 已退款 ¥' + a.refund : stopped ? '已停用' : '已到期收回') + '</span></div>' +
      '<div class="p-kv"><span>账号邮箱</span><div class="v hx-num">' + a.email + '</div><span></span></div>' +
      '<p class="p-accgone">' + I('lock', 14) + '<span>' + (window.CC_AI_GONE ? window.CC_AI_GONE(a) : refunded ? a.endAt + '确认被封，¥' + a.refund + ' 已原路退回，软件和线路照常用到到期；登录信息已收起' : stopped ? a.endAt + '确认被封，没有退款，原因见消息；登录信息已收起' : a.endAt + '到期收回，登录信息已收起，这个号不能再续') + '</span></p>' +
      (aiActive() || S.aiPrep ? '' : '<div class="acts">' + (refill ? btn('primary', '补号', 'aiRefill') : btn('primary', '重新开通全套', 'upgrade')) + '<span class="p-accbuy">在网页上打开，买好后新号会出现在这里</span></div>') + '</div>';
  }
  /* 号在准备（01 §5.12）：首单、补号付了款、后台加的号还没发下来。S.aiPrep：line 等线路开通 / queued 排队等发放 / issuing 发放中 */
  var AI_PREP = { line: ['等线路开通', '独享线路开通以后再发号，免得没有保护就登录'], queued: ['排队中', '线路已经开通，号在排队等发放'], issuing: ['发放中', '正在给你发号'] };
  function accPrep() {
    var p = AI_PREP[S.aiPrep] || AI_PREP.queued;
    return '<div class="hx-card p-acc"><div class="hd"><div class="nm"><b>Claude 账号准备中</b><span>' + p[1] + '</span></div><span class="hx-tag neutral">' + p[0] + '</span></div>' +
      '<p class="p-accgone">' + I('clock', 14) + '<span>发好后会通知你，号会出现在这一页，点「登录 Claude」就能用</span></p></div>';
  }
  function accountsPage() {
    var live = AIACC.filter(aiLive), gone = AIACC.filter(function (a) { return !aiLive(a); });
    return '<div class="p-pagehead"><div class="t"><h1>我的账号</h1><span>我们提供的 Claude 账号，' + (live.length && live[0].login === 'password' ? '登录信息在这里看' : '在这里一键登录') + '</span></div></div>' +
      '<div class="p-sec">' + (live.length ? '<div class="hx-alert info" role="note" style="align-items:flex-start">' + I('shield', 20) + '<span class="msg">' + aiOnly(live[0]) + '</span></div>' : '') +
      (S.aiPrep ? accPrep() : '') + live.concat(gone).map(accCard).join('') + '</div>' +
      '<p class="hx-small hx-muted">' + (live.length ? '被封后点「账号登不上了」，核实后按没用完的天数退 KYC 和订阅的钱，号不换；想接着用可以补号。规则见用户协议第 12 条。' : gone.length ? '收回和退款的号在这里留 30 天。' : '') + '</p>';
  }

  /* 原型演示：切「我的账号」的样子（01 §5.12），连带那一刻会收到的消息 */
  function aiDemo(kind) {
    aiLoginStop(); clearTimeout(aiHideTimer); S.aiShown = null; S.aiDemo = kind; S.ai = true; S.aiPrep = /^prep/.test(kind) ? kind.slice(4) : ''; S.aiReported = {}; S.aiGuardOpen = null; S.ownExit = ''; S.trial = '';
    AIACC.length = 0; aiCase(kind).forEach(function (a) { AIACC.push(a); });
    for (var i = MSGS.length - 1; i >= 0; i--) if (/^M-AI-/.test(MSGS[i].id)) MSGS.splice(i, 1);
    var at = 0; MSGS.forEach(function (m, j) { if (m.id === 'M-1003') at = j + 1; });
    (AI_MSGS[kind] || []).slice().reverse().forEach(function (m) { var c = {}; for (var k in m) c[k] = m[k]; c.read = false; MSGS.splice(at, 0, c); });
    S.screen = 'main'; S.page = 'accounts'; S.dialog = null;
    if (S.status === 'pm' && S.reason === 'expired') setStatus('ok');
    else render();
  }

  /* ---------------- 页面：消息 ---------------- */
  function unreadMsgs() { return MSGS.filter(function (m) { return !m.read && !m.withdrawn; }).length; }
  function bodyText(t) { return esc(t).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>'); }
  function plain(t) { return String(t).replace(/\*\*/g, ''); }
  var LINK_ACT = { ticket: 'msgGo:ticket', renewal: 'msgGo:renewal', order: 'openWeb', product: 'openWeb', page: 'openWeb', refill: 'aiRefill', upgrade: 'upgrade' };
  function msgItem(m) {
    var open = S.msgOpen === m.id;
    return '<div class="p-mitem' + (m.read ? '' : ' unread') + (m.lv === 'action' ? ' action' : '') + (m.withdrawn ? ' withdrawn' : '') + (open ? ' open' : '') + '"><button class="hd" data-act="msgOpen:' + m.id + '" aria-expanded="' + open + '">' +
      '<span class="dot" aria-hidden="true"></span><span class="tt"><span>' + esc(m.title) + '</span>' + (m.lv === 'action' && !m.withdrawn ? '<span class="hx-tag warn" style="flex:none">需要你处理</span>' : '') + (m.read ? '' : '<span class="p-sronly">未读</span>') + '</span><time>' + m.t + '</time>' +
      '<span class="sum">' + (m.withdrawn ? '已撤回' : open ? bodyText(m.body) : esc(plain(m.body))) + '</span></button>' +
      (open && m.link && !m.withdrawn ? '<div class="go">' + btn('secondary', (m.link[0] === 'ticket' || m.link[0] === 'renewal' ? '' : I('external', 16)) + m.link[1], LINK_ACT[m.link[0]] + (m.link[2] ? ':' + m.link[2] : ''), ' style="min-height:36px"') + '</div>' : '') + '</div>';
  }
  function annItem(n) {
    var open = S.msgOpen === n.id;
    return '<div class="p-mitem' + (open ? ' open' : '') + '"><button class="hd" data-act="msgOpen:' + n.id + '" aria-expanded="' + open + '"><span class="dot" aria-hidden="true"></span><span class="tt"><span>' + esc(n.title) + '</span>' + (n.kind === 'maint' ? '<span class="hx-tag warn" style="flex:none">维护通知</span>' : n.kind === 'important' ? '<span class="hx-tag warn" style="flex:none">重要</span>' : '') + '</span><time>' + n.t + '</time><span class="sum">' + esc(n.body) + '</span></button></div>';
  }
  function messagesPage() {
    var u = unreadMsgs(), tab = S.msgTab;
    var list = tab === 'msg' ? (MSGS.length ? MSGS.map(msgItem).join('') : '<div class="p-accempty">还没有消息</div>') : (ANNS.length ? ANNS.map(annItem).join('') : '<div class="p-accempty">现在没有公告</div>');
    var group = tab === 'ann' ? groupCard() : '';
    return '<div class="p-pagehead"><div class="t"><h1>消息</h1><span>' + (u ? u + ' 条未读' : '没有未读消息') + '</span></div>' + (tab === 'msg' && u ? btn('secondary', '全部标为已读', 'msgReadAll') : '') + '</div>' +
      '<div class="hx-seg" role="tablist" aria-label="消息和公告" style="margin-bottom:12px">' + [['msg', '消息' + (u ? ' · ' + u : '')], ['ann', '公告']].map(function (t) { return '<button role="tab" aria-selected="' + (tab === t[0]) + '" aria-pressed="' + (tab === t[0]) + '" data-act="msgTab:' + t[0] + '">' + t[1] + '</button>'; }).join('') + '</div>' +
      group + '<div class="hx-card p-mlist" style="padding:0">' + list + '</div>';
  }
  /* 售后 QQ 群：后台「售后渠道」里类型是 QQ 群号的第一条；没有就不显示 */
  function groupCard() { var c = qqChan(); return c ? '<div class="hx-card" style="padding:14px 16px;margin-bottom:12px">' + qqBlock() + '</div>' : ''; }

  /* 公告勾了弹窗的、「需要处理」消息：打开主界面时居中弹一次，一次只弹一个 */
  function popupView() {
    var P = S.popup; if (!P) { fx('popup', ''); return ''; }
    var f = fx('popup', P.id);
    var tag = P.lv === 'update' ? '<span class="hx-tag neutral">新版本</span>' : P.lv === 'action' ? '<span class="hx-tag warn">需要你处理</span>' : P.kind === 'maint' ? '<span class="hx-tag warn">维护通知</span>' : P.kind === 'important' ? '<span class="hx-tag warn">重要公告</span>' : '<span class="hx-tag neutral">公告</span>';
    return '<div class="p-popscrim"><div class="p-popup' + (f ? ' pin' : '') + '" role="dialog" aria-modal="true" aria-labelledby="pop-t"><div class="k">' + tag + '</div><h3 id="pop-t">' + esc(P.title) + '</h3><p>' + bodyText(P.body) + '</p>' +
      '<div class="acts">' + (P.acts ? P.acts.map(function (a, i) { return btn(a[0], a[1], a[2], i ? '' : ' data-autofocus'); }).join('') : (P.link ? btn('secondary', P.link[1], 'popGo') : '') + btn('primary p-white', '知道了', 'popAck', ' data-autofocus')) + '</div></div></div>';
  }
  /* 新版本下载好了（01 §5.10）：每个版本弹一次，「立即更新」「下次开机时更新」；没点的概览上方一直有横幅 */
  function updatePopup() {
    showPopup({ id: 'update:' + S.newVersion, lv: 'update', title: '新版本 ' + S.newVersion + ' 已就绪', body: '升级约 10 秒，期间网络暂停（不会暴露真实 IP），装好后自动恢复。现在不方便的话，等下次开机时自动更新。', acts: [['primary p-white', '立即更新', 'popUpdateNow'], ['secondary', '下次开机时更新', 'popUpdateLater']] });
  }
  function showPopup(p) { if (S.popup) { if (S.popQueue.length < 3) S.popQueue.push(p); return; } S.popup = p; render(); }

  /* ---------------- 页面：工单 ---------------- */
  var ST_TAG = { wait: ['warn', '已回复'], done: ['ok', '已处理'], open: ['neutral', '待处理'], closed: ['neutral', '已关闭'] };
  /* 诊断信息（01 §5.13）：默认附上，可以不附；「看看里面有什么」逐项列出 */
  function diagBox() {
    var on = !S.newTicket || S.newTicket.diag !== false;
    return '<div class="p-diag"><label class="p-check"><input type="checkbox" data-act="ntDiag"' + (on ? ' checked' : '') + '>附上诊断信息</label>' +
      '<div class="row2"><span>日志、状态、最近的检查结果，不含密钥、密码和你访问的网站</span>' + btn('text', '看看里面有什么', 'dialog:diag') + '</div></div>';
  }
  /* 工单（01 §5.9「页面」）：先是整页列表，顶上按状态筛选，有新回复的排最前；点一张进整页对话，新建也整页 */
  var TFILTERS = [['all', '全部'], ['open', '待处理'], ['wait', '已回复'], ['done', '已处理'], ['closed', '已关闭']];
  function ticketListPage() {
    var F = S.tFilter || 'all';
    var n = function (k) { return S.tickets.filter(function (t) { return k === 'all' || t.st === k; }).length; };
    var list = S.tickets.filter(function (t) { return F === 'all' || t.st === F; }).slice().sort(function (a, b) { return (b.unread ? 1 : 0) - (a.unread ? 1 : 0); });
    var rows = list.map(function (t) {
      var tg = ST_TAG[t.st], last = t.msgs[t.msgs.length - 1];
      return '<button class="p-trow" data-act="ticket:' + t.id + '"><span class="r1">#' + t.id + ' · ' + t.title + (t.unread ? '<span class="hx-badge" aria-label="有新回复">新回复</span>' : '') + '<span class="hx-tag ' + tg[0] + '">' + tg[1] + '</span></span>' +
        '<span class="r2"><span class="last">' + (last.who === '客服' ? '客服：' : '') + esc(last.x || '［图片］') + '</span><time>' + esc(last.t) + '</time></span></button>';
    }).join('');
    return '<div class="p-pagehead"><div class="t"><h1>工单</h1><span>' + n('all') + ' 张 · 同时最多 3 张没关闭的</span></div>' + btn('primary', '新建工单', 'newTicket') + '</div>' +
      '<div class="hx-seg p-tfilter" role="tablist" aria-label="按状态筛选">' + TFILTERS.map(function (f) { return '<button role="tab" aria-selected="' + (F === f[0]) + '" aria-pressed="' + (F === f[0]) + '" data-act="tFilter:' + f[0] + '">' + f[1] + '<span class="n">' + n(f[0]) + '</span></button>'; }).join('') + '</div>' +
      '<div class="hx-card p-tl">' + (rows || '<div class="p-accempty">没有这类工单</div>') + '</div>' +
      '<div class="p-tnote">' + I('lock', 16) + '<span>工单走专用通道，断网时也能提交和查看回复</span></div>';
  }
  function ticketsPage() { return S.newTicket ? newTicketView() : convView(); }
  function backBtn(act) { return btn('text', '‹ 返回工单', act, ' class="hx-btn hx-btn-text p-back"'); }
  function convView() {
    var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0], tg = ST_TAG[t.st];
    var msgs = '<div class="hx-chat">' + t.msgs.map(function (m, mi) { return '<div class="hx-msg ' + (m.who === '你' ? 'me' : 'agent') + '"><span class="who">' + m.who + ' · ' + esc(m.t) + '</span><div class="b">' + (m.x ? '<span class="tx">' + esc(m.x) + '</span>' : '') + msgImgs(t, mi, m) + (m.diag ? '<span class="p-diagchip">' + I('medkit', 14) + '已附上诊断信息</span>' : '') + '</div></div>'; }).join('') + '</div>';
    var foot;
    if (t.st === 'done') {
      foot = '<div class="p-rate">' + (t.rated ? I('check', 16, 'c-ok') + '<span>已评价：' + t.rated + '。谢谢反馈。</span>' : '<span>这个问题解决了吗？</span>' + btn('secondary', '已解决', 'rate:已解决') + btn('secondary', '未解决', 'rate:未解决')) + '</div>';
    } else {
      var R = S.replyShots[t.id] || [], pics = S.ticketImages !== false;
      foot = (pics && R.length ? shotsView(R, 'replyShot', 'replyShotDel', 'replyShotOpen') : '') +
        '<div class="row"><textarea class="hx-textarea" data-keep="reply-' + t.id + '" id="f-reply" placeholder="' + (pics ? '回复客服，可以直接粘贴截图' : '回复客服') + '" aria-label="回复内容"></textarea>' + (pics && !R.length ? btn('secondary', '加图', 'replyShot') : '') + btn('primary', '发送', 'reply') + '</div>';
    }
    return '<div class="p-tconv"><div class="hd">' + backBtn('ticketBack') + '<div class="tt"><b>#' + t.id + ' · ' + t.title + '</b><span>' + (t.st === 'wait' ? '客服已回复，等你回复' : t.st === 'open' ? '已提交，客服会尽快回复' : t.st === 'closed' ? '已关闭' : '已解决') + '</span></div><span class="hx-tag ' + tg[0] + '">' + tg[1] + '</span></div>' +
      '<div class="thread" data-scroll="thread">' + msgs + '</div><div class="foot">' + foot + '</div></div>';
  }
  function ntSteps(T) { var st = ['type']; if (T && T.help.length) st.push('help'); if (T && T.q) st.push('q'); st.push('desc'); return st; }
  function ntShots(N) {
    var list = N.previews || []; while (list.length < N.shots) list.push('');
    return shotsView(list, 'ntShot', 'ntShotDel', 'ntShotOpen');
  }
  /* 工单图片（docs/01 §5.9）：对话里的图放在气泡里，点开看大图（左右切换）；发之前缩略图预览、可删；可以直接粘贴截图。
     原型里用示意图；真客户端按编号经守护程序分块取，取到之前是加载中的占位。 */
  function demoShot(n) {
    return 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="320" height="200"><rect width="320" height="200" fill="#e7eaf1"/><rect x="16" y="16" width="288" height="24" rx="4" fill="#cdd3df"/><rect x="16" y="56" width="180" height="12" rx="3" fill="#cdd3df"/><rect x="16" y="76" width="220" height="12" rx="3" fill="#cdd3df"/><text x="160" y="160" font-size="20" text-anchor="middle" fill="#8790a3">截图 ' + n + '</text></svg>');
  }
  function msgImgs(t, mi, m) {
    if (!m.imgs || !m.imgs.length) return '';
    return '<div class="p-mimgs">' + m.imgs.map(function (src, k) {
      return '<button class="p-mimg" data-act="imgOpen:' + t.id + ':' + mi + ':' + k + '" aria-label="看大图">' + (src ? '<img src="' + src + '" alt="截图">' : '<span class="ld" aria-label="图片加载中"></span>') + '</button>';
    }).join('') + '</div>';
  }
  function shotsView(list, addAct, delAct, openAct) {
    var shots = list.map(function (src, i) {
      return '<div class="p-shot"><button class="p-shotimg" data-act="' + openAct + ':' + i + '" aria-label="看大图">' + (src ? '<img src="' + src + '" alt="截图 ' + (i + 1) + '">' : '截图 ' + (i + 1)) + '</button>' +
        '<button class="p-shotx" data-act="' + delAct + ':' + i + '" aria-label="删除截图 ' + (i + 1) + '"><span>' + I('close', 12) + '</span></button></div>';
    }).join('');
    if (list.length < 3) shots += '<button class="p-shotadd" data-act="' + addAct + '">' + I('download', 16) + '加图</button>';
    return '<div class="p-shots">' + shots + '</div>';
  }
  function lightboxView() {
    var L = S.lightbox; if (!L || !L.list.length) return '';
    var many = L.list.length > 1;
    return '<div class="p-lightbox" role="dialog" aria-label="看大图" data-act="lbClose"><img src="' + L.list[L.i] + '" alt="截图">' +
      (many ? '<button class="lb-prev" data-act="lbGo:-1" aria-label="上一张">' + I('chevron', 20) + '</button><button class="lb-next" data-act="lbGo:1" aria-label="下一张">' + I('chevron', 20) + '</button><span class="lb-n">' + (L.i + 1) + ' / ' + L.list.length + '</span>' : '') +
      '<button class="lb-x" data-act="lbClose" aria-label="关闭">' + I('close', 16) + '</button></div>';
  }
  function lightbox(list, i) { list = list.filter(Boolean); if (!list.length) return; S.lightbox = { list: list, i: Math.min(Math.max(0, i), list.length - 1) }; render(); }
  function newTicketView() {
    var N = S.newTicket, T = N.type != null ? TTYPES[N.type] : null, steps = ntSteps(T), idx = steps.indexOf(N.step), body;
    var prev = idx > 0 ? steps[idx - 1] : null, next = steps[idx + 1];
    var back = prev ? btn('text', '上一步', 'ntNext:' + prev) : '';
    var crumb = '<div class="p-crumb">新建工单' + (T ? ' · ' + T.n : '') + '</div>';
    if (N.step === 'type') {
      body = '<h2 class="hx-heading" style="margin:0 0 12px">遇到的是哪一类问题？</h2><div class="p-types">' + TTYPES.map(function (t, i) { return '<button class="p-opt" data-act="ntType:' + i + '">' + t.n + '</button>'; }).join('') + '</div>';
    } else if (N.step === 'help') {
      body = '<h2 class="hx-heading" style="margin:0 0 4px">先试试这些</h2><p class="p-sub" style="margin-bottom:12px">没解决再继续提交。</p>' +
        '<div class="hx-row" style="margin-bottom:16px">' + T.help.map(function (h) { return btn('secondary', h[0], h[1]); }).join('') + '</div>' +
        '<div class="hx-row">' + btn('primary', '没解决，继续提交', 'ntNext:' + next) + back + '</div>';
    } else if (N.step === 'q') {
      body = '<label class="hx-field"><span class="hx-heading" style="margin-bottom:6px">' + T.q + '</span>' +
        (T.shot && S.ticketImages !== false ? ntShots(N) : '<textarea class="hx-textarea" data-keep="nt-ans" id="f-ntans" style="min-height:64px"></textarea>') + '</label>' +
        (S.actErr.nt ? '<span class="hx-help is-error" style="display:flex;gap:4px;margin-top:6px">' + I('warning', 16) + S.actErr.nt + '</span>' : '') +
        '<div class="hx-row" style="margin-top:16px">' + btn('primary', '下一步', 'ntQNext') + back + '</div>';
    } else {
      body = '<label class="hx-field"><span class="hx-label">描述问题</span><textarea class="hx-textarea" data-keep="nt-desc" id="f-ntdesc" placeholder="什么时候开始的、在做什么时出现的">' + (N.desc ? esc(N.desc) : '') + '</textarea>' + (S.actErr.nt ? '<span class="hx-help is-error">' + I('warning', 16) + S.actErr.nt + '</span>' : '') + '</label>' +
        (S.ticketImages !== false ? '<div class="hx-field" style="margin-top:12px"><span class="hx-label">截图（最多 3 张，也可以直接粘贴）</span>' + ntShots(N) + '</div>' : '') +
        '<div style="margin-top:16px">' + diagBox() + '</div>' +
        '<div class="hx-row" style="margin-top:16px">' + btn('primary', '提交', 'ntSubmit') + back + '</div>';
    }
    var names = { type: '选择类型', help: '自助办法', q: '一个问题', desc: '描述问题' };
    return '<div class="p-tconv"><div class="hd">' + backBtn('ntCancel') + '<div class="tt"><b>新建工单</b><span>' + (T ? '第 ' + (idx + 1) + ' 步，共 ' + steps.length + ' 步 · ' : '第 1 步 · ') + names[N.step] + '</span></div></div><div class="p-new">' + crumb + body + '</div></div>';
  }

  /* ---------------- 页面：帮助（01 §5.13） ---------------- */
  function helpPage() {
    return '<div class="p-pagehead"><div class="t"><h1>帮助</h1><span>常见问题和官网是同一份，断网时也能看</span></div></div>' +
      '<div class="hx-card p-sec p-faq"><div class="p-cardhead"><h2>常见问题</h2></div>' + (FAQ.length ? '' : '<p class="p-sub">还没拿到帮助内容，连上网后会自动更新</p>') + FAQ.map(function (f, i) {
        return '<details class="p-faqi" data-faq="' + i + '"' + (S.faqOpen[i] ? ' open' : '') + '><summary><b>' + f[0] + '</b>' + I('chevron', 16) + '</summary><p>' + f[1] + '</p></details>';
      }).join('') + '</div>' +
      (GUIDES.length ? '<div class="hx-card p-sec p-guides"><div class="p-cardhead"><h2>使用指南</h2><span>在浏览器里打开官网</span></div>' + GUIDES.map(function (g, gi) { return entry('book', g[0], g[1], 'guideOpen:' + gi); }).join('') + '</div>' : '') +
      '<div class="hx-card p-sec"><div class="p-cardhead"><h2>联系我们</h2><span>联系时请提供账户编号 ' + ACCOUNT + '</span></div><div>' + channels() + '</div>' +
      '<div class="p-helpacts">' + btn('primary', I('chat', 16) + '提交工单', 'newTicket') + btn('secondary', I('medkit', 16) + '收集诊断信息', 'helpDiag') + '</div></div>';
  }

  /* ---------------- 页面：设置 ---------------- */
  function sw(on, act, label, dis) { return '<button class="hx-switch" role="switch" aria-checked="' + on + '" aria-label="' + label + '" data-act="' + act + '"' + (dis ? ' disabled' : '') + '></button>'; }
  /* 联系方式：先选类型再填账号，存成一行「类型 账号」（校验在 common::contact）。
     类型是一排按钮，不用下拉框：下拉框展开时界面一刷新就被收回去。选中的类型放在隐藏框里，和输入框一样跨重画保留。 */
  var CKINDS = ['微信', 'QQ', '邮箱', 'Telegram'];
  var CK_HINT = { '微信': '微信号或绑定的手机号', 'QQ': 'QQ 号', '邮箱': '邮箱地址', 'Telegram': '@用户名或手机号' };
  function ckindOf(key, cur) { var el = root && root.querySelector('[data-keep="' + key + '"]'); var k = el ? el.value : (cur || '').split(' ')[0]; return CKINDS.indexOf(k) < 0 ? '微信' : k; }
  function contactKind(key, cur, off) {
    var k = ckindOf(key, cur);
    return '<div class="hx-seg p-ckind" role="group" aria-label="联系方式类型">' + CKINDS.map(function (x) { return '<button type="button" aria-pressed="' + (x === k) + '" data-act="ckind:' + key + ':' + x + '"' + (off ? ' disabled' : '') + '>' + x + '</button>'; }).join('') + '</div><input type="hidden" data-keep="' + key + '" value="' + k + '">';
  }
  function contactValue(cur) { return (cur || '').replace(/^\S+\s+/, ''); }
  function lanUserList() {
    if (!S.lan.length) return '<p class="p-sub" style="padding:8px 0">还没有添加地址。</p>';
    return '<div class="p-lanlist">' + S.lan.map(function (l, i) {
      var note = S.lanEdit === i ? '<input class="hx-input" data-keep="lanedit" id="f-lanedit" value="' + esc(l.note) + '" aria-label="备注">' : '<span class="nt">' + (l.note ? esc(l.note) : '无备注') + (l.pub ? ' · <span class="p-danger-note">直接看到真实 IP</span>' : '') + '</span>';
      var acts = S.lanEdit === i ? btn('primary', '保存', 'lanNoteSave:' + i) : btn('text', '备注', 'lanNote:' + i) + btn('text', '删除', 'lanDel:' + i);
      return '<div class="p-lanrow"><span class="ip hx-num">' + esc(l.ip) + '</span>' + note + '<div class="hx-row" style="flex-wrap:nowrap">' + acts + '</div></div>';
    }).join('') + '</div>';
  }
  /* 设置分组（01 §5.13）：通用、网络、关于，最下面单独一组危险操作 */
  function setGroup(title, sub, inner, cls) { return '<div class="p-sec p-setgroup' + (cls || '') + '"><div class="p-grouph"><h2>' + title + '</h2>' + (sub ? '<span>' + sub + '</span>' : '') + '</div>' + inner + '</div>'; }
  function routeCard() {
    var R = S.splitRoute || { listed: false, domains: [] };
    // 共存模式（开着客户自己的代理）：名单里的走 Leyra 出口，其他交给客户自己的代理；名单一直可以改。
    if (S.coexist) return '<div class="hx-card p-setcard"><div class="hx-setrow"><div class="l"><span>走 Leyra 出口的网站</span><small>' +
      (S.sharedVpn ? '共存模式：检测到你开着自己的 VPN（' + esc(S.sharedVpn) + '）。Claude 和名单里的网站走 Leyra 出口，其他照常走你自己的 VPN。' : '共存模式：检测到你开着自己的代理。名单里的网站走 Leyra 出口，其他照常走你自己的代理。') +
      '加好后点「保存名单」，生效后刷新网页即可</small></div></div>' + routeEditor(R) +
      // Claude Code 不认系统代理：共存模式下它只能连 Leyra，要给它设一下代理（不设也不会暴露真实 IP，只是连不上）
      '<div class="hx-setrow"><div class="l"><span>Claude Code</span><small>共存模式下 Claude Code 要走 Leyra 本机代理：在终端里先执行 <code>set HTTPS_PROXY=http://127.0.0.1:17890</code>（Mac 上是 <code>export HTTPS_PROXY=http://127.0.0.1:17890</code>）再启动 claude。不设置时 Claude Code 连不上，但不会暴露真实 IP</small></div></div></div>';
    return '<div class="hx-card p-setcard"><div class="hx-setrow"><div class="l"><span>只让名单里的网站走出口</span><small>' +
      (R.listed ? '已开启：名单外的网站和应用直连，速度和平时一样，但会看到你的真实 IP' : '关闭时所有流量都走出口（最安全）；开启后只有名单里的网站走出口，其他直连更快') +
      '</small></div>' + sw(!!R.listed, 'routeListed', '只让名单里的网站走出口') + '</div>' +
      (R.listed ? routeEditor(R) : '') + '</div>';
  }
  function routeEditor(R) {
    return (true ? '<div class="hx-stack" style="padding:0 0 4px"><textarea class="hx-input" data-keep="routedomains" rows="4" aria-label="走出口的网站" placeholder="一行一个网站或 IP，例如 claude.ai（自动包含子域名）、1.1.1.1">' + esc((R.domains || []).join('\n')) + '</textarea>' +
        '<div style="display:flex;gap:8px;justify-content:flex-end">' + btn('text', '恢复默认', 'routeReset') + btn('secondary', '保存名单', 'routeSave') + '</div></div>' : '');
  }
  function settingsPage() {
    return '<div class="p-pagehead"><div class="t"><h1>设置</h1></div></div>' +
      setGroup('通用', '', '<div class="hx-card p-setcard">' +
        '<div class="hx-setrow"><div class="l"><span>外观</span></div><div class="hx-seg" role="group" aria-label="外观">' + [['system', '跟随系统'], ['light', '浅色'], ['dark', '深色']].map(function (a) { return '<button aria-pressed="' + (S.appearance === a[0]) + '" data-act="appearance:' + a[0] + '">' + a[1] + '</button>'; }).join('') + '</div></div>' +
        '<div class="hx-setrow"><div class="l"><span>通知</span><small>状态变化、工单回复、到期提醒</small></div>' + sw(S.notify, 'notify', '通知') + '</div>' +
        // 出口连不上时：开 = 自动换到能用的出口并通知（网站看到的 IP 会变）；关 = 直接断网提醒（IP 不会自己变）
        '<div class="hx-setrow"><div class="l"><span>出口连不上时自动换</span><small>' + (S.autoExit !== false ? '开：自动换到其他能用的出口并通知你，网站看到的 IP 会变' : '关：直接断网提醒，网站看到的 IP 不会自己变') + '</small></div>' + sw(S.autoExit !== false, 'autoExit', '出口连不上时自动换') + '</div>' +
        '<div class="hx-setrow"><div class="l"><span>时区</span><small>' + (!TZN.exit ? lat(TZN.sys) + '· 还不知道出口所在的时区，暂时没法比对' : S.tzChanged ? '和出口一致 · ' + TZN.exit : lat(TZN.sys) + '· 和出口不一致，境外网站可能据此判断你所在的地区') + '</small></div>' + (!TZN.exit ? '' : S.tzChanged ? btn('text', '恢复原来的时区', 'tzRestore') : btn('secondary', '更换时区', 'tzChange')) + '</div>' +
        '</div>') +
      setGroup('网络', '', routeCard() + '<div class="hx-card"><div class="p-cardhead" style="margin:0"><h2>局域网</h2></div>' +
        '<p class="p-explain">局域网地址都已内置（10.x、172.16～31.x、192.168.x 等），打印机、NAS、路由器直接可用，不用添加</p>' +
        (S.lan.length ? '<div class="hx-caption hx-muted" style="margin-top:4px">你添加的公网地址（不走独享线路）</div>' + lanUserList() : '') +
        '<details class="p-adv"' + (S.advOpen ? ' open' : '') + ' data-adv><summary>高级 ' + I('chevron', 16) + '</summary><div class="hx-stack" style="margin-top:8px"><p class="p-sub">公网地址默认不能添加。只有确认需要让某个公网地址看到你的真实 IP 时才在这里添加：单个地址或最大 /24 的网段，最多 20 条；AI 服务、CDN、公共 DNS 和线路自己的地址不能加。</p>' +
        '<div class="p-lanadd"><input class="hx-input" data-keep="advip" id="f-advip" placeholder="例如 203.0.113.50" aria-label="公网地址"><input class="hx-input" data-keep="advnote" placeholder="备注（选填）" aria-label="备注">' + btn('danger', '添加公网地址', 'advAdd') + '</div>' +
        (S.advMsg ? '<span class="hx-help is-error" style="display:flex;gap:4px;align-items:center">' + I('warning', 16) + S.advMsg + '</span>' : '') + '</div></details></div>' +
        '<div class="hx-card p-setcard"><div class="hx-setrow"><div class="l"><span>开通前先断网</span><small>开通前只能访问局域网；关掉要确认是你本人</small></div>' + sw(S.prepPaused, 'prepPaused', '开通前先断网') + '</div></div>') +
      setGroup('关于', '', '<div class="hx-card p-setcard">' +
        '<div class="hx-setrow"><div class="l"><span>版本 ' + S.version + '</span><small>' + (S.busy.upd ? '' : '自动下载新版本，下载完成后提示') + '</small>' + busy('upd') + '</div>' + btn('secondary', '检查更新', 'checkUpdate', S.busy.upd ? ' disabled' : '') + '</div>' +
        '<div class="hx-setrow"><div class="l"><span>用户协议</span></div>' + btn('text', '查看', 'dialog:agreement') + '</div>' +
        '<div class="hx-setrow"><div class="l"><span>免责声明</span></div>' + btn('text', '查看', 'dialog:disclaimer') + '</div>' +
        '</div>') +
      setGroup('危险操作', '会撤掉防护、暴露真实 IP，只在完全上不了网或不用了时用', '<div class="hx-card p-setcard">' +
        '<div class="hx-setrow"><div class="l"><span>网络恢复工具</span><small>完全上不了网时用：撤掉防护规则、恢复系统网络设置</small></div>' + btn('danger', '打开', 'rescueOpen') + '</div>' +
        '<div class="hx-setrow"><div class="l"><span>彻底退出</span><small>撤掉防护、关掉软件，会暴露真实 IP；下次打开软件后点「开启防护」即可恢复</small></div>' + btn('danger', I('lock', 14) + '彻底退出', 'dialog:quitAll') + '</div>' +
        '</div>', ' p-dangerzone');
  }

  /* ---------------- 整页：激活 / 向导 ---------------- */
  function activateScreen() {
    // 授权码激活：一个授权码绑定一台电脑；重装软件或系统后在同一台电脑上填原来的码即可恢复。
    var E = S.actErr || {}, off = S.busy.act ? ' disabled' : '';
    function err(k) { return E[k] ? '<span class="hx-help is-error">' + I('warning', 16) + esc(E[k]) + '</span>' : ''; }
    var steps = [['填授权码激活', '在售后 QQ 群下单拿到授权码，填在左边点「激活」。一个授权码只能用于一台电脑。'],
      ['接入你的出口', '激活后点「接入你的出口」，粘贴你的 IP（socks5、http、ss、vmess 等链接或订阅），接上就自动开启防护。'],
      ['自动校准，随时检查', '浏览器保护和电脑时区自动设好；「安全检查」里能看到浏览器实际的出口 IP。']];
    return '<div class="p-full lx-act"><div class="p-wordmark"><span class="p-logo" aria-hidden="true"></span>Leyra</div>' +
      '<div class="p-act"><div class="hx-card">' +
      '<div><h1 class="hx-title">激活</h1><p class="p-sub">填你在售后 QQ 群买到的授权码。重装过软件或系统的，填原来的授权码即可恢复。</p></div>' +
      '<label class="hx-field"><span class="hx-label">授权码</span><input class="hx-input hx-num" data-keep="act-code" placeholder="XXXX-XXXX-XXXX-XXXX" autocomplete="off" spellcheck="false"' + off + '>' + err('code') + '</label>' +
      '<div class="hx-field"><span class="hx-label">联系方式（必填）</span>' + contactKind('act-contact-kind', '', !!off) + '<input class="hx-input" data-keep="act-contact" placeholder="微信号或绑定的手机号" aria-label="联系方式"' + off + '><span class="hx-help">售后靠它联系你，请填真实账号</span>' + err('contact') + '</div>' +
      '<label class="hx-field"><span class="hx-label">订单号（选填）</span><input class="hx-input" data-keep="act-order" placeholder="下单时的订单号或备注"' + off + '></label>' +
      '<div class="lx-agree"><label><input type="checkbox" data-keep="act-agree"' + off + '> 我已阅读并同意</label>' + btn('text', '用户协议', 'dialog:agreement') + '</div>' + err('agree') +
      '<div>' + (S.busy.act ? busy('act') : btn('primary', '激活', 'activate', ' style="min-width:120px"')) + '</div></div>' +
      '<div class="lx-side"><ol class="lx-steps">' + steps.map(function (x, i) { return '<li><span class="n">' + (i + 1) + '</span><div><b>' + x[0] + '</b><p>' + x[1] + '</p></div></li>'; }).join('') + '</ol>' +
      '<p class="p-sub">重装过系统？输入原来的授权码即可恢复。换了电脑请联系售后。</p>' +
      (qqChan() ? '<div class="hx-card lx-qq">' + qqBlock() + '</div>' : '') + '</div></div></div>';
  }
  function stepper(n) {
    var names = ['时区', '局域网', '安全检查'], heads = ['检查电脑时区', '确认局域网设备', '安全检查'];
    return '<div class="hx-steps"><div class="txt" style="display:flex;flex-direction:column;gap:4px"><div class="hx-small hx-muted">第 ' + n + ' 步，共 3 步</div><div class="hx-heading">' + heads[n - 1] + '</div></div>' +
      '<div class="hx-steps-bar" aria-hidden="true">' + [1, 2, 3].map(function (i) { return '<i class="' + (i < n ? 'done' : i === n ? 'cur' : '') + '"></i>'; }).join('') + '</div>' +
      '<div class="hx-steps-list">' + names.map(function (x, k) { var i = k + 1; return '<div class="' + (i < n ? 'done' : i === n ? 'cur' : '') + '"' + (i === n ? ' aria-current="step"' : '') + '><span class="n">' + (i < n ? I('check', 12) : i) + '</span>' + x + '</div>'; }).join('') + '</div></div>';
  }
  function wizardScreen() {
    var W = S.wizard, bd, ft = '';
    if (W.step === 0) {
      var okNow = S.status === 'ok';
      bd = '<div class="content" style="align-items:center;text-align:center"><div class="hx-status">' + H.statusGraphic(okNow ? 'ok' : 'neutral', fam()).replace('class="hx-sg ', 'class="hx-sg' + fx('wsg', okNow) + ' ') + '<div class="txt"><h2>' + (okNow ? '已保护' : byo() ? '出口已接上' : '线路已开通') + '</h2>' + (okNow ? '<p>真实 IP 已隐藏，所有流量经' + (byo() ? '你的出口' : '独享线路') + '传输。</p>' : inline(S.starting && S.starting.stage === 1 ? '正在校验线路完整性' : '正在建立独享线路')) + '</div></div></div>';
      if (okNow) ft = btn('primary', '下一步', 'wizNext');
    } else if (W.step === 1) {
      bd = stepper(1) + '<div class="content"><p class="p-sub" style="font-size: 14px;line-height: 22px">境外网站可能通过电脑时区判断你所在的地区，建议和出口保持一致。</p>' +
        (!TZN.exit ? '<div class="hx-alert info" role="note">' + I('info', 20) + '<span class="msg">还不知道出口所在的时区，知道后会再提醒你比对。先跳过这一步。</span></div>'
        : '<div class="p-cmp">' +
        '<div class="hx-card hx-data"><div class="lab">' + I('globe', 16) + '出口位置</div><div class="val txt" style="font-size: 16px;line-height: 24px">' + (TZN.city || TZN.exit) + '</div><div class="sub">' + (TZN.city ? TZN.exit : '出口时区') + '</div></div>' +
        '<div class="hx-card hx-data"><div class="lab">' + I('clock', 16) + '电脑时区</div><div class="val txt" style="font-size: 16px;line-height: 24px">' + (W.tz === 'changed' ? TZN.exit : TZN.sys) + '</div><div class="sub" style="display:flex;gap:4px;align-items:center">' + (W.tz === 'changed' ? I('check', 16, 'c-ok') + '和出口一致' : I('warning', 16, 'c-warn') + '和出口不一致') + '</div></div></div>' +
        '<div class="hx-row">' + (W.tz === 'changed' ? btn('text', '恢复原来的时区', 'wizTzRestore') : btn('secondary', '更换时区', 'dialog:tz')) + '</div>') + '</div>';
      ft = btn('primary', '下一步', 'wizNext');
    } else if (W.step === 2) {
      bd = stepper(2) + '<div class="content"><p style="color:var(--text)">家里和公司的局域网地址都已内置（10.x、172.16～31.x、192.168.x 等），打印机、NAS、路由器管理页直接可用，不用添加。</p></div>';
      ft = btn('primary', '下一步', 'wizNext');
    } else {
      var c = S.check;
      if (c.ctx === 'wizard' && c.phase === 'run') bd = stepper(3) + '<div class="content">' + progView(c) + '</div>';
      else if (c.ctx === 'wizard' && c.phase === 'closing') bd = stepper(3) + closingView().replace('padding:160px', 'padding:80px');
      else {
        var wk = WK(), bad = badItems();
        bd = stepper(3) + '<div class="content center"><div class="hx-status" style="padding:8px 0">' + H.statusGraphic(wk.danger ? 'danger' : wk.warn ? 'warn' : 'ok', fam()).replace('class="hx-sg ', 'class="hx-sg fx ') + '<div class="txt"><h2>' + (wk.danger ? wk.danger + ' 项需要处理' : wk.warn ? wk.warn + ' 项有提示' : wkTotal(wk) + ' 项，全部正常') + '</h2><p>报告已生成 · 检查编号 ' + CHECK_ID + '</p></div></div>' +
          '<div class="p-counts">' + cnt('check', 'c-ok', wk.ok, '正常') + cnt('warning', 'c-warn', wk.warn, '提示') + cnt('shield-off', 'c-danger', wk.danger, '需要处理') + '</div>' +
          (bad.length ? '<div class="p-wizlist">' + bad.map(function (it) { return '<div><span class="hx-dot ' + it.s + '" aria-hidden="true"></span><b>' + it.n + '</b><span>' + it.d + '</span></div>'; }).join('') + '</div><p class="hx-small hx-muted">点「完成」后在「安全检查」里看详细说明和处理办法。</p>' : '') + '</div>';
        ft = btn('primary', '完成', 'wizDone');
      }
    }
    return '<div class="p-wiz"><div class="bd">' + bd + '</div>' + (ft ? '<div class="ft">' + ft + '</div>' : '') + '</div>';
  }

  /* ---------------- 窗口骨架 ---------------- */
  var NAV_ALL = [['overview', '概览', 'shield-plain'], ['routes', '线路', 'signal'], ['check', '安全检查', 'pulse'], ['accounts', '我的账号', 'key'], ['profile', '个人中心', 'user'], ['messages', '消息', 'bell'], ['tickets', '工单', 'chat'], ['help', '帮助', 'help'], ['settings', '设置', 'settings']];
  function navItems() { return NAV_ALL.filter(function (n) { return n[0] !== 'accounts' || S.ai || !!S.aiPrep; }); }
  function navView() {
    var unread = S.tickets.filter(function (t) { return t.unread; }).length;
    var card;
    if (S.status === 'down') card = ['danger', '防护没有运行', '真实 IP 可能暴露', 'shield-off'];
    else if (S.status === 'ok') card = ['ok', '已保护', curExit() ? esc(curExit().name) + ' · 线路 ' + ROUTES[S.route].n : '线路 ' + ROUTES[S.route].n + (typeof ROUTES[S.route].ms === 'number' ? ' · ' + ROUTES[S.route].ms + ' ms' : ''), 'shield'];
    else if (S.status === 'pm') card = ['danger', '已断网', '真实 IP 没有暴露', 'shield-off'];
    else if (S.status === 'paused' && S.pause && S.pause.kind === 'off') card = ['warn', '防护未开启', '点「开启防护」开始保护', 'pause'];
    else if (S.status === 'paused') card = ['warn', '防护已暂停', '<span data-mmss>' + H.mmss(S.pause.left) + '</span> 后恢复', 'pause'];
    else if (S.status === 'starting') card = ['neutral', '启动中', '正在建立独享线路', 'shield-plain'];
    else card = ['neutral', '准备中', S.ownExit === 'waiting' ? '还没接出口' : '线路开通中', 'shield-plain'];
    var mu = unreadMsgs(), cur = S.page;
    return '<nav class="hx-nav" aria-label="主导航">' + navItems().map(function (n) {
      var badge = n[0] === 'tickets' && unread ? '<span class="hx-badge badge" aria-label="' + unread + ' 条未读">' + unread + '</span>'
        : n[0] === 'messages' && mu ? '<span class="hx-badge badge" aria-label="' + mu + ' 条未读消息">' + mu + '</span>' : '';
      return '<button class="hx-nav-item" aria-current="' + (cur === n[0] ? 'page' : 'false') + '" data-act="go:' + n[0] + '">' + (cur === n[0] ? '<i class="bar" aria-hidden="true"></i>' : '') + I(n[2], 18) + '<span>' + n[1] + '</span>' + badge + '</button>';
    }).join('') + '<div class="hx-nav-spacer"></div><button class="hx-scard ' + card[0] + '" data-act="go:overview" aria-label="' + card[1] + '，回到概览">' + I(card[3], 20) + '<div><b>' + card[1] + '</b><span>' + card[2] + '</span></div></button></nav>';
  }
  function mainContent() {
    switch (S.page) {
      case 'routes': return routesPage();
      case 'check': return checkPage();
      case 'profile': return profilePage();
      case 'accounts': return S.ai || S.aiPrep ? accountsPage() : overview();
      case 'messages': return messagesPage();
      case 'tickets': return ticketListPage();
      case 'help': return helpPage();
      case 'settings': return settingsPage();
      default: return overview();
    }
  }
  function needConsent() { return S.consentVer != null && S.consentVer < DISCLAIMER_VER; }
  /* 要同意免责声明时整页只有它：公告、新版本、时区这类弹窗和对话框先不出来（状态留着，同意了再按顺序弹）；
     只放行免责声明页自己打开的「用户协议」 */
  function consentGate() { return needConsent() && !(S.dialog && S.dialog.type === 'agreement'); }
  function disclaimerList() { return '<ol class="p-terms">' + DISCLAIMER.map(function (t) { return '<li><div><b>' + t[0] + '</b>' + t[1] + '</div></li>'; }).join('') + '</ol>'; }
  function consentScreen() {
    return '<div class="p-consent"><div class="hx-card p-consentcard"><div class="hd"><h1>免责声明</h1><span>第 ' + DISCLAIMER_VER + ' 版 · 共 ' + DISCLAIMER.length + ' 条，读完后输入下面这句话才能继续使用</span></div>' +
      '<div class="bd">' + disclaimerList() + '<p class="hx-small hx-muted">另见' + btn('text', '《用户协议》', 'dialog:agreement') + '</p></div>' +
      '<label class="hx-field"><span class="hx-label">请输入：<b class="p-consenttxt">' + CONSENT_TEXT + '</b></span><input class="hx-input" id="f-consent" data-keep="consent" autocomplete="off" spellcheck="false" placeholder="在这里一字一字地输入，不能粘贴"></label>' +
      (S.actErr.consent ? '<span class="hx-help is-error">' + I('warning', 16) + S.actErr.consent + '</span>' : '') +
      '<div class="hx-row">' + btn('primary', '同意并继续', 'consentGo') + btn('secondary', '不同意，退出', 'consentNo') + '</div>' +
      '<p class="hx-small hx-muted">不同意只关掉界面；已经在保护中的，防护照常。</p></div></div>';
  }
  function bodyView() {
    if (needConsent()) return '<div class="p-body">' + consentScreen() + '</div>';
    if (S.screen === 'activate') return '<div class="p-body">' + activateScreen() + '</div>';
    if (S.screen === 'wizard') return '<div class="p-body">' + wizardScreen() + '</div>';
    if (S.screen === 'trialStopped') return '<div class="p-body">' + trialStoppedScreen() + '</div>';
    if (S.page === 'tickets' && (S.newTicket || S.ticketSel)) return '<div class="p-body">' + navView() + '<main class="p-main flush' + fx('page', 'tickets') + '">' + ticketsPage() + '</main></div>';
    return '<div class="p-body">' + navView() + '<main class="p-main' + fx('page', S.page) + (S.page === 'overview' ? ' p-ovw' : '') + '" data-scroll="main">' + mainContent() + '</main></div>';
  }

  /* 危险操作先确认（01 §4 第 6 条），默认焦点在「取消」。分两种：会切断防护、暴露真实 IP 的（红底警示、确认后要输电脑密码），
     和不会的（断网几秒、关掉别的程序、删掉东西，写明「防护照常」）。返回 [标题, 说明, 确认按钮的字, 会不会切断防护]；
     这次不用确认就返回空。暂停防护有自己的弹窗（要选时长）。 */
  var CONFIRM = {
    prepPaused: function () { return S.prepPaused ? ['关掉「开通前先断网」？', '关掉后线路开通前照常上网，你访问的网站能看到你的真实 IP。', '关掉', true] : null; },
    updateNow: function () { return ['现在更新？', '更新时会断网几秒（不会走直连），装好后自动接着保护。正在下载或通话的话，可以等会儿再更新。', '立即更新']; },
    quitApp: function () { return ['退出 ' + CONFL.name + '？', '它里面没保存的内容可能会丢，先保存好再退出。', '退出 ' + CONFL.name]; },
    proxyOff: function () { return ['关掉系统代理？', '关掉以后浏览器不再绕到已经退出的代理软件那里，防护照常。之后想改回来，点「撤销」。', '关掉']; },
    lanDel: function (i) { var l = S.lan[+i]; return l ? ['删除 ' + l.ip + '？', '删除后这台电脑访问不到这个地址，要用时再添加。', '删除'] : null; },
    ntCancel: function () {
      var N = S.newTicket, typed = function (id) { var el = root.querySelector('#' + id); return !!(el && el.value.trim()); };
      return N && (N.answer || N.shots && N.shots.length !== 0 || typed('f-ntdesc') || typed('f-ntans')) ? ['放弃这张工单？', '已经写的内容不会保存。', '放弃'] : null;
    }
  };
  var confirmFn = null;
  function confirmFirst(c, fn) { confirmFn = fn; S.dialog = { type: 'confirm', title: c[0], body: c[1], ok: c[2], open: !!c[3] }; render(); }

  /* ---------------- 弹窗 ---------------- */
  function dialogView() {
    var D = S.dialog; if (D && !window.LEYRA_FEATURES.licenseCodes && /switchCode|restoreCode/.test(D.type)) return ''; if (!D) { fx('dlg', ''); return ''; }
    var k = fx('dlg', D.type), h;
    if (D.type === 'quitAll') {
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('warning', 20, 'c-danger') + '彻底退出？</h3>' +
        '<div class="p-pausewarn"><b>退出后，你的真实 IP 会暴露</b><ul><li>防护规则撤掉，整台电脑不走独享线路，所有网站和软件都能看到你的真实 IP 和所在地</li><li>要隐藏 IP 的网站和软件，先退出或关掉再继续</li><li>再打开软件或重启电脑，自动恢复保护</li></ul></div>' +
        '<p class="hx-small hx-muted">只想关窗口的话，用「退出（后台继续保护）」就行。</p>' +
        '<div class="acts">' + btn('danger', I('lock', 14) + '彻底退出', 'quitAllGo') + btn('primary', '取消', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'trialQuit') {
      // 和「彻底退出」一样：红色警告、确认后要电脑密码（01 §4 第 8 步）
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('warning', 20, 'c-danger') + '退出软件？</h3>' +
        '<div class="p-pausewarn"><b>退出以后不再保护，你访问的网站能看到你的真实 IP</b><ul><li>防护规则、浏览器保护设置撤掉，网址解析、代理和时区恢复原样，电脑照常上网</li><li>重启电脑、再打开软件都不再接管网络</li><li>以后买了单独软件再打开就能用，自己的出口和设置都留着</li></ul></div>' +
        
        '<div class="acts">' + btn('danger', I('lock', 14) + '退出软件', 'trialQuitGo') + btn('primary', '取消', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'pause') {
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('warning', 20, 'c-danger') + (D.login ? '暂停防护去登录 Wi-Fi？' : '暂停防护？') + '</h3>' +
        '<div class="p-pausewarn"><b>暂停期间，你的真实 IP 会暴露</b><ul>' +
        '<li>整台电脑不走独享线路，所有网站和软件都能看到你的真实 IP 和所在地</li>' +
        '<li>' + (D.login ? '先关掉浏览器里的其它网页和别的软件，只留登录 Wi-Fi 的页面' : '要隐藏 IP 的网站和软件，先退出或关掉再暂停') + '</li>' +
        '<li>' + (D.login ? '登录完成、线路连上后自动恢复' : '到时间自动恢复，也可以随时点「恢复防护」') + '</li></ul></div>' +
        '' +
        claudeRunning(D) +
        (aiActive() ? '<p><span class="p-danger-note">暂停期间不要登录或使用我们提供的 Claude 账号，已经打开的 Claude 页面也先关掉，否则被封不退</span></p>' : '') +
        (D.login ? '' : '<div class="hx-seg" role="group" aria-label="暂停时长">' + [10, 30, 60].map(function (m) { return '<button aria-pressed="' + (D.min === m) + '" data-act="pauseMin:' + m + '">' + m + ' 分钟</button>'; }).join('') + '</div>') +
        '<div class="acts">' + btn('danger', I('lock', 14) + '暂停 ' + D.min + ' 分钟', 'pauseGo') + btn('primary', '取消', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'confirm') {
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('warning', 20, D.open ? 'c-danger' : 'c-warn') + D.title + '</h3>' +
        (D.open ? '<div class="p-pausewarn"><b>这会切断防护，你的真实 IP 会暴露</b><ul><li>' + D.body + '</li></ul></div>'
          : '<p>' + D.body + '</p><p class="p-safenote">' + I('shield', 14) + '防护照常，不会暴露真实 IP</p>') +
        '<div class="acts">' + btn('danger', (D.open ? I('lock', 14) : '') + D.ok, 'confirmGo') + btn('primary', '取消', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'access') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">输入接入码</h3><p>向客服索取接入码，粘贴后自动重试连接。</p>' +
        '<label class="hx-field"><span class="p-sronly">接入码</span><textarea class="hx-textarea' + (D.err ? ' is-error' : '') + '" data-keep="access" id="f-access" placeholder="粘贴以 CC1- 开头的一行文本" data-autofocus></textarea>' + (D.err ? '<span class="hx-help is-error">' + I('warning', 16) + D.err + '</span>' : '') + '</label>' +
        '<div class="acts">' + btn('secondary', '取消', 'close') + btn('primary', '使用', 'accessUse') + '</div></div>';
    } else if (D.type === 'addexit') {
      var re = D.idx != null ? exitsShown()[D.idx] : null;
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + (re ? '更换「' + esc(re.name) + '」的链接' : '添加出口') + '</h3>' +
        '<p>' + (re ? '粘贴新的链接或订阅地址，换好后这个出口的线路跟着更新。' : '粘贴卖家给你的链接（一行一条）或一行订阅地址，加好后在出口里点它就能切过去。') + '出口好不好用由出口本身决定。</p>' +
        (re ? '' : '<label class="hx-field"><span>名称</span><input class="hx-input" data-keep="exitname" id="f-exitname" placeholder="自己起个名字，例如「我自己的 VPS」" data-autofocus></label>') +
        '<label class="hx-field"><span class="p-sronly">链接或订阅地址</span><textarea class="hx-textarea' + (D.err ? ' is-error' : '') + '" data-keep="exitlinks" id="f-exitlinks" rows="5" placeholder="每行一条链接，或一行订阅地址"' + (re ? ' data-autofocus' : '') + '></textarea>' + (D.err ? '<span class="hx-help is-error">' + I('warning', 16) + D.err + '</span>' : '') + '</label>' +
        (re ? '' : '<label class="p-check lx-direct"><input type="checkbox" data-keep="exitdirect"> 本机直连<span class="p-sub">（卖家的节点只允许国内连接时勾选：由这台电脑直接连，不经香港）</span></label>' + '') +
        '<div class="acts">' + (D.busy ? '<span class="p-sub">正在读链接、下订阅，要几秒…</span>' : btn('text', I('paste', 16) + '粘贴', 'pasteField:exitlinks', ' title="把复制好的链接或订阅地址放进上面的框"') + btn('secondary', '取消', 'close') + btn('primary', re ? '更换' : '添加', 'exitAddGo')) + '</div></div>';
    } else if (D.type === 'ownexit') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + (S.ownExit === 'connected' ? '更换出口' : '接入你的出口') + '</h3>' +
        '<p>粘贴卖家给你的链接（一行一条）或一行订阅地址。出口好不好用由你的出口决定。</p>' +
        '<label class="hx-field"><span class="p-sronly">链接或订阅地址</span><textarea class="hx-textarea' + (D.err ? ' is-error' : '') + '" data-keep="ownexit" id="f-ownexit" rows="5" placeholder="每行一条链接，或一行订阅地址" data-autofocus></textarea>' + (D.err ? '<span class="hx-help is-error">' + I('warning', 16) + D.err + '</span>' : '') + '</label>' +
        '<label class="p-check lx-direct"><input type="checkbox" data-keep="ownexitdirect"> 本机直连<span class="p-sub">（卖家的节点只允许国内连接时勾选：由这台电脑直接连，不经香港）</span></label>' +
        '<div class="acts">' + (D.busy ? '<span class="p-sub">正在接入，读链接、下订阅要几秒…</span>' : btn('text', I('paste', 16) + '粘贴', 'pasteField:ownexit', ' title="把复制好的链接或订阅地址放进上面的框"') + btn('secondary', '取消', 'close') + btn('primary', '接入', 'ownExitUse')) + '</div></div>';
    } else if (D.type === 'restoreCode') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">用原授权码恢复</h3><p>输入这台电脑原来的授权码，接回原来的账户。</p>' +
        '<label class="hx-field"><span class="p-sronly">授权码</span><input class="hx-input' + (D.err ? ' is-error' : '') + '" data-keep="restore-code" id="f-restore-code" placeholder="例如 K7QM-29XF-H4TD-8NWP" data-autofocus>' + (D.err ? '<span class="hx-help is-error">' + I('warning', 16) + D.err + '</span>' : '') + '</label>' +
        '<div class="acts">' + btn('secondary', '取消', 'close') + btn('primary', '恢复', 'restoreUse') + '</div></div>';
    } else if (D.type === 'modeOffer') {
      // 升级后问一次：只让 Claude 走出口（新的默认）还是全部走出口（以前的方式）
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">选择走出口的方式</h3>' +
        '<p>新版本可以<b>只让 Claude 走出口</b>：Claude 桌面版、Claude Code、Claude 网页和它用到的服务都经出口，Claude 看不到你的真实 IP；其他软件照常直连，或走你自己的 VPN，不受影响。出口断了也只有 Claude 断开。</p>' +
        '<p class="p-sub">也可以保持以前的方式：电脑上所有流量都走出口。以后在「设置」里随时能改。</p>' +
        '<div class="acts">' + btn('secondary', '保持全部走出口', 'modeChoose:all') + btn('primary', '只让 Claude 走出口（推荐）', 'modeChoose:claude', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'tz') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('clock', 20, 'c-warn') + '电脑时区和出口不一致</h3><p>' + tzWhere() + '，电脑时区是' + lat(TZN.sys) + '，境外网站可能据此判断你所在的地区。是否把电脑时区改为' + lat(TZN.exit) + '？</p>' +
        '<div class="acts">' + btn('secondary', '暂不', 'tzLater') + btn('primary', '更换时区', 'tzDo', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'browsers') {
      h = '<div class="hx-dialog wide' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">检查浏览器</h3><p>点「检查」会用这个浏览器打开一个本机测试页，几秒就好，看它的 WebRTC 会不会暴露地址。</p>' +
        '<div class="p-browsers">' + (S.browsers.length ? S.browsers.map(function (b, i) {
          return '<div class="p-browser"><span class="hx-dot ' + ({ clean: '', lan: 'warn', real: 'danger', none: 'neutral', testing: 'neutral' })[b.st] + '"></span><div class="t"><b>' + esc(b.n) + '</b><span>' + BROWSER_TEXT[b.st] + '</span></div>' + (b.st === 'testing' ? '' : btn('secondary', b.st === 'none' ? '检查' : '再查一次', 'browserTest:' + i)) + '</div>';
        }).join('') : '<p class="hx-muted">没找到浏览器。</p>') + '</div><div class="acts">' + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'disclaimer') {
      h = '<div class="hx-dialog wide terms' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">免责声明</h3><p>第 ' + DISCLAIMER_VER + ' 版，共 ' + DISCLAIMER.length + ' 条。</p>' + disclaimerList() +
        '<div class="acts">' + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'agreement') {
      h = '<div class="hx-dialog wide terms' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">用户协议</h3><p>共 ' + TERMS.length + ' 条。</p><ol class="p-terms">' + TERMS.map(function (t) { return '<li><div><b>' + t[0] + '</b>' + t[1] + '</div></li>'; }).join('') + '</ol>' +
        '<div class="acts">' + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'diag') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">诊断信息里有什么</h3><p>附在这张工单里，只给客服看，跟着工单删除。</p>' +
        '<ul class="p-diaglist">' + DIAG.map(function (x) { return '<li>' + I('check', 16, 'c-ok') + '<span>' + x + '</span></li>'; }).join('') + '</ul>' +
        '<p class="p-diagno">' + I('lock', 16) + '<span>不含密钥、密码、Claude 账号的登录信息，也不含你访问的网站和域名。</span></p>' +
        '<div class="acts">' + btn('primary', '知道了', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'refund') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">退款规则说明</h3><p>' + TERMS[9][1] + '</p><p>' + TERMS[8][0] + '：' + TERMS[8][1] + '</p>' + (S.ai ? '<p>' + TERMS[11][0] + '：全套里我们提供的 Claude 账号被封，核实后按没用完的天数退会员的钱，软件和线路的钱不退。完整规则见用户协议第 12 条。</p>' : '') +
        '<div class="acts">' + btn('secondary', '查看用户协议', 'dialog:agreement') + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'install') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">安装说明</h3><ol class="p-terms" style="max-height:none">' +
        ['双击安装包，安装时只需确认一次管理员权限。', '如果出现「Windows 已保护你的电脑」，点「更多信息」再点「仍要运行」。', '如果 360 或火绒弹窗，点「允许」。', '升级由软件自动完成，无需操作。', '仍被拦截或无法安装，请截图后提交工单。'].map(function (x) { return '<li><div>' + x + '</div></li>'; }).join('') + '</ol>' +
        '<div class="acts">' + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'aiReport') {
      var a = AIACC.filter(function (x) { return x.id === D.id; })[0];
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">账号登不上了？</h3><p>' + a.plat + ' · ' + a.tier + '（' + a.email + '）。确认后我们会核实账号状态：确认被封的，按没用完的天数退钱，进度通过消息告诉你。</p><p>核实期间先不要在其他地方尝试登录。</p>' +
        '<div class="acts">' + btn('secondary', '取消', 'close') + btn('primary', '确认，帮我核实', 'aiReportGo', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'contact') {
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">联系售后</h3><p>联系时请提供账户编号，续费、加购流量都通过售后办理。</p><div style="margin-bottom:12px">' + channels() + '</div>' + accountField() +
        '<div class="acts">' + btn('secondary', '提交工单', 'newTicket') + btn('primary', '关闭', 'close', ' data-autofocus') + '</div></div>';
    } else if (D.type === 'switchCode') {
      // 续费 / 换授权码：在这台电脑上填一个新的授权码，新码的天数加在原到期日后面。
      h = '<div class="hx-dialog' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">续费</h3>' +
        '<p>在售后 QQ 群下单拿到新的授权码，填在下面。新授权码的天数会加在原来的到期日后面；还是这台电脑，不用重装。</p>' +
        '<label class="hx-field"><span class="hx-label">新的授权码</span><input class="hx-input hx-num" data-keep="switch-code" id="f-switchcode" placeholder="XXXX-XXXX-XXXX-XXXX" autocomplete="off" spellcheck="false"' + (D.busy ? ' disabled' : '') + ' data-autofocus>' +
        (D.err ? '<span class="hx-help is-error" style="display:flex;gap:4px">' + I('warning', 16) + esc(D.err) + '</span>' : '<span class="hx-help">一个授权码只能用一次</span>') + '</label>' +
        '<div class="acts">' + (D.busy ? busy('switchCode') : btn('secondary', '取消', 'close') + btn('primary', '续费', 'switchCodeGo')) + '</div></div>';
    } else if (D.type === 'renew') {
      var rb = !!S.busy.renew;
      h = '<div class="hx-dialog wide' + k + '" role="dialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">已经线下付了钱？</h3><p>付过钱的在这里提交续费申请，客服核对后开通。还没付的' + btn('text', '在网页上续费', 'renewWeb', ' style="padding:0 2px;min-height:0;height:auto"') + '，付完自动顺延，不用提交申请。</p>' +
        '<div>' + channels() + '</div>' +
        '<div class="p-renewsec" role="group" aria-labelledby="rn-what"><span class="hx-label" id="rn-what">续什么</span><div class="p-renewopts">' + renewOpts().map(function (o, i, all) { var f = all.filter(function (x) { return D.pick[x[0]]; })[0] || all[0]; return '<button class="p-opt" aria-pressed="' + !!D.pick[o[0]] + '" data-act="renewPick:' + o[0] + '"' + (o === f ? ' data-autofocus' : '') + (rb ? ' disabled' : '') + '>' + o[1] + '</button>'; }).join('') + '</div>' +
        '<p class="hx-muted">' + (window.CC_RENEW_HELP ? window.CC_RENEW_HELP() : '全套是线路和 Claude 账号一起续，从原到期日按月接着算；流量只在本期有效') + '</p>' +
        (D.err ? '<span class="hx-help is-error">' + I('warning', 16) + D.err + '</span>' : '') + '</div>' +
        '<label class="hx-field" style="margin-top:16px"><span class="hx-label">付款后的订单号或备注（选填）</span><input class="hx-input" data-keep="renew-note" id="f-renewnote"' + (rb ? ' disabled' : '') + '></label>' +
        '<div class="acts">' + (rb ? busy('renew') : btn('secondary', '取消', 'close') + btn('primary', '我已付款，提交续费申请', 'renewSubmit')) + '</div></div>';
    } else if (D.type === 'public') {
      h = '<div class="hx-dialog' + k + '" role="alertdialog" aria-modal="true" aria-labelledby="dlg-t"><h3 id="dlg-t">' + I('warning', 20, 'c-warn') + '添加公网地址？</h3><p><span class="p-danger-note">这个地址将直接看到你的真实 IP</span></p>' +
        '<div class="hx-card" style="padding:8px 12px;margin-bottom:4px"><div class="p-kv"><span>地址</span><span class="v hx-num">' + esc(D.ip) + '</span><span></span></div><div class="p-kv"><span>归属地</span><span class="v">' + D.geo + '</span><span></span></div></div>' +
        '<div class="acts">' + btn('danger', '仍然添加', 'advConfirm') + btn('primary', '取消', 'close', ' data-autofocus') + '</div></div>';
    }
    return '<div class="p-modal" data-act="scrim">' + h + '</div>';
  }
  function toastView() {
    var out = '';
    if (S.warnToast && S.screen === 'main') out += '<div class="hx-toast sticky' + fx('wtoast', S.warnToast) + '" role="status">' + I('warning', 20) + S.warnToast + '<button class="hx-icon-btn x" aria-label="关闭" data-act="warnClose">' + I('close', 16) + '</button></div>';
    else fx('wtoast', '');
    if (S.toast) out += '<div class="hx-toast' + fx('toast', S.toast.id) + '" role="status" data-toast="' + S.toast.id + '">' + I(S.toast.icon, 20, S.toast.icon === 'check' ? 'c-ok' : 'hx-muted') + S.toast.text + '</div>';
    return '<div class="p-toastlayer">' + out + '</div>';
  }

  /* ---------------- 托盘小窗口 ---------------- */
  function redDot() { return unreadMsgs() > 0 || S.tickets.some(function (t) { return t.unread; }); }
  /* 托盘图标和正式软件的同一套（已保护、已断网、已暂停、准备中），由 project/tools/icon/make.py 写进来 */
  var TRAY_SVG = {"ok": "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 32 32\"><defs><linearGradient id=\"tray-ok-f\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#45CC88\"/><stop offset=\".55\" stop-color=\"#2DB873\"/><stop offset=\"1\" stop-color=\"#1E9C5E\"/></linearGradient></defs><path d=\"M4.5 6.4Q4.5 4.4 6.5 4.2L14.8 1.9Q16 1.5 17.2 1.9L25.5 4.2Q27.5 4.4 27.5 6.4L27.5 14.4C27.5 23.31 20.83 28.332 16 30.6C11.17 28.332 4.5 23.31 4.5 14.4Z\" fill=\"url(#tray-ok-f)\"/><circle cx=\"16\" cy=\"14.6\" r=\"4.4\" fill=\"#fff\"/></svg>", "pm": "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 32 32\"><defs><linearGradient id=\"tray-pm-f\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#EC6A62\"/><stop offset=\".55\" stop-color=\"#DD4A42\"/><stop offset=\"1\" stop-color=\"#BE3730\"/></linearGradient></defs><path d=\"M4.5 6.4Q4.5 4.4 6.5 4.2L14.8 1.9Q16 1.5 17.2 1.9L25.5 4.2Q27.5 4.4 27.5 6.4L27.5 14.4C27.5 23.31 20.83 28.332 16 30.6C11.17 28.332 4.5 23.31 4.5 14.4Z\" fill=\"url(#tray-pm-f)\"/><path d=\"M11.4 10L20.6 19.2\" stroke=\"#fff\" stroke-width=\"3\" stroke-linecap=\"round\"/></svg>", "paused": "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 32 32\"><defs><linearGradient id=\"tray-paused-f\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#EBB656\"/><stop offset=\".55\" stop-color=\"#DDA035\"/><stop offset=\"1\" stop-color=\"#BC8423\"/></linearGradient></defs><path d=\"M4.5 6.4Q4.5 4.4 6.5 4.2L14.8 1.9Q16 1.5 17.2 1.9L25.5 4.2Q27.5 4.4 27.5 6.4L27.5 14.4C27.5 23.31 20.83 28.332 16 30.6C11.17 28.332 4.5 23.31 4.5 14.4Z\" fill=\"url(#tray-paused-f)\"/><rect x=\"11.5\" y=\"9.6\" width=\"3.2\" height=\"10\" rx=\"1.2\" fill=\"#fff\"/><rect x=\"17.3\" y=\"9.6\" width=\"3.2\" height=\"10\" rx=\"1.2\" fill=\"#fff\"/></svg>", "prep": "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 32 32\"><defs><linearGradient id=\"tray-prep-f\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#A7A19A\"/><stop offset=\".55\" stop-color=\"#8F8981\"/><stop offset=\"1\" stop-color=\"#77716A\"/></linearGradient></defs><path d=\"M4.5 6.4Q4.5 4.4 6.5 4.2L14.8 1.9Q16 1.5 17.2 1.9L25.5 4.2Q27.5 4.4 27.5 6.4L27.5 14.4C27.5 23.31 20.83 28.332 16 30.6C11.17 28.332 4.5 23.31 4.5 14.4Z\" fill=\"url(#tray-prep-f)\"/><circle cx=\"16\" cy=\"14.6\" r=\"3.7\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2.2\"/></svg>"};
  function trayKind() { if (S.screen === 'trialStopped') return 'prep'; return { ok: 'ok', pm: 'pm', down: 'pm', paused: 'paused' }[S.status] || 'prep'; }
  function trayIconCls() { if (S.screen === 'trialStopped') return 'neutral'; return { ok: 'ok', pm: 'danger', down: 'danger', paused: 'warn' }[S.status] || 'neutral'; }
  function trayView() {
    if (!S.tray) { fx('tray', ''); return ''; }
    // 免费试用停用：只有到期说明和两个动作（01 §4 第 8 步）
    if (S.screen === 'trialStopped') return '<div class="p-traypop"><div class="p-menu' + fx('tray', 'open') + '" role="menu" aria-label="Leyra 状态菜单"><div class="hd neutral"><b><i aria-hidden="true"></i>免费试用已到期</b><span>现在没有保护，电脑照常上网</span></div><hr>' +
      '<button class="mi" role="menuitem" data-act="buySoft">购买单独软件…</button><button class="mi" role="menuitem" data-act="trialExit">退出软件</button></div></div>';
    var st = S.status, hd, acts = [], unread = unreadMsgs(), renewAct = A.renewOpen ? 'renewOpen' : 'renew';
    var ipLine = cityDot() + IP, line = (curExit() ? esc(curExit().name) + ' · ' : '') + routeName(S.route) + (ROUTES[S.route].ms == null ? '' : ' · ' + ROUTES[S.route].ms + ' ms');
    function mi(label, act, sub) { return '<button class="mi" role="menuitem" data-act="' + act + '">' + label + (sub ? '<span class="sub" aria-hidden="true">▸</span>' : '') + '</button>'; }
    if (st === 'ok') { hd = ['ok', '已保护' + (TZN.city ? ' · ' + TZN.city : ''), line]; acts = [mi('暂停防护（会暴露真实 IP）', 'pause', true), mi('切换线路', 'go:routes', true), mi('刷新连接', 'trayRefresh'), mi('安全检查…', 'runCheck')]; }
    else if (st === 'paused' && S.pause && S.pause.kind === 'off') { hd = ['warn', '防护未开启', '你访问的网站能看到你的真实 IP']; acts = [mi('开启防护', 'resume')]; }
    else if (st === 'paused') { hd = ['warn', '防护已暂停 · <span data-mmss>' + H.mmss(S.pause.left) + '</span> 后恢复', '你访问的网站能看到你的真实 IP']; acts = [mi('恢复防护', 'resume')]; }
    else if (st === 'pm') {
      hd = ['danger', '已断网 · ' + PM[S.reason].short, '真实 IP 没有暴露'];
      if (S.reason === 'route') acts = [mi('刷新连接', 'pmRefresh'), mi('换一条线路', 'pmSwitch'), mi('暂停防护去登录 Wi-Fi（会暴露真实 IP）', 'pauseLogin', true)];
      else if (S.reason === 'traffic') acts = [mi('续费…', renewAct), mi('联系客服…', 'contact')];
      else if (S.reason === 'trialExpired') acts = [mi('购买单独软件…', 'buySoft'), mi('退出软件', 'dialog:trialQuit')];
      else if (S.reason === 'expired') acts = [mi('接入你的出口…', 'dialog:ownexit'), mi('重新开通全套…', 'upgrade'), mi('联系客服…', 'contact')];
      else if (S.reason === 'component' || S.reason === 'rules') acts = [mi('重新启动防护', 'restart')];
      else if (S.reason === 'unbound' || S.reason === 'banned' || S.reason === 'settings') acts = [mi('联系客服…', 'contact')];
    }
    else if (st === 'down') { hd = ['danger', '防护没有运行', '真实 IP 可能暴露']; acts = [mi('恢复防护', 'daemonStart')]; }
    else if (st === 'starting') { hd = ['neutral', '启动中', '正在建立独享线路']; }
    else { hd = ['neutral', S.ownExit === 'waiting' ? '准备中 · 还没接出口' : '准备中 · 线路开通中', S.prepPaused ? '已先断网，只能访问局域网' : S.ownExit === 'waiting' ? '接上之前网站能看到你的真实 IP' : '开通前网站能看到你的真实 IP']; }
    if (S.busy.tray) hd[2] = S.busy.tray.text;
    return '<div class="p-traypop"><div class="p-menu' + fx('tray', 'open') + '" role="menu" aria-label="Leyra 状态菜单"><div class="hd ' + hd[0] + '"><b><i aria-hidden="true"></i>' + hd[1] + '</b><span>' + hd[2] + '</span>' + (st === 'ok' || st === 'starting' ? '<span>' + ipLine + '</span>' : '') + '</div>' +
      (acts.length ? '<hr>' + acts.join('') : '') + '<hr>' + mi('消息' + (unread ? ' · ' + unread + ' 条未读' : ''), 'go:messages') + mi('打开主窗口', 'openMain') + '<hr>' + mi('退出（后台继续保护）', 'trayQuit') + mi('彻底退出（会暴露真实 IP）…', 'dialog:quitAll') + '</div></div>';
  }

  /* ---------------- Windows 系统通知（屏幕右下角） ---------------- */
  var NOTES = {
    expire1d: { title: '全套明天到期', body: '点「续费」在网页上付款，付完自动顺延', btns: [['secondary', '续费', 'renewWeb']] },
    renewed: { title: '续费已完成', body: '有效期至 12 月 18 日', btns: [['secondary', '打开主窗口', 'openMain']] },
    update: { title: '新版本 ' + S.newVersion + ' 已就绪', body: '升级约需 10 秒，期间网络暂停。', btns: [['secondary', '立即更新', 'updateNow'], ['secondary', '下次开机时更新', 'updateLater']] },
    provisioned: { title: '线路已开通', body: '线路已开通，正在建立独享线路，完成后自动开始防护。', btns: [['secondary', '打开主窗口', 'openMain']] }
  };
  var noteSeq = 0;
  function sysNotify(title, body) { NOTES.custom = { title: esc(title), body: esc(body), btns: [['secondary', '打开主窗口', 'openMain']] }; notify('custom'); render(); }
  function notify(kind) {
    if (!S.notify) return;
    S.notes = S.notes.filter(function (n) { return n.kind !== kind; });
    var id = ++noteSeq; S.notes.push({ id: id, kind: kind });
    setTimeout(function () { var had = S.notes.length; S.notes = S.notes.filter(function (n) { return n.id !== id; }); if (S.notes.length !== had) render(); }, 7000);
  }
  function unnotify(kind) { S.notes = S.notes.filter(function (n) { return n.kind !== kind; }); }
  function notesView() {
    if (!S.notes.length || S.tray) return '';
    return '<div class="p-notes" aria-live="polite">' + S.notes.map(function (n) {
      var N = NOTES[n.kind];
      return '<div class="p-note' + fx('note' + n.id, 'on') + '" role="status"><div class="app">' + I('shield', 16) + '<span>Leyra</span><button class="hx-icon-btn" aria-label="关闭通知" data-act="noteClose:' + n.id + '">' + I('close', 16) + '</button></div>' +
        '<div class="bd"><b>' + N.title + '</b><p>' + N.body + '</p></div><div class="acts">' + N.btns.map(function (b) { return btn(b[0], b[1], 'noteDo:' + n.id + ':' + b[2]); }).join('') + '</div></div>';
    }).join('') + '</div>';
  }

  /* ---------------- 窗口里的右键菜单 ---------------- */
  function ctxView() {
    var C = S.ctx; if (!C) return '';
    return '<div class="p-ctx" role="menu" style="left:' + C.x + 'px;top:' + C.y + 'px">' + C.items.map(function (it) { return it === '-' ? '<hr>' : '<button role="menuitem" data-act="' + it[1] + '">' + (it[2] ? I(it[2], 16) : '') + it[0] + '</button>'; }).join('') + '</div>';
  }
  function ctxItems(el) {
    var spec = el.getAttribute('data-ctx'), i = spec.indexOf(':'), kind = spec.slice(0, i), arg = spec.slice(i + 1);
    if (kind === 'copy') { S.ctxCopy = arg; return [['复制', 'ctxCopy', 'copy']]; }
    if (kind === 'route') { var k = +arg, fixed = k === S.route && S.routeMode === 'manual'; return [[fixed ? '已固定使用这条' : '固定使用这条', 'pickRoute:' + k, 'signal'], ['恢复自动', 'routeMode:auto', 'refresh'], '-', ['这条线路不好用', 'routeBad', 'warning']]; }
    return null;
  }
  /* 网页自带的右键菜单不要（输入框里的保留）；能复制的东西和线路行给自己的菜单，选中了文字给「复制」 */
  function onContext(e) {
    var field = e.target.closest('input, textarea');
    if (field) { if (!/^(checkbox|radio|range|button|submit)$/.test(field.type)) fieldMenu(e, field); return; }
    e.preventDefault();
    var win = e.target.closest('.p-win'), el = e.target.closest('[data-ctx]'), items = el ? ctxItems(el) : null;
    var selText = window.getSelection ? String(getSelection()).trim() : '';
    if (!items && selText) { S.ctxCopy = selText; items = [['复制', 'ctxCopy', 'copy']]; }
    if (!items || !win) { if (S.ctx) { S.ctx = null; render(); } return; }
    var r = win.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
    S.ctx = { x: Math.max(0, Math.min(x, r.width - 190)), y: Math.max(0, Math.min(y, r.height - items.length * 30 - 16)), items: items };
    render();
  }

  /* 输入框的右键菜单：剪切、复制、粘贴、全选（Windows 关掉了网页自带的菜单，Mac 也用同一套）。
     粘贴由软件自己读剪贴板，不依赖网页的剪贴板权限。 */
  function fieldMenu(e, field) {
    e.preventDefault();
    var win = e.target.closest('.p-win'); if (!win) return;
    var ro = field.readOnly || field.disabled, start = field.selectionStart, end = field.selectionEnd, has = start != null && start !== end;
    S.ctxField = { key: field.getAttribute('data-keep'), el: field, start: start, end: end, text: has ? field.value.slice(start, end) : '' };
    var items = [];
    if (has && !ro) items.push(['剪切', 'ctxCut', 'cut']);
    if (has) items.push(['复制', 'ctxFieldCopy', 'copy']);
    if (!ro) items.push(['粘贴', 'ctxPaste', 'paste']);
    if (items.length) items.push('-');
    items.push(['全选', 'ctxSelectAll', 'selectall']);
    var r = win.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
    S.ctx = { x: Math.max(0, Math.min(x, r.width - 190)), y: Math.max(0, Math.min(y, r.height - items.length * 30 - 16)), items: items };
    render();
  }
  function readClipboard() {
    var T = window.__TAURI__;
    if (T && T.core && T.core.invoke) return T.core.invoke('read_clipboard').then(function (r) { return (r && r.text) || ''; });
    if (navigator.clipboard && navigator.clipboard.readText) return navigator.clipboard.readText();
    return Promise.reject(new Error('no clipboard'));
  }
  // 把文字放进输入框（替换选中的部分），并通知界面内容变了。
  function putText(el, t, start, end) {
    var v = el.value;
    if (start == null || start > v.length) start = end = v.length;
    if (end == null || end < start) end = start;
    var max = el.maxLength > 0 ? el.maxLength : Infinity;
    el.value = (v.slice(0, start) + t + v.slice(end)).slice(0, max);
    var pos = Math.min(start + t.length, el.value.length);
    el.focus(); try { el.setSelectionRange(pos, pos); } catch (e) { }
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }
  function fieldOf(F) { return F ? (F.key ? root.querySelector('[data-keep="' + F.key + '"]') : (F.el && root.contains(F.el) ? F.el : null)) : null; }
  function pasteInto(F) {
    return readClipboard().then(function (t) {
      var el = fieldOf(F); if (!el) return;
      if (!t) { toast('剪贴板里没有文字，先复制卖家给你的链接', 'info'); return; }
      putText(el, t.replace(/\r\n?/g, '\n'), F.start, F.end);
    }, function () { toast('读不到剪贴板，请在输入框里按 ' + (/Mac/.test(navigator.platform) ? '⌘V' : 'Ctrl+V') + ' 粘贴', 'info'); });
  }
  function fieldAct(op) {
    var F = S.ctxField; S.ctxField = null; render();
    var el = fieldOf(F); if (!el) return;
    if (op === 'all') { el.focus(); try { el.select(); } catch (e) { } return; }
    if (op === 'paste') { pasteInto(F); return; }
    if (F.text && navigator.clipboard) { try { navigator.clipboard.writeText(F.text).catch(function () { }); } catch (e) { } }
    if (op === 'cut') putText(el, '', F.start, F.end);
  }

  /* ---------------- 网络恢复工具 ---------------- */
  function rescueView() {
    var R = S.rescue; if (!R) { fx('rescue', ''); return ''; }
    var inner = '<div class="hx-alert danger" role="alert" style="align-items:flex-start">' + I('warning', 20) + '<span class="msg">将解除防护规则并恢复系统网络设置，之后不走独享线路，你访问的网站能看到你的真实 IP。只在软件无法打开、长时间无法联网时使用。</span></div>';
    if (R.step === 'intro') inner += '<div class="hx-row" style="justify-content:flex-end">' + btn('secondary', '关闭', 'rescueClose') + btn('danger', '恢复上网', 'rescueAsk') + '</div>';
    else {
      inner += (R.admin ? '<div class="hx-row" style="justify-content:space-between"><span class="hx-small">第 1 步 · 请求管理员权限</span><span class="hx-small c-ok" style="display:inline-flex;gap:4px;align-items:center">' + I('check', 16) + '已获得</span></div>' : busy('rescue') + '<p class="hx-small hx-muted">系统会弹出用户账户控制窗口，请点「是」</p>');
      if (R.admin) {
        inner += '<div class="hx-steplist">' + [['解除防护规则', '正在解除防护规则'], ['恢复系统网络设置', '正在恢复系统网络设置'], ['验证连接', '正在验证连接']].map(function (s, i) {
          if (i < R.done) return '<div class="hx-stepi"><span class="ic">' + I('check', 16, 'c-ok') + '</span><div class="t"><b>' + s[0] + '</b></div><span class="st">已完成</span></div>';
          if (i === R.done) return '<div class="hx-stepi run"><span class="ic"><span class="hx-spin" style="color:var(--accent)"></span></span><div class="t"><b>' + s[1] + '</b></div><span class="st"></span></div>';
          return '<div class="hx-stepi wait"><span class="ic"><span class="hx-ring-wait"></span></span><div class="t"><b>' + s[0] + '</b></div><span class="st">等待</span></div>';
        }).join('') + '</div>';
      }
      if (R.done >= 3) inner += '<p class="hx-small hx-muted">如果软件还在，回到软件点「恢复防护」或重启电脑；如果已卸载，电脑之后一直直接上网。</p>' +
        '<div class="p-toastlayer"><div class="hx-toast sticky' + fx('rtoast', 'on') + '" role="status">' + I('warning', 20) + '<span style="flex:1">已恢复上网。现在不走独享线路，网站能看到你的真实 IP。打开软件即可恢复防护。</span></div></div>' +
        '<div class="hx-row" style="justify-content:flex-end">' + btn('secondary', '关闭', 'rescueClose') + '</div>';
    }
    var dlg = R.confirm ? '<div class="p-modal"><div class="hx-dialog fx" role="alertdialog" aria-modal="true" aria-labelledby="rq-t"><h3 id="rq-t">' + I('warning', 20, 'c-warn') + '恢复上网？</h3><p>将解除防护规则并恢复系统网络设置，之后不走独享线路，你访问的网站能看到你的真实 IP。</p><div class="acts">' + btn('danger', '恢复上网', 'rescueGo') + btn('primary', '取消', 'rescueNo', ' data-autofocus') + '</div></div></div>' : '';
    return '<div class="p-rescue"><div class="hx-win win11' + fx('rescue', 'open') + '" style="position:relative"><div class="w11-bar"><span>网络恢复工具</span><div class="w11-caps" aria-hidden="true"><span>' + capIcon('min') + '</span><span class="cls">' + capIcon('close') + '</span></div></div><div class="in">' + inner + '</div>' + dlg + '</div></div>';
  }

  /* ---------------- 原型演示条 ---------------- */
  function dbtn(label, act, on, cls) { return '<button data-act="' + act + '"' + (on != null ? ' aria-pressed="' + !!on + '"' : '') + (cls ? ' class="' + cls + '"' : '') + '>' + label + '</button>'; }
  function demoView() { return ''; }
  function unusedLegacyDemoView() {
    var st = S.status, r = S.reason, inMain = S.screen === 'main';
    var pmOn = function (x) { return inMain && st === 'pm' && r === x; };
    var c = S.check, running = c.phase === 'run' && !c.fast;
    return '<section class="p-demo" aria-label="原型演示"><div class="ttl">' + I('info', 16) + '<b>原型演示</b><span>正式软件里没有这一条，用来切换状态和流程</span></div>' +
      '<div class="grp"><span>状态</span><div class="btns">' +
      dbtn('已保护', 'demo:ok', inMain && st === 'ok') + dbtn('启动中', 'demo:starting', inMain && st === 'starting' && !S.starting.slow) + dbtn('启动中 · 超过 20 秒', 'demo:startingSlow', inMain && st === 'starting' && S.starting.slow) + dbtn('防护没有运行', 'demo:down', inMain && st === 'down') + '<span class="sep"></span><span class="sub">已断网</span>' +
      [['原因确认中', 'checking'], ['全套已到期', 'expired'], ['本月流量已用完', 'traffic'], ['线路暂时不可用', 'route'], ['线路维护中', 'node'], ['本设备已解绑', 'unbound'], ['服务维护中', 'service'], ['防护组件未就绪', 'component'], ['账户已停用', 'banned'], ['独享线路更新中', 'rotated'], ['防护规则没装上', 'rules'], ['防护设置待更新', 'settings']].map(function (x) { return dbtn(x[0], 'demo:pm:' + x[1], pmOn(x[1])); }).join('') +
      '<span class="sep"></span><span class="sub">防护已暂停</span>' + dbtn('用户', 'demo:pauseUser', inMain && st === 'paused' && S.pause.kind === 'user') + dbtn('Wi-Fi 登录', 'demo:pauseWifi', inMain && st === 'paused' && S.pause.kind === 'wifi') +
      '<span class="sep"></span><span class="sub">准备中</span>' + dbtn('默认', 'demo:prep', inMain && st === 'prep' && !S.prepPaused) + dbtn('先断网', 'demo:prepPaused', inMain && st === 'prep' && S.prepPaused) + '</div></div>' +
      '<div class="grp"><span>流程</span><div class="btns">' + dbtn('免责声明（第一次打开）', 'demo:consent', needConsent()) + dbtn('从激活开始', 'demo:activate', S.screen === 'activate') + dbtn('首次向导', 'demo:wizard', S.screen === 'wizard') + dbtn('模拟：开通完成', 'demo:provisioned') + dbtn('模拟：线路已连上', 'demo:connected') + dbtn('倒计时快进到 5 秒', 'demo:ff5') + dbtn('安全检查快进', 'demo:checkff', null, running ? 'is-hint' : '') + '</div></div>' +
      '<div class="grp"><span>提示</span><div class="btns">' + dbtn('公告 · 普通', 'demo:banner:normal', S.banner === 'normal') + dbtn('公告 · 重要', 'demo:banner:important', S.banner === 'important') + dbtn('公告 · 维护通知', 'demo:banner:maint', S.banner === 'maint') + dbtn('到期前 1 天', 'demo:expire1d', S.alerts.expire1d) + dbtn('新版本已下载', 'demo:update', S.alerts.update) + dbtn('冲突 · 查出是谁', 'demo:conflict:named', S.alerts.conflict && !S.conflictFixed && CONFL.kind === 'named') + dbtn('冲突 · 代理没关', 'demo:conflict:stale', S.alerts.conflict && !S.conflictFixed && CONFL.kind === 'stale') + dbtn('冲突 · 查不出', 'demo:conflict:unknown', S.alerts.conflict && !S.conflictFixed && CONFL.kind === 'unknown') + dbtn('个人中心 · 到期方式一', 'demo:profileMode', S.profileMode === 'split') +
      '<span class="sep"></span><span class="sub">续费</span>' + dbtn('即将到期', 'demo:renewSoon', S.alerts.expireSoon && !S.renewDone.node) + dbtn('续费处理中', 'demo:renewWait', S.renew.phase === 'processing') + dbtn('需要补充信息', 'demo:renewInfo', S.renew.phase === 'needinfo') + dbtn('模拟：续费完成', 'demo:renewDone') +
        '<span class="sep"></span><span class="sub">单独软件</span>' + dbtn('等接出口', 'demo:ownWaiting', S.ownExit === 'waiting') + dbtn('已接上自带出口', 'demo:ownConnected', S.ownExit === 'connected') + dbtn('不是（全套）', 'demo:ownOff', !S.ownExit) +
        '<span class="sep"></span><span class="sub">多个出口</span>' + dbtn('老套餐 · 分配的 + 自己加的', 'demo:exitsOld', S.exits === 'old') + dbtn('单独软件 · 自己加的几个', 'demo:exitsSoft', S.exits === 'soft') + dbtn('全套 · 只有一个出口', 'demo:exitsOff', !S.exits) + '</div></div>' +
      '<div class="grp"><span>免费试用</span><div class="btns">' + [['on12', '试用中（还剩 12 天）', S.trial === 'on' && S.trialEnd.days === 12], ['on3', '还剩 3 天', S.trial === 'on' && S.trialEnd.days === 3], ['on1', '明天到期', S.trial === 'on' && S.trialEnd.days === 1], ['expired', '到期断网', S.trial === 'expired' && S.screen === 'main'], ['stopped', '停用', S.screen === 'trialStopped'], ['converted', '已转正', false]].map(function (x) { return dbtn(x[1], 'demo:trial:' + x[0], x[2]); }).join('') + '</div></div>' +
      '<div class="grp"><span>账号与消息</span><div class="btns">' + dbtn('有 Claude 账号', 'demo:ai', S.ai) + dbtn('密码号 · 显示登录信息', 'demo:aishow', S.ai && !!S.aiShown) +
      '<span class="sep"></span><span class="sub">登录 Claude</span>' + [['wait', '等待中'], ['ok', '已打开'], ['timeout', '没等到'], ['error', '取不到']].map(function (x) { return dbtn(x[1], 'demo:ailogin:' + x[0], S.ai && !!S.aiLogin && S.aiLogin.phase === x[0]); }).join('') +
      '<span class="sep"></span><span class="sub">我的账号</span>' + AI_DEMOS.map(function (x) { return dbtn(x[1], 'demo:aist:' + x[0], S.ai && S.aiDemo === x[0]); }).join('') + dbtn('暂停中', 'demo:aipause', S.ai && S.status === 'paused' && S.page === 'accounts') + '<span class="sep"></span>' + dbtn('Pro 能升到 Max 5x', 'demo:tierup', S.tierUp) + dbtn('没归到网站账户', 'demo:nobal', S.balance == null) + dbtn('没有动态', 'demo:noact', !S.hist.events.length) +
      '<span class="sep"></span><span class="sub">未读消息</span>' + [0, 2, 4].map(function (n) { return dbtn(n + ' 条', 'demo:unread:' + n, unreadMsgs() === n); }).join('') +
      '<span class="sep"></span>' + dbtn('弹窗 · 需要处理的消息', 'demo:popMsg', !!(S.popup && S.popup.lv === 'action')) + dbtn('弹窗 · 维护公告', 'demo:popAnn', !!(S.popup && S.popup.kind)) + '</div></div>' +
      '<div class="grp"><span>外观</span><div class="btns">' + dbtn('跟随系统', 'appearance:system', S.appearance === 'system') + dbtn('浅色', 'appearance:light', S.appearance === 'light') + dbtn('深色', 'appearance:dark', S.appearance === 'dark') + '</div></div>' +
      '<div class="grp"><span>窗口</span><div class="btns">' + dbtn('960 × 620', 'winW:960', S.winW === 960) + dbtn('860 × 560 最小', 'winW:860', S.winW === 860) + dbtn('1200 × 720', 'winW:1200', S.winW === 1200) + '<span class="sep"></span>' + dbtn('托盘菜单', 'demo:tray', S.tray) + dbtn('网络恢复工具', 'rescueToggle', !!S.rescue) + '</div></div></section>';
  }

  /* ---------------- 渲染 ---------------- */
  var root, renderSeq = 0;
  // 输入法拼写中（中文还没上屏）不重画：整页重画会打断拼写，字就打不进去。上屏后补画一次。
  var composing = false, renderLater = false;
  document.addEventListener('compositionstart', function () { composing = true; }, true);
  document.addEventListener('compositionend', function () { composing = false; if (renderLater) { renderLater = false; setTimeout(render, 0); } }, true);
  // 鼠标按着时先不重画：按下和松开之间整页换掉，这一下点击就丢了（App 每秒刷新状态都会重画）。
  // 松开、点击处理完再补画；拖窗口这类收不到松开的，最多压 1 秒。
  var pressAt = 0, pressLater = false;
  function pressDone() { if (!pressAt) return; pressAt = 0; if (pressLater) { pressLater = false; setTimeout(render, 0); } }
  document.addEventListener('pointerdown', function (e) { if (e.button !== 0) return; var t = pressAt = Date.now(); setTimeout(function () { if (pressAt === t) pressDone(); }, 1000); }, true);
  ['pointerup', 'pointercancel'].forEach(function (k) { document.addEventListener(k, pressDone, true); });
  window.addEventListener('blur', pressDone);
  var lastHtml = null;
  // 在工单的回复框、问题描述里粘贴截图：当成加了一张图（真客户端里 A.replyShot / A.ntShot 收到的是文件）。
  document.addEventListener('paste', function (e) {
    if (e.target && e.target.id === 'f-consent') { e.preventDefault(); S.actErr.consent = '这句话要自己输入，不能粘贴'; render(); return; }
    var el = e.target && e.target.closest && e.target.closest('[data-keep]'), k = el && el.getAttribute('data-keep');
    if (!k || !(k.indexOf('reply-') === 0 || k === 'nt-desc') || S.ticketImages === false || !e.clipboardData) return;
    var f = Array.prototype.filter.call(e.clipboardData.files || [], function (x) { return /^image\//.test(x.type); })[0];
    if (!f) return;
    e.preventDefault();
    if (k === 'nt-desc') A.ntShot(f); else A.replyShot(f);
  });
  function render() {
    if (!root) return;
    if (composing) { renderLater = true; return; }
    if (pressAt) { pressLater = true; return; }
    var seq = ++renderSeq;
    nextKeys = {};
    // 保留输入、焦点和滚动
    var keep = {}, focus = null, sel = null, scroll = {};
    Array.prototype.forEach.call(root.querySelectorAll('[data-keep]'), function (el) { keep[el.getAttribute('data-keep')] = el.type === 'checkbox' ? el.checked : el.value; });
    var ae = document.activeElement;
    if (ae && ae.hasAttribute && ae.hasAttribute('data-keep')) { focus = ae.getAttribute('data-keep'); try { sel = [ae.selectionStart, ae.selectionEnd]; } catch (e) { } }
    else if (ae && ae.getAttribute && ae.getAttribute('data-act') && root.contains(ae)) focus = 'act:' + ae.getAttribute('data-act');
    Array.prototype.forEach.call(root.querySelectorAll('[data-scroll]'), function (el) { scroll[el.getAttribute('data-scroll') + ':' + S.page] = el.scrollTop; });
    var adv = root.querySelector('[data-adv]'); if (adv) S.advOpen = adv.open;
    Array.prototype.forEach.call(root.querySelectorAll('[data-grp]'), function (d) { S.grpOpen[d.getAttribute('data-grp')] = d.open; });
    Array.prototype.forEach.call(root.querySelectorAll('[data-faq]'), function (d) { S.faqOpen[d.getAttribute('data-faq')] = d.open; });
    var prevPop = lastKeys.popup, prevDlg = lastKeys.dlg, prevRescueConfirm = root.querySelector('.p-rescue .p-modal') ? 1 : 0;

    document.body.setAttribute('data-theme', themeId());
    var trayCls = trayIconCls();
    var html =
      trayView() +
      '<div class="p-desk"><div class="p-stage"><div class="hx-win win11 p-win" role="application" aria-label="Leyra 主窗口"' + winStyle() + '><div class="w11-bar"><span class="ttl">Leyra</span>' + '<div class="w11-caps" aria-hidden="true"><span>' + capIcon('min') + '</span><span>' + capIcon('max') + '</span><span class="cls">' + capIcon('close') + '</span></div>' + '</div>' +
      bodyView() + (consentGate() ? '' : dialogView()) + (needConsent() ? '' : popupView()) + toastView() + lightboxView() + ctxView() + '</div></div>' + demoView() + '</div>' + rescueView() + notesView() +
      '<div class="p-taskbar" role="toolbar" aria-label="任务栏"><span class="tb-more" aria-hidden="true">' + I('chevron', 16) + '</span><button class="hx-trayicon ' + trayCls + '" aria-label="Leyra 托盘图标" aria-expanded="' + S.tray + '" data-act="trayToggle">' + TRAY_SVG[trayKind()] + (redDot() ? '<span class="p-reddot" aria-hidden="true"></span>' : '') + '</button><span class="tb-clock"><span data-clock>' + hhmm(new Date()) + '</span><span data-date>' + ymd(new Date()) + '</span></span></div>';
    lastKeys = nextKeys;
    var commit = function () {
    if (seq !== renderSeq) return;
    // 和上一次一模一样就不动：鼠标停着的按钮、选中的字都还在。
    if (html === lastHtml) return;
    lastHtml = html;
    // 换页时整页重画（保留换页动效）；同一页里只改变化的部分。以前每秒整窗重画，悬停闪、动画重播、点击偶尔落空，用起来一卡一卡。
    if (lastPage !== S.page || !root.firstChild) { lastPage = S.page; root.innerHTML = html; }
    else morphHtml(root, html);

    Array.prototype.forEach.call(root.querySelectorAll('[data-keep]'), function (el) { var k = el.getAttribute('data-keep'); if (k in keep) { if (el.type === 'checkbox') el.checked = keep[k]; else el.value = keep[k]; } });
    Array.prototype.forEach.call(root.querySelectorAll('[data-scroll]'), function (el) { var k = el.getAttribute('data-scroll') + ':' + S.page; if (k in scroll) el.scrollTop = scroll[k]; else if (el.getAttribute('data-scroll') === 'thread') el.scrollTop = el.scrollHeight; });
    var newDlg = S.dialog && prevDlg !== lastKeys.dlg, newRescue = S.rescue && S.rescue.confirm && !prevRescueConfirm;
    if (S.popup && prevPop !== lastKeys.popup) { var pf = root.querySelector('.p-popup [data-autofocus]'); if (pf) { pf.focus(); focus = null; } }
    if (newDlg || newRescue) { var af = root.querySelector((newRescue ? '.p-rescue ' : '.p-win ') + '[data-autofocus]'); if (af) af.focus(); }
    else if (focus) {
      var f = focus.indexOf('act:') === 0 ? root.querySelector('[data-act="' + focus.slice(4) + '"]') : root.querySelector('[data-keep="' + focus + '"]');
      if (f) { f.focus({ preventScroll: true }); if (sel && f.setSelectionRange) try { f.setSelectionRange(sel[0], sel[1]); } catch (e) { } }
    }
    };
    // 换页不用 startViewTransition：过渡那 0.2 秒里点击全落到整页上（白点），看到的又是截图，位置对不上。换页动效是 .p-main.fx。
    commit();
  }
  var lastPage = null;
  // 把 html 套到现有节点上：同位置同标签的节点保留，只改属性和文字；不同的才替换。事件都挂在 root/document 上，节点保留不影响点击。
  function morphHtml(target, html) {
    var t = document.createElement('template'); t.innerHTML = html; morphChildren(target, t.content);
  }
  function morphChildren(from, to) {
    var fc = from.firstChild, tc = to.firstChild;
    while (tc) {
      var tnext = tc.nextSibling;
      if (!fc) { from.appendChild(tc); tc = tnext; continue; }
      var fnext = fc.nextSibling;
      if (fc.nodeType === tc.nodeType && (fc.nodeType !== 1 || fc.tagName === tc.tagName)) {
        if (fc.nodeType === 1) morphNode(fc, tc);
        else if (fc.nodeValue !== tc.nodeValue) fc.nodeValue = tc.nodeValue;
      } else from.replaceChild(tc, fc);
      fc = fnext; tc = tnext;
    }
    while (fc) { var n = fc.nextSibling; from.removeChild(fc); fc = n; }
  }
  function morphNode(from, to) {
    var i, fa = from.attributes, ta = to.attributes;
    for (i = fa.length - 1; i >= 0; i--) if (!to.hasAttribute(fa[i].name)) from.removeAttribute(fa[i].name);
    for (i = 0; i < ta.length; i++) if (from.getAttribute(ta[i].name) !== ta[i].value) from.setAttribute(ta[i].name, ta[i].value);
    if (from.tagName === 'INPUT' && !from.hasAttribute('data-keep') && from !== document.activeElement) {
      if (from.type === 'checkbox' || from.type === 'radio') from.checked = to.hasAttribute('checked');
      else from.value = to.getAttribute('value') || '';
    }
    if (from.tagName === 'TEXTAREA' || from.tagName === 'SELECT') { if (from.outerHTML !== to.outerHTML) from.parentNode.replaceChild(to, from); return; }
    morphChildren(from, to);
  }
  function winStyle() { return ' style="width:' + S.winW + 'px;height:' + (S.winW === 860 ? 560 : S.winW === 1200 ? 720 : 620) + 'px"'; }
  function patchCountdown() {
    if (!S.pause) return;
    var left = S.pause.left, total = S.pause.total;
    Array.prototype.forEach.call(document.querySelectorAll('[data-mmss]'), function (e) { e.textContent = H.mmss(left); });
    Array.prototype.forEach.call(document.querySelectorAll('.hx-sg.warn [data-hx-arc]'), function (a) { var len = +a.getAttribute('data-hx-arc'); a.setAttribute('stroke-dashoffset', (len * (1 - left / total)).toFixed(2)); });
  }
  function patchProg() {
    var c = S.check; if (c.phase !== 'run') return;
    var pc = document.querySelector('[data-prog-pct]'); if (pc) pc.textContent = progPct(c);
    var a = document.querySelector('[data-prog-arc]');
    if (a) { var len = +a.getAttribute('data-prog-arc'); a.setAttribute('stroke-dashoffset', (len * (1 - checkFrac(c))).toFixed(1)); }
    Array.prototype.forEach.call(document.querySelectorAll('[data-prog-left]'), function (e) { e.textContent = checkRemaining(c); });
    var al = document.querySelector('[data-audit-left]'); if (al) al.textContent = H.mmss(Math.max(0, stageDur(c, c.idx) - c.t));
  }

  /* ---------------- 交互 ---------------- */
  function val(key) { var el = root.querySelector('[data-keep="' + key + '"]'); return el ? (el.type === 'checkbox' ? el.checked : el.value.trim()) : ''; }
  function setVal(key, v) { var el = root.querySelector('[data-keep="' + key + '"]'); if (el) el.value = v; }
  function go(page) {
    S.screen = 'main'; S.page = page; S.tray = false; S.dialog = null;
    if (page === 'tickets') { var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0]; if (t && !S.newTicket) t.unread = false; }
    render();
  }
  function ipParts(ip) { var m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip); if (!m) return null; var p = m.slice(1).map(Number); return p.every(function (x) { return x <= 255; }) ? p : null; }
  function isPrivate(p) { return p[0] === 10 || (p[0] === 172 && p[1] >= 16 && p[1] <= 31) || (p[0] === 192 && p[1] === 168); }

  var A = {
    go: function (p) {
      if (p === 'check' && (S.check.ctx === 'wizard' || S.check.phase === 'result')) S.check = { phase: 'idle' };
      if (p === 'tickets') S.ticketSel = null;
      go(p);
    },
    screen: function (s) { S.screen = s; S.dialog = null; render(); },
    dialog: function (t) { S.tray = false; S.dialog = { type: t }; render(); },
    close: function () { S.dialog = null; render(); },
    scrim: function () { },
    dismiss: function (what) { if (what === 'banner') S.banner = null; else if (what === 'proxyUndo') S.proxyUndo = false; else S.alerts[what] = false; render(); },
    pause: function () { openPause(false); },
    pauseLogin: function () { openPause(true); },
    pauseMin: function (m) { S.dialog.min = +m; render(); },
    confirmGo: function () { var f = confirmFn; confirmFn = null; S.dialog = null; render(); if (f) f(); },
    pauseGo: function () { startPause(S.dialog.min, S.dialog.login); },
    resume: function () { S.tray = false; resume(); },
    warnClose: function () { S.warnToast = null; render(); },
    contact: function () { A.dialog('contact'); },
    openWeb: function () { toast('已交给默认浏览器，没弹出来请检查默认浏览器设置', 'external'); },
    newTicket: function () { S.dialog = null; S.newTicket = { step: 'type', type: null, answer: null, shots: 0 }; S.actErr = {}; go('tickets'); },
    daemonStart: function () { run('daemon', [['正在请求管理员权限', 1], ['正在启动防护', 1.5]], function () { S.status = 'ok'; toast('防护组件已启动'); }); },
    restart: function () { run('status', [['正在建立独享线路', 2], ['正在校验线路完整性', 1.5]], function () { S.status = 'ok'; }); },
    pmRefresh: function () { run('status', [['正在重新评估线路质量', 2.5]]); },
    pmSwitch: function () { var n = (S.route + 1) % 3; run('status', [['正在切换至线路 ' + ROUTES[n].n, 2]], function () { S.route = n; }); },
    refreshAny: function () { if (S.status === 'pm') { go('overview'); A.pmRefresh(); } else { go('routes'); A.routeRefresh(); } },
    runCheck: function () { startCheck(false, 'main'); },
    restoreUse: function () {
      if (!val('restore-code')) { S.dialog.err = '请输入原来的授权码'; render(); return; }
      S.dialog = null; S.screen = 'main'; S.page = 'overview'; startStarting({ hold: false }); toast('已恢复原来的账户');
    },
    accessUse: function () {
      var v = val('access');
      if (!/^CC1-\S+/.test(v)) { S.dialog.err = v ? '接入码应以 CC1- 开头，请向客服确认' : '请粘贴客服发来的接入码'; render(); return; }
      S.dialog = null;
      if (S.screen === 'activate') { toast('接入码已保存，正在重试连接'); return; }
      if (S.status === 'pm' || S.status === 'starting') { S.screen = 'main'; S.page = 'overview'; startStarting({ hold: false }); toast('接入码已保存，正在重试连接'); }
      else toast('接入码已保存');
    },
    // 接入自带出口（01 §5.2）：服务端读链接、登记、推回线路；第一次接上就开启防护
    ownExitUse: function () {
      var v = val('ownexit');
      if (!v) { S.dialog.err = '请粘贴卖家给你的链接或订阅地址'; render(); return; }
      var first = S.ownExit !== 'connected';
      S.dialog = null; S.ownExit = 'connected';
      if (first) { S.screen = 'main'; S.page = 'overview'; startStarting({ hold: false }); }
      toast('出口接上了：2 条线路');
    },
    // 升级到全套：一键登录官网，打开升级商品（付款在网页上）
    // 免费试用：「购买」一键登录官网，打开单独软件的购买弹窗、预选这台电脑；付款后直接转成永久的单独软件
    buySoft: function () { S.tray = false; S.dialog = null; render(); toast('已在网页上打开购买，选这台电脑付款后直接换上', 'external'); },
    // 换授权码（01 §3.1「试用换成买的」）：原型里换成单独软件
    switchCodeGo: function () {
      var c = val('switch-code');
      if (!c) { S.dialog.err = '先填授权码'; render(); return; }
      S.dialog.busy = true; S.dialog.err = '';
      var up = !S.trial;
      run('switchCode', [['正在换授权码', 1.5]], function () {
        // 单独软件电脑填全套码：换成全套，线路开通前照常用自己的出口
        if (up) { S.dialog = null; S.page = 'profile'; setTimeout(function () { toast('已换成全套，线路开通后自动连上'); }, 0); return; }
        S.dialog = null; S.trial = ''; S.screen = 'main'; S.page = 'profile'; setStatus('ok');
        setTimeout(function () { toast('已换成单独软件，永久使用'); }, 0);
      });
    },
    // 退出软件（试用到期）：撤掉防护规则、恢复网址解析、代理和时区，界面退出；之后再打开只显示到期页
    trialQuitGo: function () { S.dialog = null; S.trial = 'stopped'; S.screen = 'trialStopped'; render(); toast('原型演示：已撤掉防护、界面退出；再打开软件是这一页', 'info'); },
    trialExit: function () { S.tray = false; render(); toast('原型演示：界面退出，不接管网络', 'info'); },
    upgrade: function () { if (S.dialog && S.dialog.type === 'switchCode') S.dialog = null; render(); toast('已在网页上打开「升级到全套」，付款后自动换成独享线路', 'external'); },
    // 续费（01 §5.7）：一键登录官网自助续费（余额或在线付款，付完自动顺延）；线下付过钱的在续费面板提交申请
    renewWeb: function () { S.tray = false; if (S.dialog && S.dialog.type === 'renew') S.dialog = null; render(); toast('已在网页上打开「续费」，付款后自动顺延，不用再提交申请', 'external'); },
    // Pro 全套升到 Max 5x：一键登录官网，打开升档、预选这台电脑
    tierUp: function () { toast('已在网页上打开「升到 Max 5x」，补差价后升档，到期日不变', 'external'); },
    /* 线路 */
    routeMode: function (m) { S.routeMode = m; render(); },
    /* 出口（01 §5.2「多个出口」）：切换出口 IP 跟着变、防护不断；自己加的能改链接、删除 */
    pickExit: function (i) {
      i = +i; var L = exitsShown(), e = L[i], was = L[S.exit];
      if (i === S.exit) { toast('正在用「' + e.name + '」'); return; }
      run('route', [['正在切换到出口「' + e.name + '」', 2]], function () {
        S.exit = i; S.route = 0; IP = e.ip;
        S.hist.events.unshift({ time: hhmm(new Date()), text: '切换到出口「' + e.name + '」' });
        setTimeout(function () { toast('已切到「' + e.name + '」，出口 IP 变为 ' + e.ip + (was && was.city !== e.city ? '，所在地变成' + e.city : '')); }, 0);
      });
    },
    exitAdd: function () { S.dialog = { type: 'addexit' }; render(); },
    exitRelink: function (i) { S.dialog = { type: 'addexit', idx: +i }; render(); },
    exitAddGo: function () {
      var D = S.dialog, re = D.idx != null ? exitsShown()[D.idx] : null, name = re ? re.name : val('exitname'), links = val('exitlinks');
      if (!links) { D.err = '请粘贴链接或订阅地址'; render(); return; }
      D.busy = true; D.err = ''; render();
      setTimeout(function () {
        S.dialog = null;
        if (re) { re.n = 3; toast('「' + re.name + '」的链接换好了：3 条线路'); return; }
        EXITS.push({ name: name || '我的出口 ' + (exitsShown().filter(function (e) { return e.own; }).length + 1), own: true, city: '美国·纽约', ip: '192.0.2.140', n: 2, ms: 102, sig: 3 });
        toast('出口加好了：2 条线路，点它就能切过去');
      }, 1500);
    },
    exitDel: function (i) {
      i = +i; var e = exitsShown()[i], cur = i === S.exit;
      confirmFirst(['删除出口「' + e.name + '」？', '这个出口的线路从列表里去掉，链接要用时再加。' + (cur ? '你正在用它，会先切到另一个出口。' : ''), '删除'], function () {
        var keep = exitsShown()[cur ? (i === 0 ? 1 : 0) : S.exit];
        EXITS.splice(EXITS.indexOf(e), 1); S.exit = exitsShown().indexOf(keep); if (cur) { S.route = 0; IP = keep.ip; }
        toast('已删除「' + e.name + '」' + (cur ? '，切到了「' + keep.name + '」' : ''));
      });
    },
    pickRoute: function (i) {
      i = +i; S.routeMode = 'manual';
      if (i === S.route) { toast('已固定使用线路 ' + ROUTES[i].n); return; }
      run('route', [['正在切换至线路 ' + ROUTES[i].n, 2]], function () { S.route = i; setTimeout(function () { toast('已切换至线路 ' + ROUTES[i].n + '，已固定使用'); }, 0); });
    },
    routeRefresh: function () { run('route', [['正在重新评估线路质量', 2.5]], function () { setTimeout(function () { toast('线路质量已重新评估'); }, 0); }); },
    routeBad: function () { var n = (S.route + 1) % 3; run('route', [['正在切换至线路 ' + ROUTES[n].n, 2]], function () { S.route = n; setTimeout(function () { toast('已换到线路 ' + ROUTES[n].n + '，已告诉我们这条线路不好用'); }, 0); }); },
    /* 测速（01 §5.2）：只测当前线路，同一条 10 分钟内只真测一次 */
    speedTest: function () {
      if (S.speedAt && Date.now() - S.speedAt < 600000) { toast('10 分钟内测过了，下次 ' + hhmm(new Date(S.speedAt + 600000)) + ' 后再测', 'info'); return; }
      S.speedTesting = true; render();
      setTimeout(function () {
        var r = ROUTES[S.route]; S.speedTesting = false; S.speedAt = Date.now(); r.speed = { mbps: 74, ago: 0, measured: true };
        S.hist.events.unshift({ time: hhmm(new Date()), text: '测速：线路 ' + r.n + ' 下载 74 Mbps' }); toast('线路 ' + r.n + ' 下载 74 Mbps');
      }, 3000);
    },
    /* 安全检查 */
    checkStart: function () { startCheck(false, 'main'); },
    checkDeep: function () { startCheck(true, 'main'); },
    checkCancel: function () { if (S.check.ctx === 'wizard') { S.check = { phase: 'idle' }; S.wizard.step = 2; } else S.check = { phase: 'idle' }; render(); },
    checkAgain: function () { startCheck(S.check.deep, 'main'); },
    checkHist: function (i) { var h = S.checkHist[+i]; if (!h) return; S.check = { phase: 'result', ctx: 'main', hist: +i, deep: h.kind === 'deep' }; render(); },
    checkBack: function () { S.check = { phase: 'idle' }; render(); },
    syncTime: function () { S.timeSynced = true; toast('时间已同步'); },
    quitApp: function () {
      run('quit', [['已请求 ' + CONFL.name + ' 退出', 3, true]], function () { S.conflictFixed = true; });
      toast('已请求 ' + CONFL.name + ' 退出');
    },
    proxyOff: function () { S.conflictFixed = true; S.proxyUndo = true; toast('已关掉系统代理'); },
    proxyUndo: function () { S.proxyUndo = false; S.conflictFixed = false; toast('系统代理已照原样改回'); },
    /* 查不出是谁：打开新建工单，类型「无法上网」，描述里预填看到的东西，附上诊断信息 */
    conflictTicket: function () {
      S.dialog = null; S.actErr = {};
      S.newTicket = { step: 'desc', type: 0, answer: '', shots: 0, diag: true, desc: '冲突软件提示认不出是什么：' + CONFL.msg.replace(/<[^>]*>/g, '') + (CONFL.evidence ? '（看到的：' + CONFL.evidence + '）' : '') };
      go('tickets');
    },
    /* 个人中心 */
    contactEdit: function () { S.editingContact = true; render(); var f = root.querySelector('#f-contact'); if (f) { f.focus(); f.select(); } },
    contactCancel: function () { S.editingContact = false; render(); },
    ckind: function (key, x) { var el = root.querySelector('[data-keep="' + key + '"]'); if (el) el.value = x; render(); },
    contactSave: function () { var v = val('contact'); if (!v) return; S.contact = val('contact-kind') + ' ' + v; S.editingContact = false; toast('联系方式已保存'); },
    /* 我的账号：登录 Claude（01 §5.12）。原型里等几秒就当登录邮件到了；演示面板能切没等到、取不到 */
    aiGuard: function (id) { S.aiGuardOpen = S.aiGuardOpen === id ? null : id; render(); },
    aiLogin: function (id) {
      if (S.status === 'paused') return;
      aiLoginStop(); S.aiLogin = { id: id, phase: 'wait', left: AI_LOGIN_SECS }; render();
      toast('已在浏览器打开 Claude 登录页', 'external');
      aiLoginTimer = setInterval(function () {
        var L = S.aiLogin; if (!L || L.phase !== 'wait') { clearInterval(aiLoginTimer); return; }
        L.left--;
        if (L.left <= AI_LOGIN_SECS - 6 && !L.hold) return A.aiLoginDone('ok');
        if (L.left <= 0) return A.aiLoginDone('timeout');
        var t = root.querySelector('[data-ailogin-left]'), b = root.querySelector('[data-ailogin-bar]');
        if (t) t.textContent = mmssLeft(L.left); if (b) b.style.width = Math.round(L.left * 100 / AI_LOGIN_SECS) + '%';
      }, 1000);
    },
    aiLoginDone: function (phase) {
      clearInterval(aiLoginTimer); if (!S.aiLogin) return;
      S.aiLogin.phase = phase; render();
      // 成功：几秒后回到平时
      if (phase === 'ok') { var id = S.aiLogin.id; setTimeout(function () { if (S.aiLogin && S.aiLogin.id === id && S.aiLogin.phase === 'ok') { S.aiLogin = null; render(); } }, 4000); }
    },
    aiLoginCancel: function () { aiLoginStop(); render(); },
    /* 密码号「显示登录信息」：守护程序经服务端现取（服务端每次记一笔时间、设备和保护状态），AI_SHOW_SECS 秒后自动收起；暂停着不给看 */
    aiShow: function (id) {
      if (S.status === 'paused') return;
      clearTimeout(aiHideTimer); S.aiShown = id; render();
      aiHideTimer = setTimeout(function () { if (S.aiShown === id) { S.aiShown = null; render(); } }, AI_SHOW_SECS * 1000);
    },
    aiHide: function () { clearTimeout(aiHideTimer); S.aiShown = null; render(); },
    // 「看怎么算的」：一键登录官网，打开封号退款计算器、按这台电脑的真实记录填好（去向 refund_calc）
    aiCalc: function () { toast('已在网页上打开「封号退款计算器」，按这台电脑的记录填好了', 'external'); },
    aiReport: function (id) { S.dialog = { type: 'aiReport', id: id }; render(); },
    // 号跟着全套一起续（01 §3.1）：和「续费」一样去官网续全套
    aiRenew: function () { A.renewWeb(); },
    // 补号（01 §3.1）：一键登录官网，打开补号的购买弹窗
    aiRefill: function () { toast('已在网页上打开「补号」，买好后新号会出现在这里', 'external'); },
    aiReportGo: function () {
      var id = S.dialog.id, a = AIACC.filter(function (x) { return x.id === id; })[0];
      S.dialog = null; S.aiReported[id] = true; S.aiGuardOpen = id;
      S.tickets = S.tickets.filter(function (t) { return t.id !== 'T-1060'; });
      S.tickets.unshift({ id: 'T-1060', title: '退款 / 封号包赔', st: 'open', msgs: [{ who: '你', t: '刚刚', x: '账号登不上了：' + a.plat + ' · ' + a.tier + '（' + a.email + '）' }] });
      MSGS.unshift({ id: 'M-' + (1006 + MSGS.length), lv: 'normal', title: '封号退款：已受理', body: '已收到，正在核实账号状态。核实完会再发消息告诉你结果。', t: '刚刚', link: ['ticket', '查看进度'], read: false });
      toast('已收到，正在核实账号状态');
    },
    /* 消息：读了就告诉服务端（断网时先记本机，连上补报） */
    msgTab: function (t) { S.msgTab = t; S.msgOpen = null; render(); },
    annShow: function (id) { S.msgTab = 'ann'; S.msgOpen = id; A.go('messages'); },
    msgOpen: function (id) {
      S.msgOpen = S.msgOpen === id ? null : id;
      MSGS.forEach(function (m) { if (m.id === id) m.read = true; }); render();
    },
    msgReadAll: function () { MSGS.forEach(function (m) { m.read = true; }); toast('已全部标为已读'); },
    msgGo: function (kind, target) {
      if (kind === 'renewal') { A.renewWeb(); return; }
      var t = S.tickets.filter(function (x) { return x.id === 'T-1060'; })[0] || S.tickets.filter(function (x) { return x.id === 'T-1032'; })[0];
      S.newTicket = null; if (t) { S.ticketSel = t.id; t.unread = false; } go('tickets');
    },
    consentGo: function () {
      var v = (val('consent') || '').replace(/\s/g, '');
      if (v !== CONSENT_TEXT) { S.actErr.consent = '要一字不差地输入：' + CONSENT_TEXT; render(); return; }
      S.actErr.consent = null; S.consentVer = DISCLAIMER_VER; toast('已确认');
    },
    consentNo: function () { toast('已退出（原型演示：正式软件里界面关掉）'); },
    popUpdateNow: function () { A.popAck(); A.updateNow(); },
    popUpdateLater: function () { A.popAck(); A.updateLater(); },
    popAck: function () {
      var P = S.popup; if (P && P.msg) P.msg.read = true;
      S.popup = S.popQueue.shift() || null; render();
    },
    popGo: function () {
      var P = S.popup; A.popAck();
      if (P && P.link) { if (P.link[0] === 'ticket' || P.link[0] === 'renewal') A.msgGo(P.link[0], P.link[2]); else if (P.link[0] === 'refill') A.aiRefill(); else if (P.link[0] === 'upgrade') A.upgrade(); else A.openWeb(); }
    },
    /* 工单 */
    ticketBack: function () { S.ticketSel = null; render(); },
    tFilter: function (f) { S.tFilter = f; render(); },
    ticket: function (id) { S.newTicket = null; S.ticketSel = id; var t = S.tickets.filter(function (x) { return x.id === id; })[0]; t.unread = false; render(); },
    reply: function () {
      var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0], v = val('reply-' + t.id), shots = S.replyShots[t.id] || [];
      if (!v && !shots.length) { var f = root.querySelector('#f-reply'); if (f) f.focus(); return; }
      setVal('reply-' + t.id, ''); t.msgs.push({ who: '你', t: '刚刚', x: v, imgs: shots }); t.st = 'open'; S.replyShots[t.id] = [];
      if (t.id === S.renew.ticket && S.renew.phase === 'needinfo') S.renew.phase = 'processing';
      toast('已发送');
      var th = root.querySelector('[data-scroll="thread"]'); if (th) th.scrollTop = th.scrollHeight;
    },
    rate: function (r) { var t = S.tickets.filter(function (x) { return x.id === S.ticketSel; })[0]; t.rated = r; if (r === '未解决') t.st = 'open'; render(); },
    ntType: function (i) { var N = S.newTicket; N.type = +i; N.answer = ''; N.step = ntSteps(TTYPES[N.type])[1]; S.actErr = {}; render(); },
    ntQNext: function () {
      var N = S.newTicket, T = TTYPES[N.type];
      if (T.shot && S.ticketImages !== false) { if (!N.shots) { S.actErr = { nt: '请至少添加 1 张弹窗截图' }; render(); return; } N.answer = '已附截图'; }
      else { var v = val('nt-ans'); if (!v) { S.actErr = { nt: '请先回答这个问题' }; render(); return; } N.answer = v; }
      S.actErr = {}; N.step = 'desc'; render();
    },
    tzHelp: function () { if (!TZN.exit) toast('还不知道出口所在的时区，暂时没法比对', 'info'); else if (S.tzChanged) toast('电脑时区已和出口一致 · ' + TZN.exit); else A.dialog('tz'); },
    ntNext: function (s) { S.newTicket.step = s; S.actErr = {}; render(); },
    ntShot: function () { var N = S.newTicket, l = N.previews = N.previews || []; if (l.length < 3) l.push(demoShot(l.length + 1)); N.shots = l.length; render(); },
    ntShotDel: function (i) { var N = S.newTicket; N.previews = (N.previews || []).filter(function (_, k) { return k !== +i; }); N.shots = N.previews.length; render(); },
    ntShotOpen: function (i) { lightbox(S.newTicket.previews || [], +i); },
    replyShot: function () { var l = S.replyShots[S.ticketSel] = S.replyShots[S.ticketSel] || []; if (l.length < 3) l.push(demoShot(l.length + 1)); render(); },
    replyShotDel: function (i) { S.replyShots[S.ticketSel] = (S.replyShots[S.ticketSel] || []).filter(function (_, k) { return k !== +i; }); render(); },
    replyShotOpen: function (i) { lightbox(S.replyShots[S.ticketSel] || [], +i); },
    imgOpen: function (tid, rest) {
      var t = S.tickets.filter(function (x) { return x.id === tid; })[0], p = String(rest).split(':'), m = t && t.msgs[+p[0]];
      if (!m || !m.imgs) return;
      var shown = m.imgs.slice(0, +p[1]).filter(Boolean).length;
      lightbox(m.imgs, shown);
    },
    lbGo: function (d) { var L = S.lightbox; if (!L) return; L.i = (L.i + (+d) + L.list.length) % L.list.length; render(); },
    lbClose: function () { S.lightbox = null; render(); },
    quitAllGo: function () { S.dialog = null; render(); toast('原型演示：撤掉防护、退出软件'); },
    browserTest: function (i) { var b = S.browsers[+i]; if (!b) return; b.st = 'testing'; render(); setTimeout(function () { b.st = 'clean'; render(); }, 1500); },
    ntCancel: function () { S.newTicket = null; S.actErr = {}; render(); },
    ntDiag: function () { if (S.newTicket) { S.newTicket.diag = S.newTicket.diag === false; render(); } },
    helpDiag: function () { A.newTicket(); S.newTicket.diag = true; render(); },
    guideOpen: function () { toast('已在浏览器中打开', 'external'); },
    ntSubmit: function () {
      var d = val('nt-desc'); if (!d) { S.actErr = { nt: '请简单描述一下问题' }; render(); return; }
      var T = TTYPES[S.newTicket.type];
      S.tickets.unshift({ id: 'T-1047', title: T.n, st: 'open', msgs: [{ who: '你', t: '刚刚', x: (T.q && (!T.shot || S.ticketImages === false) ? '【' + T.q + ' ' + S.newTicket.answer + '】' : '') + d, imgs: S.newTicket.previews || [], diag: S.newTicket.diag !== false }] });
      S.tickets = S.tickets.filter(function (t, i, a) { return a.findIndex(function (x) { return x.id === t.id; }) === i; });
      S.ticketSel = 'T-1047'; S.newTicket = null; S.actErr = {}; setVal('nt-desc', ''); toast('工单 #T-1047 已提交');
    },
    /* 设置 */
    appearance: function (a) { S.appearance = a; render(); },
    prepPaused: function () { S.prepPaused = !S.prepPaused; render(); },
    notify: function () { S.notify = !S.notify; render(); },
    tzRestore: function () { S.tzChanged = false; toast('已恢复为' + lat(TZN.sys)); },
    tzChange: function () { if (TZN.exit) A.dialog('tz'); },
    tzDo: function () { S.tzChanged = true; S.dialog = null; if (S.wizard) S.wizard.tz = 'changed'; toast('电脑时区已改为' + lat(TZN.exit)); },
    tzLater: function () { S.dialog = null; if (S.wizard) S.wizard.tz = 'kept'; render(); },
    wizTzRestore: function () { S.tzChanged = false; S.wizard.tz = 'kept'; toast('已恢复为' + lat(TZN.sys)); },
    lanDel: function (i) { var l = S.lan.splice(+i, 1)[0]; S.lanEdit = -1; S.lanMsg = null; toast('已删除 ' + l.ip); },
    lanNote: function (i) { S.lanEdit = +i; render(); var f = root.querySelector('#f-lanedit'); if (f) f.focus(); },
    lanNoteSave: function (i) { S.lan[+i].note = val('lanedit'); S.lanEdit = -1; render(); },
    advAdd: function () {
      var ip = val('advip'), p = ipParts(ip); S.advOpen = true;
      if (!p) { S.advMsg = '地址格式不对，应为 4 段数字，例如 203.0.113.50'; render(); return; }
      if (isPrivate(p)) { S.advMsg = '局域网地址都已内置，不用添加'; render(); return; }
      S.advMsg = null; S.dialog = { type: 'public', ip: ip, note: val('advnote'), geo: ip.indexOf('203.0.113.') === 0 ? '日本 · ' + TZN.city : '[归属地查询结果]' }; render();
    },
    advConfirm: function () { var D = S.dialog; S.lan.push({ ip: D.ip, note: D.note, pub: true }); S.dialog = null; setVal('advip', ''); setVal('advnote', ''); toast('已添加公网地址 ' + D.ip); },
    checkUpdate: function () { if (S.alerts.update) { go('overview'); return; } toast('当前已是最新版本 ' + S.version, 'info'); },
    updateNow: function () {
      unnotify('update');
      run('update', [['正在校验新版本完整性', 1.5], ['正在部署', 2], ['正在恢复防护', 1.5]], function () { S.version = S.newVersion; S.alerts.update = false; setTimeout(function () { toast('已更新到 ' + S.newVersion); }, 0); });
    },
    updateLater: function () { unnotify('update'); S.alerts.update = false; toast('将在下次开机时更新', 'clock'); },
    /* 激活 */
    activate: function () {
      var code = val('act-code').toUpperCase(), contact = val('act-contact'), agree = val('act-agree'), e = {};
      if (!/^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(code)) e.code = '授权码应为 16 位，例如 K7QM-29XF-H4TD-8NWP';
      if (!contact) e.contact = '请填写联系方式，微信 / QQ / 邮箱都可以';
      if (!agree) e.agree = '请先阅读并同意用户协议';
      S.actErr = e; if (Object.keys(e).length) { render(); return; }
      S.contact = contact;
      run('act', [['正在验证授权码', 1.5], ['正在绑定本设备', 1.5], ['正在申请独享线路', 2]], function () {
        S.actErr = {}; S.screen = 'main'; S.page = 'overview'; clearTransient(); S.status = 'prep'; S.pause = null; S.warnToast = null;
        setTimeout(function () { toast('已激活，线路开通中'); }, 0);
      });
    },
    /* 向导 */
    wizNext: function () {
      var W = S.wizard; W.step++;
      if (W.step === 1 && !S.tzChanged) { S.dialog = { type: 'tz' }; }
      if (W.step === 1 && S.tzChanged) W.tz = 'changed';
      if (W.step === 3) { S.lanMsg = null; startCheck(false, 'wizard'); return; }
      render();
    },
    wizDone: function () { S.wizard = null; S.check = { phase: 'idle' }; S.screen = 'main'; S.page = 'overview'; toast('首次设置已完成'); },
    /* 托盘 */
    trayToggle: function () { S.tray = !S.tray; render(); },
    trayRefresh: function () { run('tray', [['正在重新评估线路质量', 2]]); },
    trayQuit: function () { S.tray = false; toast('界面已退出，防护照常运行', 'info'); },
    ctxCut: function () { fieldAct('cut'); },
    ctxFieldCopy: function () { fieldAct('copy'); },
    ctxPaste: function () { fieldAct('paste'); },
    ctxSelectAll: function () { fieldAct('all'); },
    // 弹窗里「粘贴」按钮：把剪贴板里的链接放进输入框
    pasteField: function (key) { var el = root.querySelector('[data-keep="' + key + '"]'); if (el) pasteInto({ key: key, start: el.selectionStart, end: el.selectionEnd }); },
    ctxCopy: function () { var t = S.ctxCopy; S.ctxCopy = null; if (t && navigator.clipboard) { try { navigator.clipboard.writeText(t).catch(function () { }); } catch (e) { } } toast('已复制'); },
    winW: function (w) { S.winW = +w; render(); },
    openMain: function () { S.tray = false; render(); var w = root.querySelector('.p-win'); if (w) { w.scrollIntoView({ block: 'center', behavior: 'smooth' }); } },
    /* 续费 */
    renew: function () { if (S.renew.phase) { A.renewView(); return; } S.tray = false; S.dialog = { type: 'renew', pick: S.status === 'pm' && S.reason === 'traffic' ? { traffic: true } : renewFirst(), err: null }; render(); },
    renewPick: function (k) {
      var D = S.dialog, on = !D.pick[k];
      D.pick[k] = on; D.err = null; render();
    },
    renewSubmit: function () {
      var D = S.dialog;
      if (!renewNames(D.pick).length) { D.err = '请选择要续费的项目'; render(); return; }
      var note = val('renew-note'), pick = {}; renewOpts().forEach(function (o) { if (D.pick[o[0]]) pick[o[0]] = true; });
      run('renew', [['正在提交申请', 1.5]], function () {
        if (S.dialog === D) S.dialog = null;
        var at = hhmm(new Date());
        S.renew = { phase: 'processing', at: at, pick: pick, ticket: RENEW_ID };
        renewTicket(pick, note, at);
        setTimeout(function () { toast('续费申请已提交'); }, 0);
      });
    },
    renewView: function () { ensureRenew(); S.newTicket = null; S.ticketSel = RENEW_ID; go('tickets'); },
    renewComplete: function () {
      var pick = S.renew.pick || { all: true }, t = renewTicketObj();
      if (renewDur(pick)) { S.renewDone.node = true; S.exp = { date: '12 月 18 日', days: 53 }; S.alerts.expireSoon = false; }
      if (pick.traffic) S.renewDone.traffic = true;
      var body = renewDur(pick) ? '有效期至 12 月 18 日' : '流量已加购';
      var full = body + '。';
      if (t) { t.msgs.push({ who: '客服', t: '刚刚', x: '续费已开通。' + full }); t.st = 'done'; t.unread = true; }
      S.renew = { phase: null, at: null, pick: null, ticket: null };
      S.alerts.expire1d = false; unnotify('expire1d');
      if (S.dialog && S.dialog.type === 'renew') { cancelRun('renew'); S.dialog = null; }
      NOTES.renewed.body = body; notify('renewed');
      if (S.status === 'pm' && S.reason === 'traffic' && pick.traffic) { S.screen = 'main'; S.page = 'overview'; startStarting({ hold: false }); }
      else render();
    },
    /* 系统通知 */
    noteClose: function (id) { S.notes = S.notes.filter(function (n) { return n.id !== +id; }); render(); },
    noteDo: function (id, act) {
      S.notes = S.notes.filter(function (n) { return n.id !== +id; });
      if (act === 'contact') { S.screen === 'main' && go('overview'); A.contact(); }
      else if (act === 'updateNow') { S.screen = 'main'; S.page = 'overview'; A.updateNow(); }
      else if (act === 'updateLater') A.updateLater();
      else if (act === 'renew') { S.screen = 'main'; S.page = 'overview'; A.renew(); }
      else if (act === 'renewWeb') A.renewWeb();
      else A.openMain();
    },
    /* 网络恢复工具 */
    rescueOpen: function () { S.rescue = { step: 'intro', admin: false, done: 0, confirm: false }; render(); },
    rescueToggle: function () { if (S.rescue) A.rescueClose(); else A.rescueOpen(); },
    rescueClose: function () { cancelRun('rescue'); S.rescue = null; render(); },
    rescueAsk: function () { S.rescue.confirm = true; render(); },
    rescueNo: function () { S.rescue.confirm = false; render(); },
    rescueGo: function () {
      var R = S.rescue; R.confirm = false; R.step = 'run';
      run('rescue', [['正在请求管理员权限', 2]], function () {
        R.admin = true;
        (function next() { if (S.rescue !== R) return; render(); if (R.done < 3) setTimeout(function () { if (S.rescue !== R) return; R.done++; next(); }, 1800); })();
      });
    },
    /* 演示条 */
    demo: function (what, arg) {
      var resetScreen = { ok: 1, starting: 1, startingSlow: 1, pm: 1, pauseUser: 1, pauseWifi: 1, prep: 1, prepPaused: 1, banner: 1, expire1d: 1, update: 1, renewSoon: 1, renewWait: 1, renewInfo: 1, renewDone: 1 };
      if (resetScreen[what] || what === 'activate') { S.wizard = null; if (S.check.ctx === 'wizard') S.check = { phase: 'idle' }; }
      if (resetScreen[what]) S.screen = 'main';
      if (what !== 'trial' && what !== 'tray' && S.screen === 'trialStopped') S.screen = 'main';
      if (what === 'tray') S.tray = !S.tray;
      switch (what) {
        case 'ok': S.dialog = null; S.page = 'overview'; setStatus('ok'); break;
        case 'starting': S.dialog = null; S.page = 'overview'; setStatus('starting'); break;
        case 'down': S.dialog = null; S.page = 'overview'; setStatus('down'); break;
        case 'startingSlow': S.dialog = null; S.page = 'overview'; setStatus('starting'); S.starting.t0 = Date.now() - 21000; S.starting.slow = true; render(); break;
        case 'pm': S.dialog = null; S.page = 'overview'; setStatus('pm', arg); break;
        case 'pauseUser': S.dialog = null; S.page = 'overview'; setStatus('ok'); startPause(30, false); break;
        case 'pauseWifi': S.dialog = null; S.page = 'overview'; setStatus('pm', 'route'); startPause(10, true); break;
        case 'prep': S.dialog = null; S.page = 'overview'; S.prepPaused = false; setStatus('prep'); break;
        case 'prepPaused': S.dialog = null; S.page = 'overview'; S.prepPaused = true; setStatus('prep'); break;
        case 'ownWaiting': S.trial = ''; S.dialog = null; S.screen = 'main'; S.page = 'overview'; S.ownExit = 'waiting'; S.ai = false; S.aiPrep = ''; S.alerts.expire1d = false; S.alerts.expireSoon = false; S.prepPaused = false; setStatus('prep'); break;
        case 'ownConnected': S.trial = ''; S.dialog = null; S.screen = 'main'; S.page = 'routes'; S.ownExit = 'connected'; S.ai = false; S.aiPrep = ''; S.alerts.expire1d = false; S.alerts.expireSoon = false; setStatus('ok'); break;
        case 'exitsOld': S.trial = ''; S.dialog = null; S.screen = 'main'; S.page = 'routes'; S.ownExit = ''; S.ai = false; S.aiPrep = ''; S.exits = 'old'; S.exit = 0; S.route = 0; IP = EXITS[0].ip; setStatus('ok'); break;
        case 'exitsSoft': S.trial = ''; S.dialog = null; S.screen = 'main'; S.page = 'routes'; S.ownExit = 'connected'; S.ai = false; S.aiPrep = ''; S.exits = 'soft'; S.exit = 0; S.route = 0; IP = exitsShown()[0].ip; setStatus('ok'); break;
        case 'exitsOff': S.exits = ''; S.exit = 0; S.ownExit = ''; S.ai = true; IP = '203.0.113.24'; render(); break;
        case 'ownOff': S.ownExit = ''; S.trial = ''; S.ai = true; render(); break;
        case 'activate': S.dialog = null; S.actErr = {}; S.screen = 'activate'; render(); break;
        case 'wizard': case 'provisioned':
          if (what === 'provisioned' && S.status !== 'prep') { toast('当前不在准备中，已直接进入首次向导', 'info'); }
          if (what === 'provisioned') notify('provisioned');
          S.dialog = null; S.screen = 'wizard'; S.wizard = { step: 0, tz: S.tzChanged ? 'changed' : 'ask' };
          S.tzChanged = false; S.wizard.tz = 'ask';
          clearTransient(); S.pause = null; S.warnToast = null; S.status = 'starting'; S.starting = { stage: 0, t0: Date.now(), slow: false, hold: false };
          render(); finishStarting(2000); break;
        case 'connected': lineConnected(); break;
        case 'ff5': if (S.pause) { S.pause.left = 5; patchCountdown(); render(); } else toast('当前没有进行中的倒计时', 'info'); break;
        case 'checkff': fastForward(); break;
        case 'banner': S.banner = arg; S.page = 'overview'; render(); break;
        case 'expire1d': S.alerts.expire1d = !S.alerts.expire1d; if (S.alerts.expire1d) { S.exp = { date: '11 月 18 日', days: 1 }; S.renewDone.node = false; notify('expire1d'); } else unnotify('expire1d'); S.page = 'overview'; render(); break;
        case 'consent': S.consentVer = needConsent() ? DISCLAIMER_VER : 0; S.actErr.consent = null; render(); break;
        case 'update': S.alerts.update = !S.alerts.update; if (S.alerts.update) updatePopup(); if (S.alerts.update) notify('update'); else unnotify('update'); S.page = 'overview'; render(); break;
        case 'conflict': S.screen = 'main'; S.proxyUndo = false; if (S.alerts.conflict && !S.conflictFixed && CONFL.kind === (arg || 'named')) { S.alerts.conflict = false; } else { var cd = CONFL_DEMO[arg || 'named']; for (var ck in cd) CONFL[ck] = cd[ck]; S.alerts.conflict = true; S.conflictFixed = false; } if (S.page !== 'check') S.page = 'overview'; render(); break;
        case 'renewSoon':
          if (S.alerts.expireSoon && !S.renewDone.node && !S.renew.phase) { S.alerts.expireSoon = false; }
          else { S.alerts.expireSoon = true; S.exp = { date: '11 月 18 日', days: 7 }; S.renew = { phase: null, at: null, pick: null, ticket: null }; S.renewDone = { node: false, traffic: false }; S.tickets = S.tickets.filter(function (t) { return t.id !== RENEW_ID; }); }
          if (S.page !== 'profile') S.page = 'overview'; S.dialog = null; render(); break;
        case 'renewWait': ensureRenew(); S.renew.phase = 'processing'; S.dialog = null; if (S.page !== 'profile' && S.page !== 'tickets') S.page = 'overview'; render(); break;
        case 'renewInfo':
          ensureRenew(); S.renew.phase = 'needinfo'; S.dialog = null;
          var rt = renewTicketObj(), ask = '未查到付款，请提供订单号或付款截图。';
          if (rt && rt.msgs[rt.msgs.length - 1].x !== ask) { rt.msgs.push({ who: '客服', t: '今天 15:10', x: ask }); }
          if (rt) { rt.st = 'wait'; rt.unread = !(S.page === 'tickets' && S.ticketSel === RENEW_ID); }
          if (S.page !== 'profile' && S.page !== 'tickets') S.page = 'overview'; render(); break;
        case 'renewDone': A.renewComplete(); break;
        case 'tray': render(); break;
        case 'ai': S.ai = !S.ai; S.aiPrep = ''; aiLoginStop(); clearTimeout(aiHideTimer); S.aiShown = null; S.screen = 'main'; S.page = S.ai ? 'accounts' : 'overview'; render(); break;
        case 'tierup': S.tierUp = !S.tierUp; S.screen = 'main'; S.page = 'profile'; render(); break;
        case 'nobal': S.balance = S.balance == null ? '128.00' : null; S.screen = 'main'; S.page = 'profile'; render(); break;
        case 'noact': S.hist.events = S.hist.events.length ? [] : EVENTS0.slice(); S.screen = 'main'; S.page = 'overview'; render(); break;
        // 登录 Claude 的几种样子（01 §5.12）：等待中（停住不自动到）、已打开、没等到、取不到
        case 'ailogin': aiDemo('ok'); var la = AIACC.filter(aiLive)[0].id;
          if (arg === 'wait') { S.aiLogin = { id: la, phase: 'wait', left: 112, hold: true }; render(); }
          else { S.aiLogin = { id: la, phase: arg, left: 0 }; render(); }
          break;
        case 'aist': aiDemo(arg); break;
        // 密码号显示着登录信息（01 §5.12「显示登录信息」）；再点收起
        case 'aishow': if (S.aiShown) { A.aiHide(); break; } aiDemo('password'); A.aiShow(AIACC.filter(aiLive)[0].id); break;
        // 保护暂停着：「登录 Claude」灰着，上方提示暂停期间别登录
        case 'aipause': aiDemo('ok'); startPause(30, false); S.page = 'accounts'; render(); break;
        case 'trial': trialDemo(arg); break;
        case 'unread':
          var n = +arg; MSGS.forEach(function (m, i) { m.read = i >= n; }); render(); break;
        case 'popMsg':
          var m = MSGS.filter(function (x) { return x.lv === 'action'; })[0]; m.read = false; S.screen = 'main';
          showPopup({ id: m.id, lv: 'action', title: m.title, body: m.body, link: m.link, msg: m });
          if (S.notify) sysNotify(m.title, '打开软件查看');
          break;
        case 'popAnn':
          var an = ANNS[0]; S.screen = 'main'; showPopup({ id: an.id, kind: an.kind, title: an.title, body: an.body });
          if (S.notify) sysNotify(an.title, '维护期间网络断开');
          break;
      }
    }
  };

  function onClick(e) {
    var hadCtx = !!S.ctx; if (hadCtx) S.ctx = null;
    var el = e.target.closest('[data-act]'); if (!el || !root.contains(el) || el.disabled) { if (hadCtx) render(); return; }
    var act = el.getAttribute('data-act');
    if (act === 'scrim') { if (e.target === el) { /* 点遮罩不关闭，避免误操作 */ } return; }
    var i = act.indexOf(':'), name = i < 0 ? act : act.slice(0, i), rest = i < 0 ? '' : act.slice(i + 1);
    var j = rest.indexOf(':'), a1 = j < 0 ? rest : rest.slice(0, j), a2 = j < 0 ? undefined : rest.slice(j + 1);
    var c = CONFIRM[name] && CONFIRM[name](a1, a2);
    if (c) { confirmFirst(c, function () { A[name](a1, a2); }); return; }
    if (A[name]) A[name](a1, a2);
  }

  /* 定时器：倒计时、启动超时、检查进度、时钟 */
  setInterval(function () {
    if (S.status === 'paused' && S.pause && S.pause.kind !== 'off' && !S.busy.resume) {
      S.pause.left = Math.max(0, S.pause.left - 1); patchCountdown();
      if (S.pause.left === 0) resume(true);
    }
    if (S.status === 'starting' && S.starting && S.starting.hold && !S.starting.slow && Date.now() - S.starting.t0 > 20000) { S.starting.slow = true; render(); }
    var ck = document.querySelector('[data-clock]'); if (ck) ck.textContent = hhmm(new Date());
    var dt = document.querySelector('[data-date]'); if (dt) dt.textContent = ymd(new Date());
  }, 1000);
  setInterval(checkTick, 200);

  document.addEventListener('keydown', function (e) {
    if (S.lightbox && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) { A.lbGo(e.key === 'ArrowLeft' ? -1 : 1); return; }
    if (e.key !== 'Escape') return;
    if (S.lightbox) { A.lbClose(); return; }
    if (S.ctx) { S.ctx = null; render(); return; }
    if (S.popup) { A.popAck(); return; }
    if (S.rescue && S.rescue.confirm) { S.rescue.confirm = false; render(); return; }
    if (S.dialog) { S.dialog = null; render(); return; }
    if (S.tray) { S.tray = false; render(); }
  });
  document.addEventListener('click', function (e) {
    if (S.tray && !e.target.closest('.p-traypop') && !e.target.closest('[data-act="trayToggle"]') && !e.target.closest('.p-demo')) { S.tray = false; render(); return; }
  }, true);
  if (window.matchMedia) { var mq = matchMedia('(prefers-color-scheme: dark)'); var onMq = function () { if (S.appearance === 'system') render(); }; if (mq.addEventListener) mq.addEventListener('change', onMq); else if (mq.addListener) mq.addListener(onMq); }

  document.addEventListener('DOMContentLoaded', function () {
    root = document.getElementById('app');
    root.addEventListener('click', onClick);
    root.addEventListener('contextmenu', onContext);
    render();
  });
  window.__proto = { S: S, render: render, A: A, toast: toast, ROUTES: ROUTES, PM: PM, EXITS: EXITS, confirmFirst: confirmFirst, DISCLAIMER_VER: DISCLAIMER_VER, CONSENT_TEXT: CONSENT_TEXT, updatePopup: updatePopup, CHANNELS: CHANNELS, TTYPES: TTYPES, TZN: TZN, CONFL: CONFL, INFO: INFO,
    AIACC: AIACC, MSGS: MSGS, ANNS: ANNS, FAQ: FAQ, GUIDES: GUIDES, DIAG: DIAG, showPopup: showPopup, browserSum: browserSum, renewOpts: renewOpts, fmtRate: fmtRate,
    set: function (o) { if (o.account != null) ACCOUNT = o.account; if (o.ip != null) IP = o.ip; if (o.code != null) CODE = o.code; if (o.checkId != null) CHECK_ID = o.checkId; if (o.exitMaxOwn != null) EXIT_MAX_OWN = o.exitMaxOwn; } };
})();

