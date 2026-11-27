// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Renders SVG jobs ({svg, size, out}) read from stdin to transparent PNGs at exact pixel sizes (used by make_icon.py).
const { chromium } = require('playwright');
let input = '';
process.stdin.on('data', d => input += d).on('end', async () => {
  const jobs = JSON.parse(input);
  const browser = await chromium.launch();
  try {
    for (const job of jobs) {
      const page = await browser.newPage({ viewport: { width: job.size, height: job.size }, deviceScaleFactor: 1 });
      await page.setContent(`<html><body style="margin:0;background:transparent">${job.svg}</body></html>`);
      await page.locator('svg').screenshot({ path: job.out, omitBackground: true });
      await page.close();
    }
  } finally {
    await browser.close();
  }
});
