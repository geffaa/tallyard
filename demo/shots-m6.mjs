// Overview, stock count (phone), and Excel import with errors.
import puppeteer from 'puppeteer-core';
import fs from 'node:fs';
const base = process.argv[2] ?? 'http://localhost:5080';
const out = 'out/shots';
const browser = await puppeteer.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', defaultViewport: { width: 1440, height: 900 } });
const wait = (ms) => new Promise(r => setTimeout(r, ms));
async function as(email, viewport) {
  const page = await (await browser.createBrowserContext()).newPage();
  if (viewport) await page.setViewport(viewport);
  await page.goto(base + '/login'); await page.waitForSelector('form button.btn-primary');
  await page.$eval('input[type=email]', (el) => (el.value = '')); await page.type('input[type=email]', email);
  await page.click('form button.btn-primary'); await page.waitForNavigation();
  return page;
}
const owner = await as('owner@harborpine.test');
await owner.goto(base + '/'); await owner.waitForSelector('svg path'); await wait(800);
await owner.screenshot({ path: `${out}/60-home.png` });

const phone = await as('warehouse@harborpine.test', { width: 390, height: 844, isMobile: true, hasTouch: true });
const count = await phone.evaluate(async () => {
  const w = (await (await fetch('/api/warehouses')).json())[0];
  const c = (await (await fetch('/api/categories')).json()).find(c => c.name === 'Snacks');
  const r = await fetch('/api/stock-counts', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ warehouseId: w.id, categoryId: c.id }) });
  return (await r.json()).id;
});
await phone.goto(base + '/stock/counts/' + count); await phone.waitForSelector('input[autofocus]');
const code = await phone.evaluate(async () => {
  const p = (await (await fetch('/api/products?q=chips&pageSize=1')).json()).items[0];
  return (await (await fetch('/api/products/' + p.id)).json()).barcodes.find(b => b.unit === 'case').code;
});
for (let i = 0; i < 3; i++) { await phone.type('input[autofocus]', code); await phone.keyboard.press('Enter'); await wait(500); }
await phone.click('button:nth-child(3)').catch(() => {});
await wait(700); await phone.screenshot({ path: `${out}/61-count-phone.png` });

const buyer = await as('purchasing@harborpine.test');
const xlsx = '/private/tmp/claude-501/-Users-geffaaa/dc9d8eb4-cbeb-4992-97e0-aa5eefec9b4f/scratchpad/import-demo.xlsx';
await buyer.goto(base + '/import'); await buyer.waitForSelector('input[type=file]');
await (await buyer.$('input[type=file]')).uploadFile(xlsx);
await buyer.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim().startsWith('Check')).click());
await buyer.waitForSelector('table'); await wait(700);
await buyer.screenshot({ path: `${out}/62-import.png` });
await browser.close();
