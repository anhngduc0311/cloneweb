import { Routes } from '@angular/router';

export const routes: Routes = [
 {path:'',redirectTo:'nettrom',pathMatch:'full'},
 {path:'nettrom',loadComponent:()=>import('./home').then(m=>m.Home),title:'TruyenDex · Truyện tranh online'},
 {path:'nettrom/tim-truyen-nang-cao',loadComponent:()=>import('./search').then(m=>m.Search),title:'Tìm truyện · TruyenDex'},
 {path:'nettrom/truyen-tranh/:id',loadComponent:()=>import('./detail').then(m=>m.Detail),title:'Chi tiết truyện · TruyenDex'},
 {path:'nettrom/chuong/:id',loadComponent:()=>import('./reader').then(m=>m.Reader),title:'Đọc truyện · TruyenDex'},
 {path:'nettrom/theo-doi',loadComponent:()=>import('./account').then(m=>m.Library),title:'Theo dõi · TruyenDex'},
 {path:'nettrom/lich-su',loadComponent:()=>import('./account').then(m=>m.Library),title:'Lịch sử · TruyenDex'},
 {path:'dang-nhap',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng nhập · TruyenDex'},
 {path:'dang-ky',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng ký · TruyenDex'},
 {path:'**',loadComponent:()=>import('./account').then(m=>m.NotFound),title:'Không tìm thấy trang'}
];
