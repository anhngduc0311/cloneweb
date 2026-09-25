import { Routes } from '@angular/router';

export const routes: Routes = [
  {path:'',loadComponent:()=>import('./home').then(m=>m.Home),title:'AkaTruyen · Truyện tranh online'},
  {path:'tim-truyen-nang-cao',loadComponent:()=>import('./search').then(m=>m.Search),title:'Tìm truyện · AkaTruyen'},
  {path:'truyen-tranh/:id',loadComponent:()=>import('./detail').then(m=>m.Detail),title:'Chi tiết truyện · AkaTruyen'},
  {path:'chuong/:id',loadComponent:()=>import('./reader').then(m=>m.Reader),title:'Đọc truyện · AkaTruyen'},
  {path:'theo-doi',loadComponent:()=>import('./account').then(m=>m.Library),title:'Theo dõi · AkaTruyen'},
  {path:'lich-su',loadComponent:()=>import('./account').then(m=>m.Library),title:'Lịch sử · AkaTruyen'},
  {path:'dang-nhap',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng nhập · AkaTruyen'},
  {path:'dang-ky',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng ký · AkaTruyen'},
  {path:'nettrom',redirectTo:'',pathMatch:'full'},
  {path:'nettrom/tim-truyen-nang-cao',redirectTo:'tim-truyen-nang-cao'},
  {path:'nettrom/truyen-tranh/:id',redirectTo:'truyen-tranh/:id'},
  {path:'nettrom/chuong/:id',redirectTo:'chuong/:id'},
  {path:'nettrom/theo-doi',redirectTo:'theo-doi'},
  {path:'nettrom/lich-su',redirectTo:'lich-su'},
  {path:'**',loadComponent:()=>import('./account').then(m=>m.NotFound),title:'Không tìm thấy trang'}
];
