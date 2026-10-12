import { Routes } from '@angular/router';
import { adminGuard } from './admin.guard';
import { authGuard } from './auth.guard';

export const routes: Routes = [
  {path:'',loadComponent:()=>import('./home').then(m=>m.Home),title:'DokiTruyen - Đọc Truyện Tranh Online Miễn Phí Mới Nhất | dokitruyen.com'},
  {path:'tim-truyen-nang-cao',loadComponent:()=>import('./search').then(m=>m.Search),title:'Tìm truyện nâng cao · DokiTruyen'},
  {path:'tac-gia/:name',loadComponent:()=>import('./author').then(m=>m.AuthorComponent),title:'Tác giả · DokiTruyen'},
  {path:'tac-gia',loadComponent:()=>import('./author').then(m=>m.AuthorComponent),title:'Tác giả · DokiTruyen'},
  {path:'truyen-tranh/:id',loadComponent:()=>import('./detail').then(m=>m.Detail),title:'Chi tiết truyện · DokiTruyen'},
  {path:'chuong/:id',loadComponent:()=>import('./reader').then(m=>m.Reader),title:'Đọc truyện online · DokiTruyen'},
  {path:'theo-doi',loadComponent:()=>import('./account').then(m=>m.Library),title:'Truyện theo dõi · DokiTruyen'},
  {path:'lich-su',loadComponent:()=>import('./account').then(m=>m.Library),title:'Lịch sử đọc · DokiTruyen'},
  {path:'dang-nhap',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng nhập · DokiTruyen'},
  {path:'dang-ky',loadComponent:()=>import('./account').then(m=>m.Auth),title:'Đăng ký tài khoản · DokiTruyen'},
  {path:'doi-mat-khau',loadComponent:()=>import('./change-password').then(m=>m.ChangePassword),canActivate:[authGuard],title:'Đổi mật khẩu · DokiTruyen'},
  {path:'admin',loadComponent:()=>import('./admin').then(m=>m.AdminComponent),canActivate:[adminGuard],title:'Quản trị hệ thống · DokiTruyen Admin Studio'},
  {path:'nettrom',redirectTo:'',pathMatch:'full'},
  {path:'nettrom/tim-truyen-nang-cao',redirectTo:'tim-truyen-nang-cao'},
  {path:'nettrom/truyen-tranh/:id',redirectTo:'truyen-tranh/:id'},
  {path:'nettrom/chuong/:id',redirectTo:'chuong/:id'},
  {path:'nettrom/theo-doi',redirectTo:'theo-doi'},
  {path:'nettrom/lich-su',redirectTo:'lich-su'},
  {path:'**',loadComponent:()=>import('./account').then(m=>m.NotFound),title:'Không tìm thấy trang'}
];
