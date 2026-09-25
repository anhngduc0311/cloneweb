import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Store, message, ago } from './core';
import { Icon } from './ui';

@Component({
  selector: 'app-auth',
  imports: [FormsModule, RouterLink, Icon],
  template: `
<div class="auth-wrap">
  <section class="auth-panel glass-panel">
    <a class="brand auth-brand" routerLink="/" aria-label="AkaTruyen - Trang chủ">
      <img class="brand-symbol" src="/brand-mark.svg" width="44" height="44" alt="" aria-hidden="true"><span class="brand-wordmark">aka<span>truyen</span><span class="brand-period">.</span></span>
    </a>
    <h1>{{register() ? 'Tạo tài khoản' : 'Chào mừng trở lại'}}</h1>
    <p class="muted">{{register() ? 'Lưu những bộ truyện yêu thích và tiếp tục hành trình đọc truyện của bạn.' : 'Đăng nhập để đồng bộ truyện theo dõi và lịch sử đọc trên mọi thiết bị.'}}</p>

    <form class="auth-form" (ngSubmit)="submit()">
      @if(register()){
        <label>
          <span>Tên hiển thị</span>
          <input name="name" [(ngModel)]="name" required minlength="2" maxlength="60" autocomplete="nickname" placeholder="Ví dụ: Hoàng Long">
        </label>
      }

      <label>
        <span>Địa chỉ Email</span>
        <input type="email" name="email" [(ngModel)]="email" required maxlength="254" autocomplete="email" placeholder="name@example.com">
      </label>

      <label>
        <span>Mật khẩu</span>
        <input type="password" name="password" [(ngModel)]="password" required [minlength]="register() ? 6 : 1" maxlength="128" [autocomplete]="register() ? 'new-password' : 'current-password'" placeholder="Nhập mật khẩu">
      </label>

      @if(register()){
        <label>
          <span>Xác nhận mật khẩu</span>
          <input type="password" name="confirm" [(ngModel)]="confirm" required autocomplete="new-password" placeholder="Nhập lại mật khẩu">
        </label>
      }

      @if(error()){
        <p class="error" role="alert">{{error()}}</p>
      }

      <button class="primary btn-auth-submit" [disabled]="busy()">
        {{busy() ? 'Đang xử lý…' : register() ? 'Đăng ký tài khoản' : 'Đăng nhập'}} 
        <app-icon name="arrowRight"/>
      </button>
    </form>

    <div class="auth-switch-row">
      <span>{{register() ? 'Đã có tài khoản?' : 'Chưa có tài khoản?'}}</span>
      <a [routerLink]="register() ? '/dang-nhap' : '/dang-ky'" [queryParams]="{returnUrl: returnUrl}">
        {{register() ? 'Đăng nhập ngay' : 'Đăng ký ngay'}}
      </a>
    </div>
    <small class="auth-note muted">AkaTruyen · Nền tảng đọc truyện online miễn phí</small>
  </section>
</div>`
})
export class Auth {
  store = inject(Store);
  route = inject(ActivatedRoute);
  router = inject(Router);

  register = signal(false);
  busy = signal(false);
  error = signal('');
  name = '';
  email = '';
  password = '';
  confirm = '';
  returnUrl = '/';

  constructor() {
    this.route.url.subscribe(() => {
      this.register.set(this.router.url.startsWith('/dang-ky'));
      this.error.set('');
    });
    const ret = this.route.snapshot.queryParamMap.get('returnUrl');
    if (ret?.startsWith('/')) this.returnUrl = ret;
  }

