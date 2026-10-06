import { Component, inject, signal } from '@angular/core';
import { Title, Meta } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Store, Manga, Page, message } from './core';
import { Icon, MangaCardComponent, Pagination } from './ui';

@Component({
  selector: 'app-author',
  imports: [RouterLink, FormsModule, Icon, MangaCardComponent, Pagination],
  template: `
<div class="author-page-container">
  <!-- Breadcrumb -->
  <div class="breadcrumb">
    <a routerLink="/">Trang chủ</a>
    <span class="breadcrumb-sep">›</span>
    <span class="breadcrumb-sep">Tác giả</span>
    @if(name()){
      <span class="breadcrumb-sep">›</span>
      <span class="current-crumb">{{name()}}</span>
    }
  </div>

  <!-- Author Hero Banner -->
  <section class="author-hero glass-panel">
    <div class="author-hero-content">
      <div class="author-avatar-badge">
        <span class="author-avatar-letter">{{(name() || 'A').slice(0, 1).toUpperCase()}}</span>
      </div>

      <div class="author-details">
        <div class="author-title-row">
          <h1>{{name() || 'Tìm kiếm tác giả'}}</h1>
          @if(name()){
            <span class="author-count-pill">{{total().toLocaleString('vi')}} bộ truyện</span>
          }
        </div>
        <p class="author-subtitle">
          @if(name()){
            Tổng hợp danh sách các bộ truyện tranh sáng tác và minh họa bởi <strong>{{name()}}</strong>
          }@else{
            Nhập tên tác giả hoặc họa sĩ để tìm kiếm tất cả các bộ truyện liên quan
          }
        </p>

        <!-- Quick Author Search Form -->
        <form class="author-search-bar" (ngSubmit)="searchAuthor()">
          <div class="author-search-input-field">
            <app-icon name="user" class="field-icon"/>
            <input type="search" name="authorQuery" [(ngModel)]="searchQuery" placeholder="Nhập tên tác giả khác (ví dụ: Ryon, Izumi, Oda...)" aria-label="Tìm theo tên tác giả" autocomplete="off">
            @if(searchQuery){
              <button type="button" class="btn-clear-author-input" aria-label="Xóa" (click)="searchQuery=''">×</button>
            }
          </div>
          <button type="submit" class="primary btn-author-search"><app-icon name="search"/> Tìm tác giả</button>
        </form>
      </div>
    </div>
  </section>

  @if(name()){
    <!-- Results Header with Sort Controls -->
    <div class="section-title author-results-header">
      <div class="results-heading">
        <h2><app-icon name="book"/> Truyện của tác giả {{name()}}</h2>
        <span class="muted">{{total().toLocaleString('vi')}} kết quả</span>
      </div>

      <div class="author-sort-wrap">
        <label for="author-sort-select">Sắp xếp:</label>
        <select id="author-sort-select" [(ngModel)]="sort" (ngModelChange)="onSortChange()">
          <option value="latest">Mới cập nhật chap</option>
          <option value="hot">Theo dõi nhiều nhất</option>
          <option value="rating">Đánh giá cao nhất</option>
          <option value="new">Truyện mới đăng</option>
          <option value="title">Bảng chữ cái (A-Z)</option>
        </select>
      </div>
    </div>

    @if(error()){
      <div class="error-state glass-panel">
        <app-icon name="info"/>
        <p>{{error()}}</p>
        <button (click)="load()" class="primary">Thử lại</button>
      </div>
    }

    @if(loading()){
      <div class="loading-panel">
        <div class="search-spinner" style="width:36px;height:36px;margin:0 auto 14px;"></div>
        <p>Đang tìm tất cả truyện của tác giả {{name()}}…</p>
      </div>
    }@else{
      <div class="author-manga-grid">
        @for(m of items(); track m.id){
          <app-card [manga]="m"/>
        }
      </div>

      @if(!items().length && !error()){
        <div class="empty-state glass-panel">
          <app-icon name="user"/>
          <h2>Chưa tìm thấy truyện của tác giả "{{name()}}"</h2>
          <p>Hãy thử kiểm tra lại chính tả hoặc tìm tên tác giả bằng từ khóa ngắn gọn hơn.</p>
        </div>
      }

      @if(total() > 24){
        <app-pagination [page]="page()" [total]="total()" [size]="24" [change]="goPage"/>
      }
    }
  }
</div>
`
})
export class AuthorComponent {
  api = inject(Api);
  store = inject(Store);
  route = inject(ActivatedRoute);
  router = inject(Router);
  titleService = inject(Title);
  metaService = inject(Meta);

  name = signal('');
  searchQuery = '';
  sort = 'latest';
  items = signal<Manga[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(false);
  error = signal('');
  epoch = 0;

  constructor() {
    this.route.paramMap.subscribe(params => {
      const paramName = params.get('name');
      if (paramName) {
        this.name.set(paramName);
        this.searchQuery = paramName;
        this.page.set(Math.max(1, Number(this.route.snapshot.queryParamMap.get('page')) || 1));
        this.sort = this.route.snapshot.queryParamMap.get('sort') || 'latest';
        this.updateTitle(paramName);
        void this.load();
      } else {
        this.route.queryParamMap.subscribe(qp => {
          const qpName = qp.get('name');
          if (qpName) {
            this.name.set(qpName);
            this.searchQuery = qpName;
            this.page.set(Math.max(1, Number(qp.get('page')) || 1));
            this.sort = qp.get('sort') || 'latest';
            this.updateTitle(qpName);
            void this.load();
          }
        });
      }
    });
  }

  updateTitle(authorName: string) {
    this.titleService.setTitle(`Tác giả ${authorName} - Danh Sách Truyện Tranh | AkaTruyen`);
    this.metaService.updateTag({
      name: 'description',
      content: `Xem toàn bộ các bộ truyện tranh sáng tác và minh họa bởi tác giả ${authorName} online miễn phí mới nhất tại AkaTruyen.`
    });
  }

  searchAuthor() {
    if (!this.searchQuery.trim()) return;
    void this.router.navigate(['/tac-gia', this.searchQuery.trim()]);
  }

  onSortChange() {
    this.page.set(1);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { sort: this.sort, page: 1 },
      queryParamsHandling: 'merge'
    });
    void this.load();
  }

  goPage = (p: number) => {
    this.page.set(p);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { page: p },
      queryParamsHandling: 'merge'
    });
    window.scrollTo({ top: 0, behavior: 'smooth' });
    void this.load();
  };

  async load() {
    const authorName = this.name();
    if (!authorName) return;

    const p = this.page();
    const path = `/catalog/author/${encodeURIComponent(authorName)}?page=${p}&pageSize=24&sort=${this.sort}`;
    const n = ++this.epoch;
    this.error.set('');

    const cached = this.api.getCached<Page<Manga>>(path);
    if (cached && cached.items) {
      this.items.set(cached.items);
      this.total.set(cached.total);
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    try {
      const r = await this.api.request<Page<Manga>>(path, 'GET', undefined, true);
      if (n === this.epoch) {
        this.items.set(r.items || []);
        this.total.set(r.total || 0);
      }
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }
}
