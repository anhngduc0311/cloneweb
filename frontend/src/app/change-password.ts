import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Store, message } from './core';
import { Icon } from './ui';

@Component({
  selector: 'app-change-password',
  imports: [FormsModule, RouterLink, Icon],
  template: `
<div class="auth-wrap">
  <section class="auth-panel glass-panel" aria-labelledby="password-title">
    <app-icon class="password-icon" name="lock"/>
    <h1 id="password-title">Đổi mật khẩu</h1>
    @if(store.user()?.canChangePassword === false){
      <p class="muted">Tài khoản này đăng nhập bằng Google. Hãy đổi mật khẩu trong tài khoản Google của bạn.</p>
      <a class="primary" href="https://myaccount.google.com/security" target="_blank" rel="noopener noreferrer">Mở tài khoản Google <app-icon name="arrowRight"/></a>
    }@else{
      <p class="muted">Mật khẩu mới cần có từ 10–128 ký tự. Các phiên đăng nhập khác sẽ hết hiệu lực sau khi đổi.</p>
      <form class="auth-form" #form="ngForm" (ngSubmit)="submit()">
        <fieldset [disabled]="busy()">
          <label for="current-password"><span>Mật khẩu hiện tại</span></label>
          <input id="current-password" type="password" name="currentPassword" [(ngModel)]="currentPassword" autocomplete="current-password" required maxlength="128">
          <label for="new-password"><span>Mật khẩu mới</span></label>
          <input id="new-password" type="password" name="newPassword" [(ngModel)]="newPassword" autocomplete="new-password" required minlength="10" maxlength="128" aria-describedby="password-title">
          <label for="confirm-password"><span>Xác nhận mật khẩu mới</span></label>
          <input id="confirm-password" type="password" name="confirmPassword" [(ngModel)]="confirmPassword" autocomplete="new-password" required minlength="10" maxlength="128">
        </fieldset>
        @if(error()) { <p class="error" role="alert">{{error()}}</p> }
        @if(success()) { <p class="password-success" role="status"><app-icon name="check"/> Đổi mật khẩu thành công.</p> }
        <button class="primary btn-auth-submit" type="submit" [disabled]="busy() || form.invalid">{{busy() ? 'Đang lưu…' : 'Lưu mật khẩu mới'}} <app-icon name="arrowRight"/></button>
      </form>
    }
    <a routerLink="/" class="text-button">Về trang chủ</a>
  </section>
</div>`,
  styles: [`
    .password-icon {width:32px;height:32px;color:var(--accent);margin-bottom:16px}
    fieldset {border:0;padding:0;margin:0;min-width:0;display:grid;gap:8px}
    fieldset label:not(:first-child) {margin-top:8px}
    input {width:100%;box-sizing:border-box}
    .password-success {display:flex;align-items:center;gap:8px;color:var(--accent);margin:0}
  `]
})
export class ChangePassword {
  store = inject(Store);
  busy = signal(false);
  error = signal('');
  success = signal(false);
  currentPassword = '';
  newPassword = '';
  confirmPassword = '';

  async submit() {
    if (this.busy()) return;
    this.error.set('');
    this.success.set(false);
    if (!this.currentPassword || this.currentPassword.length > 128) {
      this.error.set('Vui lòng nhập mật khẩu hiện tại.');
      return;
    }
    if (this.newPassword.length < 10 || this.newPassword.length > 128) {
      this.error.set('Mật khẩu mới phải có từ 10–128 ký tự.');
      return;
    }
    if (this.newPassword !== this.confirmPassword) {
      this.error.set('Mật khẩu xác nhận chưa trùng khớp.');
      return;
    }
    if (this.currentPassword === this.newPassword) {
      this.error.set('Mật khẩu mới phải khác mật khẩu hiện tại.');
      return;
    }
    this.busy.set(true);
    try {
      await this.store.changePassword(this.currentPassword, this.newPassword, this.confirmPassword);
      this.currentPassword = this.newPassword = this.confirmPassword = '';
      this.success.set(true);
    } catch (e) {
      this.error.set(message(e));
    } finally {
      this.busy.set(false);
    }
  }
}
