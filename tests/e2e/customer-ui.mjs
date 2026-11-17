// Customer click-through on a real Windows machine: the real Leyra interface (client-assets) in a
// browser, talking to the real installed background service over its named pipe exactly like the
// desktop window does. Every step checks what the customer would see and saves a screenshot.
//
// Usage (on the Windows test machine, service installed and owned by this user):
//   node tests/e2e/customer-ui.mjs <screenshot dir>
// Environment: TEST_CODE (authorization code), EXIT_LINK (customer's own exit link).
import net from 'node:net';
import path from 'node:path';
import fs from 'node:fs';
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright');

const out = process.argv[2] || 'ui-shots';
fs.mkdirSync(out, { recursive: true });
const PIPE = process.env.LEYRA_PIPE || '\\\\.\\pipe\\LeyraConnect.Product.v1';
const root = path.resolve('client-assets/win/index.html');
const results = [];
let quitCalled = false;

function ask(request, timeoutMs = 110000) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(PIPE);
    const chunks = [];
    const timer = setTimeout(() => { sock.destroy(); reject(new Error('本机操作超时')); }, timeoutMs);
    sock.on('connect', () => {
      const body = Buffer.from(JSON.stringify({ version: 1, request }));
      const head = Buffer.alloc(4); head.writeInt32LE(body.length);
      sock.write(Buffer.concat([head, body]));
    });
    sock.on('data', d => {
      chunks.push(d);
      const all = Buffer.concat(chunks);
      if (all.length >= 4 && all.length >= 4 + all.readInt32LE(0)) {
        clearTimeout(timer); sock.end();
        resolve(JSON.parse(all.subarray(4, 4 + all.readInt32LE(0)).toString('utf8')));
      }
    });
    sock.on('error', e => { clearTimeout(timer); reject(e); });
  });
}

let browser, ctx, page;
// What the desktop window does for each command the interface sends it.
async function host(command, args) {
  if (command === 'daemon') return ask(args.request, args.request && args.request.type === 'doctor' && args.request.deep ? 110000 : 110000);
  if (command === 'app_version') { const r = await ask({ type: 'status' }); return { ok: true, value: r.value.version }; }
  if (command === 'shell_info') return { ok: true, value: { glass: false, platform: 'windows' } };
  if (command === 'quit_app') { quitCalled = true; return { ok: true, value: {} }; }
  if (command === 'tray_take') return { ok: true, value: [] };
  if (command === 'test_browser') {
    // The desktop starts the chosen browser on the service's one-time test page; here a second tab of
    // this Chromium opens it (Chromium is the engine of Chrome and Edge).
    const r = await ask({ type: 'shell_action', command, args });
    if (!r.ok) return r;
    const tab = await ctx.newPage();
    tab.goto(r.value.url).catch(() => {});
    return { ok: true, value: {} };
  }
  if (['quit_conflict', 'force_quit_conflict', 'install_webrtc_profile', 'open_guide', 'open_web_center'].includes(command))
    return ask({ type: 'shell_action', command, args });
  return { ok: true, value: {} }; // notify, show_window, tray_update, set_theme, verify_owner ...
}

const shim = `(() => {
  const listeners = [];
  window.chrome = { webview: {
    addEventListener: (k, f) => listeners.push(f),
    postMessage: m => window.__leyraHost(m.command, m.args || {}).then(r => {
      const data = Object.assign({ id: m.id }, r && typeof r.ok === 'boolean' ? r : { ok: true, value: r });
      listeners.forEach(f => f({ data }));
    })
  } };
})();`;

function step(name, ok, detail = '') { results.push({ name, ok, detail }); console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? ' — ' + detail : '')); }
async function shot(name) { await page.screenshot({ path: path.join(out, name + '.png') }).catch(() => {}); }
async function text() { return page.evaluate(() => document.body.innerText); }
async function waitText(re, ms) { const end = Date.now() + ms; while (Date.now() < end) { const t = await text(); if (re.test(t)) return t; await page.waitForTimeout(1000); } return await text(); }
async function click(act) { await page.locator(`[data-act="${act}"]`).first().click(); }
async function heroTitle() { return page.locator('.p-hero h2').first().innerText().catch(() => ''); }

