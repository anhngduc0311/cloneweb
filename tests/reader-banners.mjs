import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { readerBannerCrop, readerBannerBounds } from '../frontend/src/app/reader-banners.ts';

// Pixel samples extracted from the reported chapter's actual source images.
const fixtures = JSON.parse(readFileSync(new URL('./fixtures/reader-banner-samples.json', import.meta.url)));
const bounds = (fixture, noise = 0) => readerBannerBounds(fixture.width, fixture.height, (top, height) => {
  const value = fixture.regions[`${top}:${height}`];
  assert.ok(value, `Unexpected sample region ${top}:${height}`);
  return Uint8ClampedArray.from(Buffer.from(value, 'base64'), (channel, i) => channel + (i % 4 === 3 ? 0 : noise));
});
assert.deepEqual(bounds(fixtures.embedded), { top: 997, bottom: 2277 }, 'Retain all 1280 rows of the comic beneath header banners');
assert.deepEqual(bounds(fixtures.embedded, 5), { top: 997, bottom: 2277 }, 'Small encoding/color differences must not defeat matching');
assert.deepEqual(bounds(fixtures.ordinary), { top: 0, bottom: 1280 }, 'Ordinary comic panels must remain untouched');
assert.deepEqual(bounds(fixtures.footer), { top: 0, bottom: 1280 }, 'Remove both footer advertisements, retaining the translator note');
assert.deepEqual(bounds(fixtures.footer, 5), { top: 0, bottom: 1280 }, 'Footer matching tolerates encoding differences');
assert.equal(readerBannerCrop(900, 80, () => { throw new Error('Too short to be a banner'); }), 0);
console.log('PASS: embedded stacked banners, retained comic content, header/footer banners, preserved translator note, encoding tolerance, ordinary pages, small images');