  async submit() {
    if (this.register() && this.password !== this.confirm) {
      this.error.set('Mật khẩu xác nhận chưa trùng khớp.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      await this.store.login(this.email, this.password, this.register() ? this.name : undefined);
      this.store.notify(this.register() ? 'Đăng ký và đăng nhập thành công!' : 'Đăng nhập thành công!');
      void this.router.navigateByUrl(this.returnUrl);
    } catch (e) {
      this.error.set(message(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-library',
  imports: [RouterLink, Icon],
  template: `
<div class="library-page-container">
  <div class="section-title">
    <h1>
      <app-icon [name]="history() ? 'clock' : 'heart'"/> 
      {{history() ? 'Lịch sử đọc truyện' : 'Truyện đang theo dõi'}}
    </h1>
    <span class="muted total-count-pill">{{items().length}} bộ</span>
  </div>

  <div class="library-sub-bar">
    <p class="muted">{{store.user() ? 'Dữ liệu được đồng bộ an toàn với tài khoản của bạn.' : 'Dữ liệu được lưu trên trình duyệt này. Hãy đăng nhập để đồng bộ giữa máy tính và điện thoại.'}}</p>
    
    <div class="library-nav-pills">
      <a routerLink="/theo-doi" [class.active]="!history()" class="lib-pill-btn">
        <app-icon name="heart"/> Theo dõi ({{store.follows().length}})
      </a>
      <a routerLink="/lich-su" [class.active]="history()" class="lib-pill-btn">
        <app-icon name="clock"/> Lịch sử ({{store.history().length}})
      </a>
    </div>
  </div>

  @if(!history() && !store.user() && !items().length){
    <div class="empty-state glass-panel">
      <app-icon name="heart"/>
      <h2>Giữ những bộ truyện bạn yêu thích</h2>
      <p>Đăng nhập để theo dõi truyện và nhận thông báo khi có chương mới.</p>
      <a class="primary" routerLink="/dang-nhap" [queryParams]="{returnUrl: '/theo-doi'}">Đăng nhập ngay</a>
    </div>
  }@else{
    <div class="library-grid">
      @for(item of items(); track item.mangaId){
        <article class="library-card glass-panel">
          <a class="lib-cover-link" [routerLink]="['/truyen-tranh', item.mangaId]">
            <img [src]="item.cover" [alt]="item.title" referrerpolicy="no-referrer" loading="lazy">
          </a>
          <div class="lib-content">
            <h2><a [routerLink]="['/truyen-tranh', item.mangaId]">{{item.title}}</a></h2>
            
            @if(history()){
              <p class="lib-chap-info"><app-icon name="play" style="width:11px;height:11px;"/> {{item.chapterTitle}}</p>
              <small class="muted">{{ago(item.readAt!)}} trước</small>
              <div class="lib-actions">
                <a class="primary btn-read-continue" [routerLink]="['/chuong', item.chapterId]">
                  Đọc tiếp <app-icon name="arrowRight" style="width:12px;height:12px;"/>
                </a>
                <button class="text-button btn-remove-item" (click)="remove(item.mangaId)">Xóa</button>
              </div>
            }@else{
              <div class="lib-actions">
                <a class="primary btn-read-continue" [routerLink]="['/truyen-tranh', item.mangaId]">
                  Xem truyện <app-icon name="chevron"/>
                </a>
              </div>
            }
          </div>
        </article>
      }
    </div>

    @if(!items().length){
      <div class="empty-state glass-panel">
        <app-icon name="book"/>
        <h2>{{history() ? 'Bạn chưa đọc bộ truyện nào' : 'Bạn chưa theo dõi bộ truyện nào'}}</h2>
        <p>Hàng ngàn bộ truyện hấp dẫn đang chờ đón bạn khám phá.</p>
        <a class="primary" routerLink="/">Khám phá truyện hay</a>
      </div>
    }
  }
</div>`
})
export class Library {
  store = inject(Store);
  router = inject(Router);
  route = inject(ActivatedRoute);

  history = signal(false);
  ago = ago;

  constructor() {
    this.route.url.subscribe(() => this.history.set(this.router.url.includes('lich-su')));
    if (this.store.user()) {
      void this.store.refreshLibrary().catch(e => this.store.notify(message(e)));
    }
  }

  items() {
    return this.history() ? this.store.history() : this.store.follows();
  }

  async remove(id: string) {
    try {
      await this.store.removeHistory(id);
    } catch (e) {
      this.store.notify(message(e));
    }
  }
}

@Component({
  selector: 'app-not-found',
  imports: [RouterLink, Icon],
  template: `
<div class="empty-state glass-panel">
  <app-icon name="book"/>
  <h1>Không tìm thấy trang</h1>
  <p>Đường dẫn này không tồn tại hoặc đã được chuyển sang địa chỉ mới.</p>
  <a class="primary" routerLink="/">Về trang chủ</a>
</div>`
})
export class NotFound {}