try {
  browser = await chromium.launch();
  ctx = await browser.newContext({ viewport: { width: 1100, height: 800 } });
  page = await ctx.newPage();
  await page.exposeFunction('__leyraHost', host);
  await page.addInitScript(shim);
  page.on('pageerror', e => console.log('page error: ' + e.message));
  await page.goto('file:///' + root.replace(/\\/g, '/'));

  // 1. A fresh install opens on the activation page.
  await page.waitForTimeout(4000);
  const actVisible = await page.locator('[data-keep="act-code"]').isVisible().catch(() => false);
  await shot('01-activation-page');
  step('fresh install opens on the activation page', actVisible);

  // 2. Activate with a real code: the overview must appear (not stay on the activation page).
  await page.fill('[data-keep="act-code"]', process.env.TEST_CODE || '');
  await page.fill('[data-keep="act-contact"]', 'automated test');
  await page.check('[data-keep="act-agree"]');
  await click('activate');
  const t0 = Date.now();
  let after = await waitText(/接入你的出口/, 110000);
  const stillActivation = await page.locator('[data-keep="act-code"]').isVisible().catch(() => false);
  await shot('02-after-activation');
  step('activation goes to the overview and asks for the own exit', /接入你的出口/.test(after) && !stillActivation, ((Date.now() - t0) / 1000).toFixed(0) + ' s');
  await page.waitForTimeout(5000);
  step('stays on the overview a few seconds later', !(await page.locator('[data-keep="act-code"]').isVisible().catch(() => false)));

  // 3. Connect the customer's own exit from the overview button: protection comes up by itself.
  await click('dialog:ownexit');
  await page.fill('#f-ownexit', process.env.EXIT_LINK || '');
  await click('ownExitUse');
  const end3 = Date.now() + 150000; let title = '';
  while (Date.now() < end3) { title = await heroTitle(); if (/已保护/.test(title)) break; await page.waitForTimeout(2000); }
  await shot('03-protected');
  step('connecting the own exit turns protection on', /已保护/.test(title), title);

  // 4. Speed test on the 线路 page: immediate feedback, then a figure (or a stated reason).
  await click('go:routes'); await page.waitForTimeout(1500);
  await click('speedTest');
  await page.waitForTimeout(1500);
  const testing = /正在测线路/.test(await text());
  step('speed test shows it is running right after the click', testing);
  // The measured speed appears in the line table; a failure says 测速未完成 with the reason.
  let routes = '', failedMsg = '';
  const end4 = Date.now() + 45000;
  while (Date.now() < end4) {
    routes = await page.locator('.p-routes').first().innerText().catch(() => '');
    failedMsg = ((await text()).match(/测速未完成[^\n]*/) || [''])[0];
    if (/\d+(\.\d+)? Mbps/.test(routes) || failedMsg) break;
    await page.waitForTimeout(1000);
  }
  await shot('04-speed-test');
  step('speed test gives a result', /\d+(\.\d+)? Mbps/.test(routes), (routes.match(/\d+(\.\d+)? Mbps/) || [failedMsg || 'no result'])[0]);

  // 5. Security check, all groups open.
  await click('go:check'); await page.waitForTimeout(1000);
  await click('checkStart');
  await waitText(/安全检查 · \d+ 项/, 120000);
  await page.waitForTimeout(1500);
  await shot('05-security-check');
  const closed = await page.locator('details.p-grp:not([open])').count();
  step('security check results are all expanded', closed === 0, closed + ' closed groups');

  // 6. Browser check: the report then shows the browser's exit IP compared with the exit.
  await click('dialog:browsers'); await page.waitForTimeout(1500);
  await page.locator('[data-act^="browserTest:"]').first().click();
  await waitText(/浏览器出口 IP \d+\.\d+\.\d+\.\d+/, 90000);
  await page.keyboard.press('Escape').catch(() => {});
  await page.locator('[data-act="close"]').first().click().catch(() => {});
  await page.waitForTimeout(2000);
  await shot('06-browser-check');
  const report = await text();
  step('browser check updates the report with the browser exit IP', /浏览器出口 IP \d+\.\d+\.\d+\.\d+，和你的出口 IP 一致/.test(report),
       (report.match(/浏览器出口 IP[^\n；]*/) || ['report not updated'])[0]);

  // 7. Timezone matched to the exit (may need a later check once the location is known).
  // The 时区 row reads 正常 (or the full sentence) once the computer matches the exit's location.
  const tzRow = t => (t.match(/\n时区\n([^\n]*)/) || ['', ''])[1];
  const tzOk = r => /^正常$|已和出口所在地一致/.test(r);
  let tz = tzRow(report);
  for (let i = 0; i < 4 && !tzOk(tz); i++) {
    await page.waitForTimeout(30000); await click('checkAgain'); await waitText(/安全检查 · \d+ 项/, 120000);
    await page.waitForTimeout(2000); tz = tzRow(await text());
  }
  await shot('07-timezone');
  step('timezone matched to the exit', tzOk(tz), tz || 'row not found');

  // 8. 彻底退出 from settings: protection off and the window asked to close, within 15 s.
  await click('go:settings'); await page.waitForTimeout(1000);
  await click('dialog:quitAll'); await page.waitForTimeout(800);
  await click('quitAllGo');
  const q0 = Date.now();
  while (!quitCalled && Date.now() - q0 < 15000) await page.waitForTimeout(500);
  const st = await ask({ type: 'status' });
  await shot('08-quit');
  step('彻底退出 stops protection and closes the window', quitCalled && st.value.protection.state === 'paused', ((Date.now() - q0) / 1000).toFixed(1) + ' s, ' + st.value.protection.state);
} catch (e) {
  step('click-through ran to the end', false, e.message);
  await shot('99-error');
} finally {
  await browser?.close();
}
const failed = results.filter(r => !r.ok);
console.log(failed.length ? 'LEYRA_UI_FLOW=FAIL (' + failed.length + ')' : 'LEYRA_UI_FLOW=PASS');
process.exit(failed.length ? 1 : 0);
