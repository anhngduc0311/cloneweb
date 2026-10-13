import assert from 'node:assert/strict';
import { rankSpotlightCandidates, originalBannerCover, bannerImageScore, loadSpotlightImages } from '../frontend/src/app/spotlight.ts';

const now = Date.parse('2026-10-12T00:00:00Z');
const story = (id, overrides = {}) => ({
  id, title: id, cover: '/cover.jpg', rating: 8, follows: 1000, contentRating: 'safe',
  chapters: [{ id: id + '-chapter', language: 'vi', publishedAt: new Date(now).toISOString() }],
  ...overrides
});
assert.deepEqual(rankSpotlightCandidates([
  story('no-chapters', { chapters: [] }), story('placeholder', { cover: '/cover-placeholder.svg' }),
  story('english-only', { chapters: [{ language: 'en', publishedAt: new Date(now).toISOString() }] }),
  story('explicit', { contentRating: 'pornographic' }), story('valid')
], now).map(m => m.id), ['valid']);
assert.equal(rankSpotlightCandidates([story('weak', { rating: 5, follows: 20 }), story('popular', { rating: 9, follows: 100000 })], now)[0].id, 'popular');
assert.equal(rankSpotlightCandidates([story('old', { chapters: [{ language: 'vi', publishedAt: '2020-01-01' }] }), story('recent')], now)[0].id, 'recent');
assert.equal(rankSpotlightCandidates([story('duplicate-low', { title: 'Truyện', rating: 4 }), story('duplicate-high', { title: 'Truyện', rating: 9 })], now).length, 1);
const images = {
  tiny: { url: '/tiny.jpg', width: 80, height: 105 },
  portrait: { url: '/portrait.jpg', width: 512, height: 768 },
  wide: { url: '/wide.jpg', width: 1920, height: 1080 }
};
assert.deepEqual(rankSpotlightCandidates([story('tiny'), story('portrait'), story('wide'), story('broken')], now, images).map(m => m.id), ['wide', 'portrait']);
assert.equal(bannerImageScore(images.tiny), 0);
assert.equal(bannerImageScore({ url: '/thumbnail.jpg', width: 256, height: 384 }), 0);
const prefix = 'https://services.f-ck.me/v1/image/';
const encode = value => btoa(value).replace(/\+/g, '-').replace(/\//g, '_');
const original = 'https://mangadex.org/covers/id/cover.jpg';
assert.equal(originalBannerCover(prefix + encode(original + '.256.jpg')), prefix + encode(original));
assert.equal(originalBannerCover('/api/catalog/image-proxy?url=cover.jpg'), '/api/catalog/image-proxy?url=cover.jpg');
assert.equal(originalBannerCover(prefix + 'invalid!'), prefix + 'invalid!');
console.log('PASS: readable candidates, popularity, recency, duplicate titles, image quality, original cover upgrade');

let releaseSlow;
const slow = new Promise(resolve => { releaseSlow = resolve; });
const updates = [];
const progressive = loadSpotlightImages([
  story('fast', { cover: '/fast.jpg' }), story('slow', { cover: '/slow.jpg' }),
  story('broken', { cover: '/broken.jpg' })
], url => url === '/slow.jpg' ? slow : Promise.resolve(url === '/broken.jpg' ? null : { url, width: 1200, height: 800 }),
selected => updates.push(selected.map(m => m.id)), () => true);
await new Promise(resolve => setImmediate(resolve));
assert.deepEqual(updates, [['fast']], 'Usable banner must be visible while another image is still pending');
releaseSlow({ url: '/slow.jpg', width: 1200, height: 800 });
await progressive;
assert.deepEqual(new Set(updates.at(-1)), new Set(['fast', 'slow']));

let current = true;
let releaseStale;
const staleImage = new Promise(resolve => { releaseStale = resolve; });
let stalePublished = false;
const stale = loadSpotlightImages([story('stale')], () => staleImage,
() => { stalePublished = true; }, () => current);
current = false;
releaseStale({ url: '/cover.jpg', width: 1200, height: 800 });
await stale;
assert.equal(stalePublished, false, 'Old requests must not overwrite a newer selection or a destroyed home page');
console.log('PASS: first banner published before slow images, broken image skipped, stale selection ignored');
