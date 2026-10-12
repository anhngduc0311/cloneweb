import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Store } from './core';

export const authGuard: CanActivateFn = async (_route, state) => {
  const router = inject(Router);
  const user = await inject(Store).ensureRestored();
  return user ? true : router.createUrlTree(['/dang-nhap'], {queryParams: {returnUrl: state.url}});
};
