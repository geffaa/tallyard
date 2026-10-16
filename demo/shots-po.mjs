// Purchasing screens, each as the person who would use it.
import puppeteer from 'puppeteer-core';
const base = process.argv[2] ?? 'http://localhost:5080';
const out = 'out/shots';
const browser = await puppeteer.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', defaultViewport: { width: 1440, height: 900 } });
const wait = (ms) => new Promise(r => setTimeout(r, ms));
async function as(email) {
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  await page.goto(base + '/login');
  await page.waitForSelector('form button.btn-primary');
  await page.$eval('input[type=email]', (el) => (el.value = ''));
  await page.type('input[type=email]', email);
  await page.click('form button.btn-primary'); await page.waitForNavigation();
  return page;
}
const shot = async (page, name) => { await wait(900); await page.screenshot({ path: `${out}/${name}.png` }); };
const po = async (page, number) => page.evaluate(async (n) => (await (await fetch('/api/purchase-orders?status=&q=' + n)).json()).items[0].id, number);

const finance = await as('finance@harborpine.test');
await finance.goto(base + '/purchasing'); await finance.waitForSelector('tbody tr.clickable'); await shot(finance, '30-po-mine');

const owner = await as('owner@harborpine.test');
await owner.goto(base + '/purchasing/' + await po(owner, 'PO-2026-000003')); await owner.waitForSelector('h1.font-mono'); await shot(owner, '31-po-approval');
await owner.goto(base + '/purchasing/new'); await owner.waitForSelector('app-product-picker input');
await owner.select('select[formcontrolname=supplierId]', await owner.$eval('select[formcontrolname=supplierId] option:nth-child(2)', o => o.value));
await owner.type('app-product-picker input', 'orange juice'); await owner.waitForSelector('li[role=option]'); await owner.keyboard.press('Enter');
await shot(owner, '32-po-new');

const staff = await as('warehouse@harborpine.test');
const p5 = await po(staff, 'PO-2026-000005');
await staff.goto(base + '/purchasing/' + p5 + '/receive'); await staff.waitForSelector('tbody tr');
const code = await staff.evaluate(async (id) => {
  const p = await (await fetch('/api/purchase-orders/' + id)).json();
  const prod = await (await fetch('/api/products/' + p.lines[0].productId)).json();
  return prod.barcodes.find(b => b.unit === 'case')?.code ?? prod.barcodes[0].code;
}, p5);
await staff.type('input[autofocus]', code); await staff.keyboard.press('Enter');
await staff.type('input[autofocus]', code); await staff.keyboard.press('Enter');
await shot(staff, '33-receive');

const buyer = await as('purchasing@harborpine.test');
await buyer.goto(base + '/purchasing/reorder'); await buyer.waitForSelector('section.card'); await shot(buyer, '34-reorder');
await browser.close();
