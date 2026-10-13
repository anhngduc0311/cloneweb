import type { Manga } from './core';

export interface BannerImage { url: string; width: number; height: number; }

// Publish each usable image immediately; a slow CDN must not hold up the hero.
export async function loadSpotlightImages(
  candidates: Manga[],
  probe: (url: string) => Promise<BannerImage | null>,
  publish: (selected: Manga[], images: Record<string, BannerImage>) => void,
  isCurrent: () => boolean
): Promise<void> {
  const images: Record<string, BannerImage> = {};
  let cursor = 0;
  const worker = async () => {
    while (cursor < candidates.length && Object.keys(images).length < 10 && isCurrent()) {
      const manga = candidates[cursor++];
      const urls = [...new Set([originalBannerCover(manga.cover), manga.cover])]
        .filter(url => url && !url.includes('cover-placeholder'));
      for (const url of urls) {
        const image = await probe(url);
        if (!isCurrent()) return;
        if (image && bannerImageScore(image) > 0) {
          images[manga.id] = image;
          publish(rankSpotlightCandidates(candidates, Date.now(), images).slice(0, 10), images);
          break;
        }
      }
    }
  };
  await Promise.all(Array.from({ length: Math.min(3, candidates.length) }, worker));
}

export function bannerImageScore(image: BannerImage): number {
  if (image.width < 480 || image.height < 320) return 0;
  const resolution = Math.min(1, image.width / 1440) * 20;
  // Wide images retain more of their composition in the full-width hero.
  const composition = Math.min(1, image.width / image.height / (16 / 9)) * 15;
  return resolution + composition;
}

export function rankSpotlightCandidates(items: Manga[], now = Date.now(), images?: Record<string, BannerImage>): Manga[] {
  const unique = new Map<string, Manga>();
  const score = (m: Manga) => {
    const updated = Math.max(...m.chapters.map(c => Date.parse(c.publishedAt) || 0));
    const days = Math.max(0, (now - updated) / 86400000);
    return Math.max(0, Math.min(10, m.rating || 0)) * 4
      + Math.min(25, Math.log10(1 + Math.max(0, m.follows || 0)) * 5)
      + 15 / (1 + days / 7)
      + (images ? bannerImageScore(images[m.id]) : 0);
  };
  for (const m of items) {
    if (!m.cover || m.cover.includes('cover-placeholder') || !m.chapters.some(c => c.language === 'vi')
      || ['erotica', 'pornographic'].includes(m.contentRating)) continue;
    if (images && (!images[m.id] || bannerImageScore(images[m.id]) === 0)) continue;
    const key = (m.title || m.id).normalize('NFKC').toLocaleLowerCase('vi').trim();
    const existing = unique.get(key);
    if (!existing || score(m) > score(existing)) unique.set(key, m);
  }
  return [...unique.values()].sort((a, b) => score(b) - score(a) || a.id.localeCompare(b.id));
}

export function originalBannerCover(url: string): string {
  // Upgrade MangaDex thumbnails through the same image CDN, retaining the real cover filename.
  const prefix = 'https://services.f-ck.me/v1/image/';
  if (!url.startsWith(prefix)) return url;
  try {
    const encoded = url.slice(prefix.length).replace(/-/g, '+').replace(/_/g, '/');
    const source = atob(encoded);
    if (!/^https:\/\/mangadex\.org\/covers\//.test(source)) return url;
    return prefix + btoa(source.replace(/\.(256|512)\.jpg$/, '')).replace(/\+/g, '-').replace(/\//g, '_');
  } catch { return url; }
}
