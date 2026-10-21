/**
 * Scripted demo recording of Tallyard.
 *   API on :5080 serving the built web app, demo data freshly seeded, Groq key set.
 *   cd demo && npm install && node record.mjs
 * Output: demo/out/tallyard-demo.mp4
 *
 * Each scene is recorded with Puppeteer's screencast (needs ffmpeg on PATH), then the clips are
 * joined and encoded to H.264. Same approach as the Telusur, racik-shopify and kala-shopify demos.
 */
import puppeteer from 'puppeteer-core';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';

const BASE = process.env.BASE || 'http://localhost:5080';
const OUT = new URL('./out/', import.meta.url).pathname;
mkdirSync(OUT, { recursive: true });
const W = 1440;
const H = 900;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const C = { brand: '#0E5E5E', signal: '#F2A541', soft: '#E3EFED', bg: '#F5F3EE', ink: '#18222C', muted: '#6B7480', line: '#E3DFD5', rail: '#142028' };

const browser = await puppeteer.launch({
  executablePath: process.env.CHROME_PATH || '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  defaultViewport: null,
  args: [`--window-size=${W},${H}`, '--hide-scrollbars'],
});

const OVERLAY = `
  (() => {
    if (window.__demo) return;
    window.__demo = true;
    const css = document.createElement('style');
    css.textContent = \`
      #demo-cursor { position: fixed; z-index: 2147483647; width: 22px; height: 22px; margin: -3px 0 0 -3px; pointer-events: none; transition: transform 60ms linear; }
      #demo-cursor.down { transform: scale(.82); }
      #demo-cap { position: fixed; z-index: 2147483646; left: calc(50% + 124px); bottom: 40px; transform: translate(-50%, 12px); opacity: 0;
        background: ${C.ink}; color: #fff; font: 600 15px/1.4 'IBM Plex Sans', system-ui, sans-serif; border-radius: 999px;
        padding: 12px 22px 11px; max-width: 900px; text-align: center; pointer-events: none;
        transition: opacity 240ms ease, transform 240ms ease; box-shadow: 0 10px 30px rgba(0,0,0,.22); }
      #demo-cap.on { opacity: 1; transform: translate(-50%, 0); }
      #demo-cap b { color: ${C.signal}; font-weight: 700; }
    \`;
    const add = () => {
      document.head.appendChild(css);
      const c = document.createElement('div');
      c.id = 'demo-cursor';
      c.innerHTML = '<svg viewBox="0 0 22 22" width="22" height="22"><path d="M3 2l15 9-7 1.6L7.5 20z" fill="${C.ink}" stroke="#fff" stroke-width="1.4" stroke-linejoin="round"/></svg>';
      const cap = document.createElement('div');
      cap.id = 'demo-cap';
      document.body.append(c, cap);
      const pos = JSON.parse(sessionStorage.getItem('demo-pos') || '[720,450]');
      c.style.left = pos[0] + 'px'; c.style.top = pos[1] + 'px';
      addEventListener('mousemove', (e) => { c.style.left = e.clientX + 'px'; c.style.top = e.clientY + 'px'; sessionStorage.setItem('demo-pos', JSON.stringify([e.clientX, e.clientY])); }, true);
      addEventListener('mousedown', () => c.classList.add('down'), true);
      addEventListener('mouseup', () => c.classList.remove('down'), true);
      new MutationObserver(() => { if (document.body.lastElementChild !== cap) document.body.append(c, cap); }).observe(document.body, { childList: true });
    };
    if (document.body) add(); else addEventListener('DOMContentLoaded', add);
  })();
`;

