import assert from 'node:assert/strict';
import { rankSpotlightCandidates, originalBannerCover, bannerImageScore } from '../frontend/src/app/spotlight.ts';

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
