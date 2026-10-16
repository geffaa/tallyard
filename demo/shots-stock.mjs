// Inventory screens, including a live update arriving while the stock page is open.
import puppeteer from 'puppeteer-core';
const base = process.argv[2] ?? 'http://localhost:5080';
const out = 'out/shots';
const browser = await puppeteer.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', defaultViewport: { width: 1440, height: 900 } });
const page = await browser.newPage();
const shot = async (name, p = page) => { await new Promise(r => setTimeout(r, 800)); await p.screenshot({ path: `${out}/${name}.png` }); };
await page.goto(base + '/login'); await page.click('button.btn-primary'); await page.waitForNavigation();
await page.goto(base + '/stock'); await page.waitForSelector('tbody tr.clickable'); await shot('20-stock');

// Live: post an adjustment from a second tab for the first row in CHI.
const first = await page.$eval('tbody tr.clickable', tr => tr.querySelector('td').textContent.trim());
const other = await browser.newPage();
await other.goto(base + '/stock');
await other.evaluate(async (sku) => {
  const p = (await (await fetch('/api/products?q=' + sku)).json()).items[0];
  const w = (await (await fetch('/api/warehouses')).json()).find(x => x.code === 'CHI');
  await fetch('/api/stock/adjustments', { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ warehouseId: w.id, reason: 'found', note: null, lines: [{ productId: p.id, quantity: 24, unit: null }] }) });
}, first);
await page.bringToFront(); await new Promise(r => setTimeout(r, 300)); await page.screenshot({ path: `${out}/21-stock-live.png` });

const yogurt = await page.evaluate(async () => (await (await fetch('/api/products?q=greek yogurt')).json()).items[0].id);
await page.goto(base + '/stock/' + yogurt); await page.waitForSelector('.tbl'); await shot('22-stock-detail');
await page.evaluate(() => window.scrollTo(0, 99999)); await shot('23-stock-card');
await page.goto(base + '/stock/adjust'); await page.waitForSelector('app-product-picker input');
await page.type('app-product-picker input', 'greek'); await page.waitForSelector('li[role=option]'); await page.keyboard.press('Enter');
await new Promise(r => setTimeout(r, 800)); await shot('24-adjust');
await page.goto(base + '/transfers/new'); await page.waitForSelector('app-product-picker input');
await page.type('app-product-picker input', 'cold brew'); await page.waitForSelector('li[role=option]'); await page.keyboard.press('Enter');
await new Promise(r => setTimeout(r, 600));
await page.click('button.btn-primary'); await page.waitForNavigation(); await shot('25-transfer');
await browser.close();