const page = await browser.newPage();
await page.setViewport({ width: W, height: H, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument(OVERLAY);
// Prompts asked during the demo: the approval comment and the reason for releasing a credit hold.
page.on('dialog', (d) => d.accept(d.type() !== 'prompt' ? undefined
  : d.message().startsWith('Why') ? 'Customer paid the overdue invoice by bank transfer this morning' : 'Within the Q4 budget'));

async function caption(html, hold = 0) {
  await page.evaluate((h) => {
    const cap = document.getElementById('demo-cap');
    if (!cap) return;
    if (!h) return cap.classList.remove('on');
    cap.innerHTML = h;
    cap.classList.add('on');
  }, html);
  if (hold) await sleep(hold);
}

let mouse = { x: 720, y: 450 };
async function moveTo(x, y, steps = 26) {
  const from = { ...mouse };
  for (let i = 1; i <= steps; i++) {
    const t = i / steps;
    const e = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2;
    await page.mouse.move(from.x + (x - from.x) * e, from.y + (y - from.y) * e);
    await sleep(12);
  }
  mouse = { x, y };
}

async function locate(selector, text) {
  await page.waitForFunction(
    (s, t) => [...document.querySelectorAll(s)].some((el) => (!t || el.textContent.includes(t)) && el.getBoundingClientRect().width > 0),
    { timeout: 90000 }, selector, text ?? null);
  const moved = await page.evaluate((s, t) => {
    const el = [...document.querySelectorAll(s)].find((e) => (!t || e.textContent.includes(t)) && e.getBoundingClientRect().width > 0);
    const r = el.getBoundingClientRect();
    if (r.top >= 0 && r.bottom <= innerHeight) return false;
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return true;
  }, selector, text ?? null);
  if (moved) await sleep(700);
  return page.evaluate((s, t) => {
    const el = [...document.querySelectorAll(s)].find((e) => (!t || e.textContent.includes(t)) && e.getBoundingClientRect().width > 0);
    const r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }, selector, text ?? null);
}

async function clickOn(selector, text, { pause = 220 } = {}) {
  const { x, y } = await locate(selector, text);
  await moveTo(x, y);
  await sleep(pause);
  await page.mouse.down();
  await sleep(90);
  await page.mouse.up();
}

async function typeSlow(selector, text, delay = 40) {
  await page.focus(selector);
  for (const ch of text) {
    await page.keyboard.type(ch);
    await sleep(delay);
  }
}

async function settle() {
  await page.waitForNetworkIdle({ idleTime: 300, timeout: 5000 }).catch(() => {});
  await sleep(250);
}

async function login(roleLabel) {
  await page.deleteCookie(...(await page.cookies()));
  await page.goto(`${BASE}/login`, { waitUntil: 'networkidle0' });
  await clickOn('button', roleLabel);
  await sleep(250);
  await clickOn('form button.btn-primary');
  await page.waitForFunction(() => !location.pathname.startsWith('/login'), { timeout: 30000 });
  await settle();
}

const api = (path, init) => page.evaluate(async (p, i) => (await fetch('/api' + p, i)).json(), path, init ?? null);

async function slide(html) {
  await page.setContent(`<!doctype html><html><head>
    <link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@500&family=IBM+Plex+Sans:wght@400;600;700&display=swap" rel="stylesheet">
    <style>
      html, body { margin: 0; height: 100%; background: ${C.bg}; color: ${C.ink}; font-family: 'IBM Plex Sans', system-ui, sans-serif; }
      .s { height: 100%; box-sizing: border-box; padding: 64px 80px; display: grid; }
      .lab { font-weight: 600; font-size: 16px; color: ${C.muted}; }
      h1 { font-size: 124px; line-height: .95; font-weight: 700; letter-spacing: -.04em; margin: 0; }
      h2 { font-size: 56px; line-height: 1; font-weight: 700; letter-spacing: -.03em; margin: 0; }
      .chips { display: flex; gap: 10px; flex-wrap: wrap; }
      .chips span { background: #fff; border: 1.5px solid ${C.line}; border-radius: 999px; padding: 8px 16px; font-weight: 600; font-size: 16px; }
      .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
      .card { border-radius: 18px; padding: 22px; background: #fff; border: 1.5px solid ${C.line}; }
      .num { font-size: 50px; font-weight: 700; letter-spacing: -.03em; line-height: 1; color: ${C.brand}; }
      ul { margin: 0; padding: 0; list-style: none; display: grid; gap: 12px; font-size: 21px; }
      li::before { content: '✓'; color: ${C.brand}; font-weight: 700; margin-right: 12px; }
      code { font-family: 'IBM Plex Mono', monospace; font-size: .9em; }
    </style></head><body>${html}</body></html>`, { waitUntil: 'load', timeout: 15000 }).catch(() => {});
  await page.evaluate(() => document.fonts.ready).catch(() => {});
}

const MARK = `<svg width="76" height="76" viewBox="0 0 32 32"><rect width="32" height="32" rx="7" fill="${C.brand}"/><path d="M9 11h14M16 11v12" stroke="${C.signal}" stroke-width="3.2" stroke-linecap="round"/><path d="M9 23h4M19 23h4" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".7"/></svg>`;

const clips = [];
async function still(name, html, seconds) {
  await slide(html);
  await sleep(300);
  const path = `${OUT}${name}.png`;
  await page.screenshot({ path });
  clips.push({ path, seconds });
  console.log(`✓ ${name}`);
}
async function record(name, fn) {
  const path = `${OUT}${name}.webm`;
  // REUSE=1 keeps clips already recorded (to redo only slides or a failed scene).
  if (process.env.REUSE && existsSync(path) && !existsSync(`${OUT}${name}-FAILED.png`)) {
    clips.push({ path });
    console.log(`= ${name} (reused)`);
    return;
  }
  const rec = await page.screencast({ path });
  try {
    await fn();
  } catch (err) {
    await page.screenshot({ path: `${OUT}${name}-FAILED.png` });
    console.error(`✗ ${name} at ${page.url()}`);
    throw err;
  } finally {
    await rec.stop();
  }
  clips.push({ path });
  console.log(`✓ ${name}`);
}

/* ---------------------------------------------------------------------- */

await still('01-title', `<div class="s" style="align-content:space-between">
  <div class="lab" style="display:flex;justify-content:space-between"><span>Portfolio · .NET + Angular</span><span>Yodhimas Geffananda</span></div>
  <div style="display:grid;gap:26px">${MARK}
    <h1>Tallyard<span style="color:${C.signal}">.</span></h1>
    <p style="font-size:30px;font-weight:600;margin:0;max-width:36ch;line-height:1.25">Operations software for wholesale distributors: stock across warehouses, purchasing, and order-to-cash, with AI that reads supplier invoices.</p>
  </div>
  <div class="chips"><span>Stock ledger under row locks</span><span>Multi-level approvals</span><span>Credit holds</span><span>Row-level security</span><span>AI, measured</span></div>
</div>`, 4.5);

await record('02-overview-and-live-stock', async () => {
  await login('Owner');
  await caption('The owner’s morning: <b>what needs attention</b>, each tile links to where it’s handled', 3200);
  await clickOn('nav a', 'Stock');
  await settle();
  await caption('Stock per product and warehouse, <b>live</b>', 1500);
  // Someone in Chicago records a find; the matrix updates by itself.
  const first = await page.$eval('tbody tr.clickable td', (td) => td.textContent.trim());
  await page.evaluate(async (sku) => {
    const p = (await (await fetch('/api/products?q=' + sku)).json()).items[0];
    const w = (await (await fetch('/api/warehouses')).json()).find((x) => x.code === 'CHI');
    await fetch('/api/stock/adjustments', { method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ warehouseId: w.id, reason: 'found', note: null, lines: [{ productId: p.id, quantity: 24, unit: null }] }) });
  }, first);
  await caption('A change made elsewhere arrives over <b>SignalR</b> and highlights the cell', 3000);
  await clickOn('tbody tr.clickable', first);
  await settle();
  await caption('Every product has a <b>stock card</b>: append-only movements with the running balance', 3000);
  await page.evaluate(() => scrollTo({ top: 99999, behavior: 'smooth' }));
  await sleep(1800);
  await caption('', 200);
});

