import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Store } from './core';

export const adminGuard: CanActivateFn = () => {
  const store = inject(Store);
  const router = inject(Router);
  const u = store.user();
  if (u && ['admin', 'superadmin', 'editor', 'translator'].includes(u.role)) {
    return true;
  }
  if (typeof window !== 'undefined' && sessionStorage.getItem('td-token')) {
    return true;
  }
  void router.navigate(['/dang-nhap'], { queryParams: { returnUrl: '/admin' } });
  return false;
};
