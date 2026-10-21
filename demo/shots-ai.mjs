// AI screens: a supplier document read into a draft order, and the data chat.
import puppeteer from 'puppeteer-core';
const base = process.argv[2] ?? 'http://localhost:5080';
const out = 'out/shots';
const browser = await puppeteer.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', defaultViewport: { width: 1440, height: 900 } });
const wait = (ms) => new Promise(r => setTimeout(r, ms));
const page = await (await browser.createBrowserContext()).newPage();
await page.goto(base + '/login'); await page.waitForSelector('form button.btn-primary');
await page.$eval('input[type=email]', (el) => (el.value = '')); await page.type('input[type=email]', 'purchasing@harborpine.test');
await page.click('form button.btn-primary'); await page.waitForNavigation();

await page.goto(base + '/purchasing/documents'); await page.waitForSelector('input[type=file]');
const input = await page.$('input[type=file]');
await input.uploadFile('../eval/supplier-docs/doc-30-abbreviated.png');
await wait(1200); await page.screenshot({ path: `${out}/50-doc-reading.png` });
await page.waitForSelector('app-product-picker', { timeout: 90000 }); await wait(2500);
await page.screenshot({ path: `${out}/51-doc-review.png` });

await page.goto(base + '/assistant'); await page.waitForSelector('textarea');
await page.type('textarea', 'Which batches expire in the next 30 days, and how fast does Greek yogurt sell?');
await page.keyboard.press('Enter');
await page.waitForSelector('.dots'); await page.waitForSelector('.dots', { hidden: true, timeout: 90000 }); await wait(800);
await page.screenshot({ path: `${out}/52-chat.png` });
await browser.close();
