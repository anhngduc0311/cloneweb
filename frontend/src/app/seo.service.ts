import { Injectable, inject } from '@angular/core';
import { Title, Meta } from '@angular/platform-browser';
import { DOCUMENT } from '@angular/common';
import { Manga, Chapter, cleanDescription } from './core';

export interface SeoConfig {
  title: string;
  description: string;
  keywords?: string;
  image?: string;
  url?: string;
  type?: 'website' | 'article' | 'book';
  jsonLd?: Record<string, any> | Array<Record<string, any>>;
}

const BASE_URL = 'https://dokitruyen.com';
const DEFAULT_IMAGE = `${BASE_URL}/brand-mark.png`;

@Injectable({ providedIn: 'root' })
export class SeoService {
  private titleService = inject(Title);
  private metaService = inject(Meta);
  private doc = inject(DOCUMENT);

  setTags(cfg: SeoConfig): void {
    const title = cfg.title;
    const desc = cfg.description.replace(/\s+/g, ' ').trim();
    const url = cfg.url || (typeof window !== 'undefined' ? window.location.href : BASE_URL);
    const image = cfg.image ? (cfg.image.startsWith('http') ? cfg.image : `${BASE_URL}${cfg.image}`) : DEFAULT_IMAGE;
    const type = cfg.type || 'website';
    const keywords = cfg.keywords || 'dokitruyen, doki truyen, truyen tranh online, doc truyen tranh, manga, manhwa, manhua, doc truyen moi nhat';

    // 1. Browser & Search Title
    this.titleService.setTitle(title);

    // 2. Standard Meta Tags
    this.metaService.updateTag({ name: 'description', content: desc });
    this.metaService.updateTag({ name: 'keywords', content: keywords });
    this.metaService.updateTag({ name: 'author', content: 'DokiTruyen' });
    this.metaService.updateTag({ name: 'robots', content: 'index, follow, max-snippet:-1, max-image-preview:large, max-video-preview:-1' });

    // 3. Open Graph (Facebook / Zalo / Telegram)
    this.metaService.updateTag({ property: 'og:site_name', content: 'DokiTruyen' });
    this.metaService.updateTag({ property: 'og:locale', content: 'vi_VN' });
    this.metaService.updateTag({ property: 'og:type', content: type });
    this.metaService.updateTag({ property: 'og:title', content: title });
    this.metaService.updateTag({ property: 'og:description', content: desc });
    this.metaService.updateTag({ property: 'og:url', content: url });
    this.metaService.updateTag({ property: 'og:image', content: image });

    // 4. Twitter Cards
    this.metaService.updateTag({ name: 'twitter:card', content: 'summary_large_image' });
    this.metaService.updateTag({ name: 'twitter:title', content: title });
    this.metaService.updateTag({ name: 'twitter:description', content: desc });
    this.metaService.updateTag({ name: 'twitter:image', content: image });

    // 5. Canonical Link
    this.updateCanonicalUrl(url);

    // 6. JSON-LD Structured Data
    if (cfg.jsonLd) {
      this.setJsonLd(cfg.jsonLd);
    }
  }

  updateCanonicalUrl(url: string): void {
    try {
      let link: HTMLLinkElement | null = this.doc.querySelector("link[rel='canonical']");
      if (!link) {
        link = this.doc.createElement('link');
        link.setAttribute('rel', 'canonical');
        this.doc.head.appendChild(link);
      }
      link.setAttribute('href', url.split('?')[0]); // Clean canonical without tracking params
    } catch {}
  }

  setJsonLd(data: Record<string, any> | Array<Record<string, any>>): void {
    try {
      const scriptId = 'doki-dynamic-jsonld';
      let script = this.doc.getElementById(scriptId) as HTMLScriptElement | null;
      if (!script) {
        script = this.doc.createElement('script');
        script.id = scriptId;
        script.type = 'application/ld+json';
        this.doc.head.appendChild(script);
      }
      script.textContent = JSON.stringify(data);
    } catch {}
  }

