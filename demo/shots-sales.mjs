// Order-to-cash screens, each as the person who would use it.
import puppeteer from 'puppeteer-core';
const base = process.argv[2] ?? 'http://localhost:5080';
const out = 'out/shots';
const browser = await puppeteer.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', defaultViewport: { width: 1440, height: 900 } });
const wait = (ms) => new Promise(r => setTimeout(r, ms));
async function as(email) {
  const page = await (await browser.createBrowserContext()).newPage();
  await page.goto(base + '/login'); await page.waitForSelector('form button.btn-primary');
  await page.$eval('input[type=email]', (el) => (el.value = '')); await page.type('input[type=email]', email);
  await page.click('form button.btn-primary'); await page.waitForNavigation();
  return page;
}
const shot = async (page, name) => { await wait(900); await page.screenshot({ path: `${out}/${name}.png` }); };
const first = (page, status) => page.evaluate(async (s) => (await (await fetch('/api/sales-orders?status=' + s)).json()).items[0]?.id, status);

const sales = await as('sales@harborpine.test');
await sales.goto(base + '/sales'); await sales.waitForSelector('tbody tr.clickable'); await shot(sales, '40-so-list');
await sales.goto(base + '/sales/new'); await sales.waitForSelector('app-product-picker input');
await sales.select('select[formcontrolname=customerId]', await sales.$eval('select[formcontrolname=customerId] option:nth-child(3)', o => o.value));
await sales.type('app-product-picker input', 'sparkling'); await sales.waitForSelector('li[role=option]'); await sales.keyboard.press('Enter');
await wait(500); await sales.select('tbody select', 'case'); await sales.$eval('tbody input[formcontrolname=quantity]', el => { el.value = '4'; el.dispatchEvent(new Event('input')); });
await shot(sales, '41-so-new');

const finance = await as('finance@harborpine.test');
const held = await first(finance, 'hold');
if (held) { await finance.goto(base + '/sales/' + held); await finance.waitForSelector('h1.font-mono'); await shot(finance, '42-so-hold'); }
await finance.goto(base + '/invoices'); await finance.waitForSelector('tbody tr');
await finance.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Overdue').click()); await wait(800); await shot(finance, '43-invoices-overdue');
await finance.goto(base + '/receivables'); await finance.waitForSelector('tbody tr'); await shot(finance, '44-aging');

const owner = await as('owner@harborpine.test');
const open = await first(owner, 'open');
await owner.goto(base + '/sales/' + open); await owner.waitForSelector('h1.font-mono'); await shot(owner, '45-so-open');
const shipBtn = await owner.$$eval('button', bs => bs.findIndex(b => b.textContent.trim() === 'Ship'));
if (shipBtn >= 0) { await (await owner.$$('button'))[shipBtn].click(); await owner.waitForSelector('app-sheet'); await shot(owner, '46-ship'); }
await browser.close();