await record('03-ai-supplier-invoice', async () => {
  await login('Purchasing');
  await clickOn('nav a', 'Supplier documents');
  await settle();
  await caption('A supplier sends an invoice as a <b>photo</b>, with abbreviated product names', 2000);
  const input = await page.$('input[type=file]');
  await input.uploadFile(new URL('../eval/supplier-docs/doc-30-abbreviated.png', import.meta.url).pathname);
  await caption('Read by AI on <b>Groq</b>: text from the image, then a strict JSON schema', 1000);
  await page.waitForSelector('app-product-picker', { timeout: 120000 });
  await settle();
  await caption('“SLVRBRK WHL MLK 1L” → Silverbrook Whole Milk 1 L. Doubtful lines are <b>flagged</b>, never guessed silently', 4200);
  await page.evaluate(() => document.querySelector('.xl\\:grid-cols-\\[minmax\\(0\\,5fr\\)_minmax\\(0\\,7fr\\)\\] > div:last-child')?.scrollIntoView({ block: 'start' }));
  await page.mouse.wheel({ deltaY: 500 });
  await sleep(1600);
  await caption('Measured on 30 invoices: <b>99%</b> of lines matched, <b>0</b> wrong matches left unflagged', 3600);
  await clickOn('button', 'Create draft PO');
  await page.waitForFunction(() => /\/purchasing\/[0-9a-f-]{36}$/.test(location.pathname), { timeout: 30000 });
  await settle();
  await caption('A person checked it; now it’s a <b>draft purchase order</b>', 2400);
  await caption('', 200);
});

await record('04-approval', async () => {
  await clickOn('button', 'Submit for approval');
  await settle();
  await caption('Over 2,000: it needs <b>finance</b>. The creator can never approve their own order', 3000);
  await login('Finance');
  await clickOn('nav a', 'Purchase orders');
  await settle();
  await caption('Finance opens straight on <b>“Needs my approval”</b>', 2400);
  await clickOn('tbody tr.clickable');
  await settle();
  await clickOn('button', 'Approve');
  await settle();
  await caption('Approved, with a comment, at the level the rules require', 2600);
  await caption('', 200);
});

