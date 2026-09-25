import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Manga, Page, message } from './core';
import { Icon, MangaCardComponent, Pagination } from './ui';
import { Sidebar } from './sidebar';

@Component({
  selector: 'app-home',
  imports: [RouterLink, Icon, MangaCardComponent, Pagination, Sidebar],
  template: `
<section class="recommendations">
  <div class="section-title">
    <h2><app-icon name="fire"/> Truyện Hot Manhwa</h2>
    <div class="carousel-controls">
      <button aria-label="Đề cử trước" (click)="rotate(-1)">‹</button>
      <button aria-label="Đề cử tiếp" (click)="rotate(1)">›</button>
    </div>
  </div>
  
  <div class="featured-carousel-track">
    @for(m of featured(); track m.id; let i = $index){
      <a class="featured-card" [routerLink]="['/truyen-tranh', m.id]">
        <img 
          [src]="m.cover" 
          [alt]="m.title" 
          [attr.fetchpriority]="i < 2 ? 'high' : 'auto'" 
          [loading]="i < 2 ? 'eager' : 'lazy'" 
          decoding="async" 
          referrerpolicy="no-referrer" 
          (load)="$any($event.target).classList.add('loaded')" 
          (error)="$any($event.target).classList.add('loaded')">
        <div class="featured-overlay">
          <span class="featured-badge">HOT #{{i + 1}}</span>
          <h3>{{m.title}}</h3>
          <p class="featured-sub">
            <app-icon name="book" style="width:12px;height:12px;"/>
            {{m.chapters && m.chapters[0] ? m.chapters[0].title : (m.author || 'Đang cập nhật')}}
          </p>
        </div>
      </a>
    }
    @if(!featured().length){
      @for(i of [1,2,3,4,5]; track i){
        <div class="skeleton featured-card"></div>
      }
    }
  </div>
</section>

<div class="columns">
  <section class="main-content-section">
    <div class="section-title">
      <h1><app-icon name="clock"/> Truyện Manga Mới Cập Nhật</h1>
      <a class="filter-shortcut-btn" routerLink="/tim-truyen-nang-cao" aria-label="Lọc truyện nâng cao">
        <app-icon name="filter"/>
        <span class="btn-text">Bộ lọc</span>
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
      <app-pagination [page]="page()" [total]="total()" [size]="28" [change]="goPage"/>
    }
  </section>

  <app-sidebar/>
</div>
`
})
export class Home {
  api = inject(Api);
  route = inject(ActivatedRoute);
  router = inject(Router);

  items = signal<Manga[]>([]);
  pool = signal<Manga[]>([]);
  featured = signal<Manga[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(true);
  error = signal('');
  skeletons = Array.from({ length: 12 }, (_, i) => i);
  offset = 0;
  epoch = 0;

  constructor() {
    this.route.queryParamMap.subscribe(p => {
      this.page.set(Math.max(1, Number(p.get('page')) || 1));
      void this.load();
    });
    void this.loadFeatured();
  }

  async loadFeatured() {
    try {
      const r = await this.api.request<Page<Manga>>('/catalog/featured?limit=20', 'GET', undefined, true);
      if (r.items && r.items.length) {
        this.pool.set(r.items);
        this.rotate(0);
      }
    } catch { }
  }

  rotate(step: number) {
    const pool = this.pool();
    if (!pool.length) return;
    this.offset = (this.offset + step + pool.length) % pool.length;
    this.featured.set(Array.from({ length: Math.min(6, pool.length) }, (_, i) => pool[(i + this.offset) % pool.length]));
  }

  async load() {
    const n = ++this.epoch;
    this.loading.set(true);
    this.error.set('');
    try {
      const r = await this.api.request<Page<Manga>>('/catalog/home?page=' + this.page(), 'GET', undefined, true);
      if (n === this.epoch) {
        this.items.set(r.items);
        this.total.set(r.total);
      }
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }

  goPage = (page: number) => {
    void this.router.navigate(['/'], { queryParams: { page } });
    window.scrollTo({ top: 350, behavior: 'smooth' });
  };
}