  // High CTR Manga Detail SEO
  setMangaDetailSeo(m: Manga, ratingData?: { rating: number; votes: number }): void {
    const rawDesc = cleanDescription(m.description);
    const shortDesc = rawDesc.length > 180 ? rawDesc.slice(0, 175) + '…' : rawDesc;
    const altText = m.alternativeTitle && m.alternativeTitle.trim().toLowerCase() !== m.title.trim().toLowerCase()
      ? ` (${m.alternativeTitle.trim()})`
      : '';

    // High CTR clean clickbait title & description
    const title = `${m.title} [Mới Nhất] - Đọc Truyện Tranh Full HD | DokiTruyen`;
    const description = `⭐ Đọc truyện tranh ${m.title}${altText} tiếng Việt mới nhất, ảnh sắc nét Full HD. Cập nhật chap mới hàng ngày, load mượt mà không quảng cáo tại DokiTruyen. ${shortDesc}`.slice(0, 160);

    const mangaUrl = `${BASE_URL}/truyen-tranh/${m.id}`;
    const coverUrl = m.cover?.startsWith('http') ? m.cover : `${BASE_URL}${m.cover}`;
    const ratingValue = (ratingData?.rating || m.rating || 9.2).toFixed(1);
    const ratingCount = Math.max(ratingData?.votes || m.follows || 120, 10);

    const jsonLd = {
      '@context': 'https://schema.org',
      '@graph': [
        {
          '@type': ['Book', 'ComicSeries'],
          '@id': `${mangaUrl}#comic`,
          'name': m.title,
          'alternateName': m.alternativeTitle ? [m.alternativeTitle, m.title] : [m.title],
          'url': mangaUrl,
          'image': coverUrl,
          'description': rawDesc,
          'inLanguage': 'vi',
          'genre': m.genres || [],
          'author': {
            '@type': 'Person',
            'name': m.author || 'Đang cập nhật'
          },
          'publisher': {
            '@type': 'Organization',
            'name': 'DokiTruyen',
            'url': BASE_URL
          },
          'aggregateRating': {
            '@type': 'AggregateRating',
            'ratingValue': ratingValue,
            'bestRating': '10',
            'worstRating': '1',
            'ratingCount': ratingCount
          }
        },
        {
          '@type': 'BreadcrumbList',
          '@id': `${mangaUrl}#breadcrumb`,
          'itemListElement': [
            {
              '@type': 'ListItem',
              'position': 1,
              'name': 'Trang chủ',
              'item': `${BASE_URL}/`
            },
            {
              '@type': 'ListItem',
              'position': 2,
              'name': 'Khám phá truyện',
              'item': `${BASE_URL}/tim-truyen-nang-cao`
            },
            {
              '@type': 'ListItem',
              'position': 3,
              'name': m.title,
              'item': mangaUrl
            }
          ]
        }
      ]
    };

    this.setTags({
      title,
      description,
      keywords: `${m.title}, doc truyen ${m.title}, truyen tranh ${m.title}, ${m.title} tieng viet, ${m.title} chap moi nhat, dokitruyen`,
      image: coverUrl,
      url: mangaUrl,
      type: 'book',
      jsonLd
    });
  }

  // High CTR Chapter Reader SEO
  setReaderSeo(m: Manga, ch: Chapter): void {
    const chapNum = ch.number != null ? `Chap ${ch.number}` : ch.title;
    const chapExtra = ch.title && !ch.title.toLowerCase().includes('chap') ? ` [${ch.title}]` : '';
    const title = `Đọc ${m.title} ${chapNum}${chapExtra} Tiếng Việt Bản Đẹp | DokiTruyen`;
    const description = `📖 Đọc truyện tranh ${m.title} ${chapNum} tiếng Việt mới nhất online. Ảnh đẹp Full HD, load siêu tốc, không quảng cáo chen ngang tại DokiTruyen.`.slice(0, 160);

    const readerUrl = `${BASE_URL}/chuong/${ch.id}`;
    const mangaUrl = `${BASE_URL}/truyen-tranh/${m.id}`;
    const coverUrl = m.cover?.startsWith('http') ? m.cover : `${BASE_URL}${m.cover}`;

    const jsonLd = {
      '@context': 'https://schema.org',
      '@graph': [
        {
          '@type': ['ComicIssue', 'WebPage'],
          '@id': `${readerUrl}#issue`,
          'name': `${m.title} - ${chapNum}`,
          'url': readerUrl,
          'image': coverUrl,
          'issueNumber': String(ch.number || ''),
          'inLanguage': 'vi',
          'partOfSeries': {
            '@type': 'ComicSeries',
            'name': m.title,
            'url': mangaUrl
          }
        },
        {
          '@type': 'BreadcrumbList',
          '@id': `${readerUrl}#breadcrumb`,
          'itemListElement': [
            {
              '@type': 'ListItem',
              'position': 1,
              'name': 'Trang chủ',
              'item': `${BASE_URL}/`
            },
            {
              '@type': 'ListItem',
              'position': 2,
              'name': m.title,
              'item': mangaUrl
            },
            {
              '@type': 'ListItem',
              'position': 3,
              'name': chapNum,
              'item': readerUrl
            }
          ]
        }
      ]
    };

    this.setTags({
      title,
      description,
      keywords: `${m.title} chap ${ch.number}, doc truyen ${m.title} chap ${ch.number}, ${m.title} chap moi nhat, dokitruyen`,
      image: coverUrl,
      url: readerUrl,
      type: 'article',
      jsonLd
    });
  }