await record('05-receive-with-scanner', async () => {
  await login('Warehouse');
  const po = (await api('/purchase-orders?status=open')).items.find((p) => p.warehouse === 'CHI' && p.status !== 'Received');
  await page.goto(`${BASE}/purchasing/${po.id}/receive`, { waitUntil: 'networkidle0' });
  await caption('The warehouse receives against the order with a <b>barcode scanner</b>', 2000);
  const detail = await api(`/purchase-orders/${po.id}`);
  const product = await api(`/products/${detail.lines[0].productId}`);
  const code = (product.barcodes.find((b) => b.unit === 'case') ?? product.barcodes[0]).code;
  for (let i = 0; i < 2; i++) {
    await typeSlow('input[autofocus]', code, 18);
    await page.keyboard.press('Enter');
    await sleep(700);
  }
  await caption('A case barcode counts a <b>case</b>. Prices stay hidden from warehouse staff', 2400);
  await clickOn('button', 'Post receipt');
  await settle();
  await caption('Stock and the moving average cost update in the same transaction', 2600);
  await caption('', 200);
});

await record('06-credit-hold-and-shipping', async () => {
  await login('Finance');
  await clickOn('nav a', 'Sales orders');
  await settle();
  await clickOn('tbody tr.clickable');
  await settle();
  await caption('An order <b>on hold</b>: the customer has invoices more than 30 days overdue', 3000);
  await clickOn('button', 'Release order');
  await settle();
  await caption('Finance releases it with a reason, and stock is <b>reserved</b> for it', 2800);
  await login('Owner');
  const so = (await api('/sales-orders?status=open')).items.find((o) => o.status === 'Confirmed');
  await page.goto(`${BASE}/sales/${so.id}`, { waitUntil: 'networkidle0' });
  await settle();
  await caption('Only reserved stock can ship, batches picked by <b>earliest expiry</b>', 1800);
  await clickOn('button', 'Ship');
  await sleep(1500);
  await caption('', 200);
  await page.keyboard.press('Escape');
});

await record('07-receivables-and-chat', async () => {
  await login('Owner');
  await clickOn('nav a', 'Receivables');
  await settle();
  await caption('Receivables by age, per customer', 2600);
  await clickOn('nav a', 'Ask your data');
  await settle();
  await clickOn('textarea');
  await typeSlow('textarea', 'Which products in Chicago need reordering, and who are our top 3 customers this month?', 22);
  await page.keyboard.press('Enter');
  await page.waitForSelector('.dots');
  await caption('The AI can only call <b>fixed queries</b> that run with the asker’s permissions', 1200);
  await page.waitForSelector('.dots', { hidden: true, timeout: 90000 });
  await sleep(600);
  await caption('A short answer, with the <b>tables it came from</b>', 4200);
  await clickOn('button', 'Indonesia');
  await sleep(900);
  await caption('The whole interface in <b>Indonesian</b>, one click', 2400);
  await clickOn('button', 'English');
  await caption('', 200);
});

await still('08-results', `<div class="s" style="align-content:center;gap:30px">
  <div style="display:flex;justify-content:space-between;align-items:end"><h2>Proven by tests, not claimed.</h2><span class="lab">63 tests on a real Postgres (Testcontainers)</span></div>
  <div class="grid">
    <div class="card"><div class="num">10/50</div><div class="lab">parallel issues of the last 10 units succeed; stock never negative</div></div>
    <div class="card"><div class="num">0</div><div class="lab">rows visible across tenants, even for SQL without a WHERE</div></div>
    <div class="card"><div class="num">99%</div><div class="lab">supplier invoice lines matched by AI (30 documents)</div></div>
    <div class="card"><div class="num">0</div><div class="lab">wrong AI matches left unflagged</div></div>
  </div>
  <ul>
    <li>Double clicks post once (idempotency keys); two people receiving or invoicing at once can’t both succeed</li>
    <li>Movements, receipts, payments and the audit log are append-only, enforced by database triggers</li>
    <li>Every balance equals the sum of its movements; reservations equal the open order lines</li>
  </ul>
  <div class="chips"><span>ASP.NET Core 10</span><span>Angular 20</span><span>Postgres 16 · RLS</span><span>SignalR</span><span>Groq</span><span>github.com/geffaa/tallyard</span></div>
</div>`, 8);

await browser.close();

const parts = clips.flatMap((c) => (c.seconds ? ['-loop', '1', '-framerate', '30', '-t', String(c.seconds), '-i', c.path] : ['-i', c.path]));
const filter = clips.map((_, i) => `[${i}:v]scale=${W}:${H}:force_original_aspect_ratio=decrease,pad=${W}:${H}:(ow-iw)/2:(oh-ih)/2:color=0xF5F3EE,fps=30,setsar=1[v${i}]`).join(';')
  + ';' + clips.map((_, i) => `[v${i}]`).join('') + `concat=n=${clips.length}:v=1:a=0[out]`;
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...parts, '-filter_complex', filter, '-map', '[out]', '-c:v', 'libx264', '-preset', 'slow', '-crf', '24', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', `${OUT}tallyard-demo.mp4`]);
console.log(`→ ${OUT}tallyard-demo.mp4`);
