import { Component, computed, effect, inject, signal, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Manga, Page, Store, cleanDescription, message, statuses } from './core';
import { SeoService } from './seo.service';
import { Icon, MangaCardComponent, Pagination } from './ui';
import { Sidebar } from './sidebar';
import { BannerImage, bannerImageScore, originalBannerCover, rankSpotlightCandidates } from './spotlight';

@Component({
  selector: 'app-home',
  imports: [RouterLink, Icon, MangaCardComponent, Pagination, Sidebar],
  template: `
<section class="doki-spotlight" aria-label="Truyện nổi bật" (mouseenter)="pauseAutoSlide()" (mouseleave)="resumeAutoSlide()">
  @if(spotlight(); as m){
    <img class="spotlight-backdrop" [src]="spotlightCovers()[m.id] || m.cover" alt="" fetchpriority="high" referrerpolicy="no-referrer" (error)="rejectSpotlight(m.id)">
    <div class="spotlight-shade"></div>
    <div class="spotlight-content">
      <span class="spotlight-eyebrow"><span></span> TÂM ĐIỂM HÔM NAY</span>
      <div class="spotlight-meta"><span class="spotlight-status">{{statuses[m.status] || 'Đang cập nhật'}}</span><span class="spotlight-rating"><app-icon name="star"/> {{m.rating.toFixed(1)}}</span><span>{{m.country === 'ko' ? 'Manhwa' : m.country === 'cn' ? 'Manhua' : 'Manga'}}</span><span>DokiTruyen</span></div>
      <h1>{{m.title}}</h1>
      <p>{{description(m)}}</p>
      <div class="spotlight-actions">
        <a class="spotlight-read" [routerLink]="m.chapters.length ? ['/chuong', m.chapters[0].id] : ['/truyen-tranh', m.id]" (click)="savePosition(m.id)"><app-icon name="play"/> Đọc ngay</a>
        <a class="spotlight-detail" [routerLink]="['/truyen-tranh', m.id]" (click)="savePosition(m.id)"><app-icon name="info"/> Chi tiết</a>
        <button class="spotlight-save" [disabled]="following()" [class.saved]="isFollowed(m.id)" [attr.aria-label]="isFollowed(m.id) ? 'Bỏ theo dõi truyện' : 'Theo dõi truyện'" [attr.aria-pressed]="isFollowed(m.id)" (click)="toggleFollow(m)"><app-icon name="bookmark"/></button>
      </div>
      <div class="spotlight-controls">
        <button class="spotlight-pause" [attr.aria-label]="autoPaused() ? 'Tiếp tục chuyển truyện' : 'Tạm dừng chuyển truyện'" [attr.aria-pressed]="autoPaused()" (click)="autoPaused.set(!autoPaused())">{{autoPaused() ? '▶' : 'Ⅱ'}}</button>
        <div class="spotlight-dots" aria-label="Chọn truyện nổi bật">
          @for(item of spotlightPool(); track item.id; let i = $index){<button [class.active]="spotlightIndex() === i" [attr.aria-label]="'Hiển thị truyện nổi bật ' + (i + 1)" [attr.aria-pressed]="spotlightIndex() === i" (click)="selectSpotlight(i)"></button>}
        </div>
      </div>
    </div>
  }@else{
    <div class="spotlight-content"><span class="spotlight-eyebrow">KHÁM PHÁ DOKITRUYEN</span><h1>Mỗi trang truyện,<br>một thế giới mới.</h1><p>Khám phá Manga, Manhwa và Manhua. Những câu chuyện yêu thích luôn chờ bạn.</p><a class="spotlight-read" routerLink="/tim-truyen-nang-cao"><app-icon name="compass"/> Khám phá ngay</a></div>
  }
</section>


<div class="columns">
  <section class="main-content-section">
    <div class="section-title">
      <div><h2>Mới cập nhật</h2><p class="section-subtitle">Truyện và các chương vừa được cập nhật</p></div>
      <a class="view-all-btn" routerLink="/tim-truyen-nang-cao" [queryParams]="{sort:'latest'}" title="Xem tất cả truyện mới cập nhật">
        <span class="desktop-text">Xem tất cả</span>
        <span class="mobile-text">Tất cả</span>
        <app-icon name="arrowRight"/>
      </a>
    </div>

    <div class="list-meta-bar">
      <span>Cập nhật mới nhất → cũ nhất</span>
      <span class="total-badge">{{total().toLocaleString('vi')}} truyện</span>
    </div>

    @if(error()){
      <div class="error-state glass-panel">
        <app-icon name="info"/>
        <h3>Chưa thể tải danh sách truyện</h3>
        <p>{{error()}}</p>
        <button class="primary" (click)="load()">Thử lại</button>
      </div>
    }

    <div class="manga-grid">
      @if(loading()){
        @for(i of skeletons; track i){
          <div class="skeleton skeleton-card"></div>
        }
      }@else{
        @for(m of items(); track m.id){
          <app-card [manga]="m"/>
        }
      }
    </div>

    @if(!loading() && !error()){
      <app-pagination [page]="page()" [total]="total()" [size]="28" [change]="goPage" [hover]="prefetchPage"/>
    }
  </section>


</div>
<section class="recommendations"
         (mouseenter)="pauseAutoSlide()"
         (mouseleave)="resumeAutoSlide()"
         (touchstart)="pauseAutoSlide()"
         (touchend)="resumeAutoSlide()">
  <div class="section-title">
    <div><h2>Manhwa nổi bật</h2><p class="section-subtitle">Những câu chuyện đáng để khám phá</p></div>
    <a class="view-all-manhwa-link" routerLink="/tim-truyen-nang-cao" [queryParams]="{country:'ko'}">
      <span class="desktop-text">Xem tất cả</span>
      <span class="mobile-text">Tất cả Manhwa</span>
      <app-icon name="arrowRight"/>
    </a>
  </div>

  <div class="featured-carousel-track">
    @for(m of featured(); track m.id; let i = $index){
      <a class="featured-card"
         [id]="'manga-card-' + m.id"
         [attr.data-manga-id]="m.id"
         [routerLink]="['/truyen-tranh', m.id]"
         (click)="savePosition(m.id)"
         (mouseenter)="api.prefetchDetail(m.id)"
         (touchstart)="api.prefetchDetail(m.id)">
        <img
          [src]="m.cover"
          [alt]="m.title"
          [attr.fetchpriority]="i < 2 ? 'high' : 'auto'"
          [loading]="i < 2 ? 'eager' : 'lazy'"
          decoding="async"
          referrerpolicy="no-referrer"
          (load)="$any($event.target).classList.add('loaded')"
          (error)="coverFallback($event)">
        <div class="featured-overlay">
          <span class="featured-badge"><app-icon name="star"/> {{m.rating.toFixed(1)}}</span>
          <h3>{{m.title}}</h3>
          <p class="featured-sub">
            <app-icon name="book" style="width:12px;height:12px;"/>
            {{m.chapters && m.chapters[0] ? m.chapters[0].title : (m.author || 'Đang cập nhật')}}
          </p>
        </div>
      </a>
    }
    @if(featuredLoading()){
      @for(i of [1,2,3,4,5]; track i){
        <div class="skeleton featured-card"></div>
      }
    }
  </div>
  @if(featuredError()){
    <div class="featured-empty"><span>Đề cử đang tạm gián đoạn.</span><button (click)="loadFeatured()">Tải lại <app-icon name="arrowRight"/></button></div>
  }
</section>
<section class="home-trending" aria-label="Truyện thịnh hành">
  <div class="section-title">
    <div><h2>Thịnh hành</h2><p class="section-subtitle">Những bộ truyện được cộng đồng theo dõi nhiều nhất</p></div>
    <a class="view-all-btn" routerLink="/tim-truyen-nang-cao" [queryParams]="{sort:'hot'}">Xem tất cả <app-icon name="arrowRight"/></a>
  </div>
  @if(trendingLoading()){
    <div class="manga-grid">@for(i of [1,2,3,4,5]; track i){<div class="skeleton skeleton-card"></div>}</div>
  }@else if(trendingError()){
    <div class="featured-empty"><span>Chưa thể tải truyện thịnh hành.</span><button (click)="loadTrending()">Thử lại <app-icon name="arrowRight"/></button></div>
  }@else{
    <div class="manga-grid">@for(m of trending(); track m.id){<app-card [manga]="m" [idPrefix]="'trending-'"/>}</div>
  }
</section>
<section class="home-community" aria-label="Cộng đồng đọc truyện"><app-sidebar/></section>

`
})
export class Home implements OnDestroy {
  api = inject(Api);
  route = inject(ActivatedRoute);
  router = inject(Router);
  seo = inject(SeoService);
  store = inject(Store);
  statuses = statuses;
  autoPaused = signal(typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  spotlightIndex = signal(0);
  private rankedSpotlights = signal<Manga[]>([]);
  private failedSpotlights = signal<string[]>([]);
  private latestSpotlights = signal<Manga[]>([]);
  private selectingSpotlight = signal(false);
  private selectionEpoch = 0;
  spotlightPool = computed(() => this.rankedSpotlights().filter(m => !this.failedSpotlights().includes(m.id)));
  spotlight = computed(() => this.spotlightPool()[this.spotlightIndex() % (this.spotlightPool().length || 1)]);
  descriptions = signal<Record<string, string>>({});
  spotlightCovers = signal<Record<string, string>>({});
  trending = signal<Manga[]>([]);
  private trendingSpotlights = signal<Manga[]>([]);
  trendingLoading = signal(true);
  trendingError = signal(false);
  description(m: Manga) {
    return cleanDescription(this.descriptions()[m.id] || m.description) === 'Chưa có mô tả cho bộ truyện này.'
      ? 'Khám phá câu chuyện mới, theo dõi chương mới nhất và tiếp tục hành trình cùng DokiTruyen.'
      : cleanDescription(this.descriptions()[m.id] || m.description);
  }
  following = signal(false);
  isFollowed(id: string) { return this.store.isFollowed(id); }
  async toggleFollow(m: Manga) {
    if (!this.store.user()) { void this.router.navigate(['/dang-nhap']); return; }
    this.following.set(true);
    try { await this.store.follow(m); } catch (e) { this.store.notify(message(e)); }
    finally { this.following.set(false); }
  }
  selectSpotlight(index: number) { this.spotlightIndex.set(index); this.startAutoSlide(); }

  items = signal<Manga[]>([]);
  pool = signal<Manga[]>([]);
  featured = signal<Manga[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(true);
  error = signal('');
  featuredLoading = signal(true);
  featuredError = signal(false);
  skeletons = Array.from({ length: 12 }, (_, i) => i);
  offset = 0;
  epoch = 0;
  private autoSlideTimer?: ReturnType<typeof setInterval>;
  isPaused = false;

  constructor() {
    effect(onCleanup => {
      const candidates = rankSpotlightCandidates([...this.pool(), ...this.trendingSpotlights(), ...this.latestSpotlights()]).slice(0, 40);
      const epoch = ++this.selectionEpoch;
      if (!candidates.length) { this.selectingSpotlight.set(false); return; }
      // Batch independently arriving feeds; only three image checks run at once.
      const timer = setTimeout(() => { void this.chooseSpotlights(candidates, epoch); }, 200);
      onCleanup(() => { clearTimeout(timer); ++this.selectionEpoch; });
    });
    this.seo.setHomeSeo();
    this.route.queryParamMap.subscribe(p => {
      this.page.set(Math.max(1, Number(p.get('page')) || 1));
      void this.load();
    });
    void this.loadFeatured();
    void this.loadTrending();
    this.startAutoSlide();
  }

  ngOnDestroy() {
    ++this.selectionEpoch;
    this.stopAutoSlide();
  }

  rejectSpotlight(id: string) {
    this.failedSpotlights.update(ids => [...ids, id]);
    this.spotlightIndex.set(0);
  }

  private probeBanner(url: string): Promise<BannerImage | null> {
    return new Promise(resolve => {
      const image = new Image();
      let finished = false;
      const finish = (result: BannerImage | null) => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        image.onload = image.onerror = null;
        resolve(result);
      };
      const timer = setTimeout(() => finish(null), 5000);
      image.referrerPolicy = 'no-referrer';
      image.onload = () => finish({ url, width: image.naturalWidth, height: image.naturalHeight });
      image.onerror = () => finish(null);
      image.src = url;
    });
  }

  private async chooseSpotlights(candidates: Manga[], epoch: number) {
    this.selectingSpotlight.set(true);
    const images: Record<string, BannerImage> = {};
    const descriptions: Record<string, string> = {};
    let cursor = 0;
    const worker = async () => {
      while (cursor < candidates.length && Object.keys(images).length < 10 && epoch === this.selectionEpoch) {
        const m = candidates[cursor++];
        let detail = m;
        try { detail = await this.api.request<Manga>('/catalog/' + m.id, 'GET', undefined, true); } catch { }
        if (epoch !== this.selectionEpoch) return;
        descriptions[m.id] = detail.description || m.description;
        const urls = [...new Set([originalBannerCover(detail.cover), detail.cover, m.cover])]
          .filter(url => url && !url.includes('cover-placeholder'));
        for (const url of urls) {
          const image = await this.probeBanner(url);
          if (epoch !== this.selectionEpoch) return;
          if (image && bannerImageScore(image) > bannerImageScore(images[m.id] || { url: '', width: 0, height: 0 })) images[m.id] = image;
          if (image && image.width >= 1000) break;
        }
      }
    };
    await Promise.all(Array.from({ length: Math.min(3, candidates.length) }, worker));
    if (epoch !== this.selectionEpoch) return;
    const selected = rankSpotlightCandidates(candidates, Date.now(), images).slice(0, 10);
    this.descriptions.set(descriptions);
    this.spotlightCovers.set(Object.fromEntries(selected.map(m => [m.id, images[m.id].url])));
    this.failedSpotlights.set([]);
    this.rankedSpotlights.set(selected);
    this.spotlightIndex.set(0);
    this.selectingSpotlight.set(false);
    this.startAutoSlide();
  }

  async loadTrending() {
    this.trendingLoading.set(true);
    this.trendingError.set(false);
    try {
      const result = await this.api.request<Page<Manga>>('/catalog/search?sort=hot&pageSize=28&language=vi', 'GET', undefined, true);
      this.trendingSpotlights.set(result.items);
      this.trending.set(result.items.slice(0, 10));
    } catch { this.trendingError.set(true); }
    finally { this.trendingLoading.set(false); }
  }

  savePosition(id?: string) {
    if (typeof window !== 'undefined') {
      sessionStorage.setItem('home_scroll_y', window.scrollY.toString());
      if (id) sessionStorage.setItem('home_manga_id', id);
    }
  }

  restorePosition() {
    if (typeof window === 'undefined') return;
    const savedY = sessionStorage.getItem('home_scroll_y');
    const savedMangaId = sessionStorage.getItem('home_manga_id');
    if (!savedY && !savedMangaId) return;

    setTimeout(() => {
      let restored = false;
      if (savedMangaId) {
        const targetEl = document.getElementById('manga-card-' + savedMangaId) ||
                         document.querySelector(`[data-manga-id="${savedMangaId}"]`);
        if (targetEl) {
          const rect = targetEl.getBoundingClientRect();
          const targetY = window.scrollY + rect.top - 80;
          window.scrollTo({ top: Math.max(0, targetY), behavior: 'instant' });
          targetEl.classList.add('last-viewed-highlight');
          setTimeout(() => targetEl.classList.remove('last-viewed-highlight'), 2500);
          restored = true;
        }
      }

      if (!restored && savedY) {
        const y = Number(savedY);
        if (!isNaN(y) && y > 0) {
          window.scrollTo({ top: y, behavior: 'instant' });
        }
      }

      sessionStorage.removeItem('home_scroll_y');
      sessionStorage.removeItem('home_manga_id');
    }, 60);
  }

  async loadFeatured() {
    this.featuredLoading.set(true);
    this.featuredError.set(false);
    try {
      const r = await this.api.request<Page<Manga>>('/catalog/featured?limit=20', 'GET', undefined, true);
      if (r.items && r.items.length) {
        this.pool.set(r.items);
        this.rotate(0);
        this.startAutoSlide();
      }
    } catch { this.featuredError.set(true); }
    finally { this.featuredLoading.set(false); }
  }

  startAutoSlide() {
    this.stopAutoSlide();
    if (typeof window === 'undefined') return;
    this.autoSlideTimer = setInterval(() => {
      if (!this.isPaused && !this.autoPaused() && !this.selectingSpotlight() && this.spotlightPool().length > 1 && !document.hidden) {
        this.spotlightIndex.update(i => (i + 1) % this.spotlightPool().length);
      }
    }, 6500);
  }

  stopAutoSlide() {
    if (this.autoSlideTimer) {
      clearInterval(this.autoSlideTimer);
      this.autoSlideTimer = undefined;
    }
  }

  pauseAutoSlide() {
    this.isPaused = true;
  }

  resumeAutoSlide() {
    this.isPaused = false;
  }

  manualRotate(step: number) {
    this.rotate(step);
    this.startAutoSlide();
  }

  coverFallback(event: Event) {
    const image = event.target as HTMLImageElement;
    if (!image.src.endsWith('/cover-placeholder.svg')) image.src = '/cover-placeholder.svg';
    image.classList.add('loaded');
  }

  rotate(step: number) {
    const pool = this.pool();
    if (!pool.length) return;
    this.offset = (this.offset + step + pool.length) % pool.length;
    this.featured.set(Array.from({ length: Math.min(6, pool.length) }, (_, i) => pool[(i + this.offset) % pool.length]));
  }

  prefetchPage = (p: number) => {
    if (p >= 1) {
      this.api.prefetch('/catalog/home?page=' + p);
    }
  };

  prefetchAdjacent() {
    const p = this.page();
    const max = Math.ceil(this.total() / 28) || 999;
    if (p < max) this.prefetchPage(p + 1);
    if (p > 1) this.prefetchPage(p - 1);
    if (p + 1 < max) {
      setTimeout(() => {
        if (this.page() === p) this.prefetchPage(p + 2);
      }, 400);
    }
  }

  async load() {
    const p = this.page();
    const path = '/catalog/home?page=' + p;
    const n = ++this.epoch;
    this.error.set('');

    const cached = this.api.getCached<Page<Manga>>(path);
    if (cached && cached.items) {
      this.items.set(cached.items);
      if (p === 1) this.latestSpotlights.set(cached.items);
      this.total.set(cached.total);
      this.loading.set(false);
      this.prefetchAdjacent();
      this.restorePosition();
      return;
    }

    this.loading.set(true);
    try {
      const r = await this.api.request<Page<Manga>>(path, 'GET', undefined, true);
      if (n === this.epoch) {
        this.items.set(r.items);
        if (p === 1) this.latestSpotlights.set(r.items);
        this.total.set(r.total);
        this.prefetchAdjacent();
        this.restorePosition();
      }
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }

  goPage = (page: number) => {
    this.prefetchPage(page);
    void this.router.navigate(['/'], { queryParams: { page } });
    const section = document.querySelector('.main-content-section');
    const targetY = section ? window.scrollY + section.getBoundingClientRect().top - 96 : 0;
    window.scrollTo({ top: Math.max(0, targetY), behavior: 'smooth' });
  };
}
