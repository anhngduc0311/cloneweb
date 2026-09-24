import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Manga, Page, message } from './core';
import { Icon, MangaCardComponent, Pagination } from './ui';
import { Sidebar } from './sidebar';
@Component({
  selector: 'app-home', imports: [RouterLink, Icon, MangaCardComponent, Pagination, Sidebar], template: `
<section class="recommendations"><div class="section-title"><h2><app-icon name="fire"/> Truyện đề cử</h2><div class="carousel-controls"><button aria-label="Đề cử trước" (click)="rotate(-1)">‹</button><button aria-label="Đề cử tiếp" (click)="rotate(1)">›</button></div></div><div class="featured-grid">@for(m of featured();track m.id){<a class="featured-card" [routerLink]="['/nettrom/truyen-tranh',m.id]"><img [src]="m.cover" [alt]="m.title" referrerpolicy="no-referrer"><div><h3>{{m.title}}</h3><p>{{m.author}}</p></div></a>}@if(!featured().length){@for(i of [1,2,3,4,5];track i){<div class="skeleton featured-card"></div>}}</div></section>
<div class="columns"><section><div class="section-title"><h1><app-icon name="clock"/> Truyện mới cập nhật <app-icon name="chevron"/></h1><a class="round-button" routerLink="/nettrom/tim-truyen-nang-cao" aria-label="Lọc truyện"><app-icon name="filter"/></a></div><p class="list-caption">Cập nhật mới nhất → cũ nhất <span>{{total().toLocaleString('vi')}} truyện</span></p>
@if(error()){<div class="error-state"><app-icon name="info"/><h3>Chưa thể tải danh sách truyện</h3><p>{{error()}}</p><button class="primary" (click)="load()">Thử lại</button></div>}
<div class="manga-grid">@if(loading()){@for(i of skeletons;track i){<div class="skeleton skeleton-card"></div>}}@else{@for(m of items();track m.id){<app-card [manga]="m"/>}}</div>
@if(!loading()&&!error()){<app-pagination [page]="page()" [total]="total()" [size]="28" [change]="goPage"/>}</section><app-sidebar/></div>`})
export class Home {
  api = inject(Api); route = inject(ActivatedRoute); router = inject(Router); items = signal<Manga[]>([]); pool = signal<Manga[]>([]); featured = signal<Manga[]>([]); total = signal(0); page = signal(1); loading = signal(true); error = signal(''); skeletons = Array.from({ length: 12 }, (_, i) => i); offset = 0; epoch = 0;
  constructor() { this.route.queryParamMap.subscribe(p => { this.page.set(Math.max(1, Number(p.get('page')) || 1)); void this.load(); }); void this.api.request<Page<Manga>>('/catalog/search?sort=rating&pageSize=10').then(r => { this.pool.set(r.items); this.rotate(0); }).catch(() => { }); }
  rotate(step: number) { const pool = this.pool(); if (!pool.length) return; this.offset = (this.offset + step + pool.length) % pool.length; this.featured.set(Array.from({ length: Math.min(5, pool.length) }, (_, i) => pool[(i + this.offset) % pool.length])); }
  async load() { const n = ++this.epoch; this.loading.set(true); this.error.set(''); try { const r = await this.api.request<Page<Manga>>('/catalog/home?page=' + this.page()); if (n === this.epoch) { this.items.set(r.items); this.total.set(r.total); if (!this.pool().length) { this.pool.set(r.items.filter(x => x.contentRating === 'safe')); this.rotate(0); } } } catch (e) { if (n === this.epoch) this.error.set(message(e)); } finally { if (n === this.epoch) this.loading.set(false); } }
  goPage = (page: number) => { void this.router.navigate(['/nettrom'], { queryParams: { page } }); window.scrollTo({ top: 420, behavior: 'smooth' }); };
}