  // Home Page SEO
  setHomeSeo(): void {
    this.setTags({
      title: 'DokiTruyen - Đọc Truyện Tranh Online Miễn Phí (Manga, Manhwa, Manhua Mới Nhất)',
      description: '🔥 DokiTruyen (dokitruyen.com) - Trang đọc truyện tranh online miễn phí cập nhật mới nhất mỗi ngày. Hơn 10,000+ bộ Manga, Manhwa, Manhua tiếng Việt Full HD load cực nhanh!',
      keywords: 'dokitruyen, doki truyen, doc truyen tranh, truyen tranh online, manga, manhwa, manhua, doc truyen moi nhat, truyen tranh full hd',
      url: `${BASE_URL}/`,
      type: 'website'
    });
  }

  // Search / Browse SEO
  setSearchSeo(opts?: { query?: string; genreName?: string; country?: string; sort?: string }): void {
    let title = 'Tìm Truyện Tranh Nâng Cao - Lọc Thể Loại, Top BXH, Tác Giả | DokiTruyen';
    let description = '⚡ Bộ lọc tìm truyện tranh thông minh: Manga, Manhwa, Manhua theo thể loại, số chương, tác giả và bảng xếp hạng hay nhất được yêu thích tại DokiTruyen.';

    if (opts?.query) {
      title = `Tìm kiếm: "${opts.query}" - Truyện Tranh Online Mới Nhất | DokiTruyen`;
      description = `Kết quả tìm kiếm truyện tranh cho từ khóa "${opts.query}". Đọc truyện tranh online miễn phí bản dịch tiếng Việt chất lượng cao tại DokiTruyen.`;
    } else if (opts?.country === 'kr') {
      title = 'Truyện Tranh Manhwa (Hàn Quốc) Hay Nhất - Đọc Online Bản Đẹp | DokiTruyen';
      description = 'Tuyển chọn các bộ truyện tranh Manhwa Hàn Quốc mới nhất, cốt truyện hấp dẫn, full màu sắc nét load mượt mà tại DokiTruyen.';
    } else if (opts?.country === 'jp') {
      title = 'Truyện Tranh Manga (Nhật Bản) Mới Nhất - Đọc Online Miễn Phí | DokiTruyen';
      description = 'Đọc truyện tranh Manga Nhật Bản bản dịch tiếng Việt mới nhất, cập nhật liên tục từng giờ tại DokiTruyen.';
    } else if (opts?.country === 'cn') {
      title = 'Truyện Tranh Manhua (Trung Quốc) Full Màu Hot Nhất | DokiTruyen';
      description = 'Kho truyện Manhua Trung Quốc tu tiên, trọng sinh, đô thị full màu cực hay cập nhật mới nhất tại DokiTruyen.';
    } else if (opts?.genreName) {
      title = `Truyện Tranh Thể Loại ${opts.genreName} Mới Nhất - Đọc Online | DokiTruyen`;
      description = `Danh sách truyện tranh thể loại ${opts.genreName} hay nhất chọn lọc, cập nhật chap mới liên tục trên DokiTruyen.`;
    }

    this.setTags({
      title,
      description,
      url: `${BASE_URL}/tim-truyen-nang-cao`,
      type: 'website'
    });
  }

  // Author Page SEO
  setAuthorSeo(authorName: string): void {
    const title = `Tác Giả ${authorName} - Toàn Bộ Truyện Tranh Sáng Tác Hay Nhất | DokiTruyen`;
    const description = `Tuyển tập toàn bộ các bộ truyện tranh sáng tác và minh họa bởi tác giả ${authorName} online miễn phí mới nhất, hình ảnh sắc nét tại DokiTruyen.`;
    const authorUrl = `${BASE_URL}/tac-gia/${encodeURIComponent(authorName)}`;

    const jsonLd = {
      '@context': 'https://schema.org',
      '@graph': [
        {
          '@type': 'ProfilePage',
          '@id': `${authorUrl}#profile`,
          'name': `Tác giả ${authorName}`,
          'url': authorUrl,
          'mainEntity': {
            '@type': 'Person',
            'name': authorName
          }
        },
        {
          '@type': 'BreadcrumbList',
          '@id': `${authorUrl}#breadcrumb`,
          'itemListElement': [
            {
              '@type': 'ListItem',
              'position': 1,
              'name': 'Trang chủ',
              'item': `${BASE_URL}/`
            },
            {
              '@type': 'ListItem',
              'position': 2,
              'name': 'Tác giả',
              'item': `${BASE_URL}/tac-gia`
            },
            {
              '@type': 'ListItem',
              'position': 3,
              'name': authorName,
              'item': authorUrl
            }
          ]
        }
      ]
    };

    this.setTags({
      title,
      description,
      keywords: `tac gia ${authorName}, truyen cua ${authorName}, ${authorName} manga, dokitruyen`,
      url: authorUrl,
      type: 'profile' as any,
      jsonLd
    });
  }
}
