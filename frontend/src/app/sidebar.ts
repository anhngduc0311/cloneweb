import { Component, signal, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, Store, Manga, Page, Comment, ago, compact, message } from './core';
import { Icon } from './ui';
@Component({selector:'app-sidebar',imports:[RouterLink,Icon],template:`
<aside class="sidebar">
 <section><div class="section-title"><h2><app-icon name="clock"/> Lịch sử đọc truyện</h2><a routerLink="/nettrom/lich-su">Xem tất cả</a></div>
 @if(!store.history().length){<div class="empty-history"><app-icon name="book"/><p>Chưa có lịch sử đọc truyện</p><small>Những câu chuyện đang chờ bạn khám phá</small></div>}
 @for(h of store.history().slice(0,3);track h.mangaId){<a class="history-row" [routerLink]="['/nettrom/chuong',h.chapterId]"><img [src]="h.cover" alt="" referrerpolicy="no-referrer"><div><strong>{{h.title}}</strong><small>{{h.chapterTitle}}</small></div><app-icon name="chevron"/></a>}
 </section>
 <section><div class="section-title"><h2><app-icon name="trophy"/> Bảng xếp hạng</h2></div><div class="rank-tabs" role="tablist">@for(t of tabs;track t.key){<button role="tab" [attr.aria-selected]="sort()===t.key" [class.active]="sort()===t.key" (click)="load(t.key)">{{t.label}}</button>}</div>
 @if(error()){<p class="error">{{error()}} <button (click)="load(sort())">Thử lại</button></p>}
 @if(loading()){<div class="rank-loading">Đang tải bảng xếp hạng…</div>}
 @for(m of ranking();track m.id;let i=$index){<a class="rank-row" [routerLink]="['/nettrom/truyen-tranh',m.id]"><span class="rank-number" [class.top]="i<3">{{i+1}}</span><img [src]="m.cover" [alt]="m.title" loading="lazy" referrerpolicy="no-referrer"><div><h3>{{m.title}}</h3><span><app-icon [name]="sort()==='rating'?'star':'heart'"/> {{sort()==='rating'?m.rating.toFixed(1):compact(m.follows)}}</span></div></a>}
 </section>
 <section><div class="section-title"><h2><app-icon name="comment"/> Bình luận gần đây</h2></div>@for(c of comments();track c.id){<div class="recent-comment"><div><span class="avatar">{{c.name.slice(0,1)}}</span><strong>{{c.name}}</strong><small>{{ago(c.createdAt)}}</small></div><p>{{c.body}}</p><a [routerLink]="['/nettrom/truyen-tranh',c.mangaId]">{{c.mangaTitle}}</a></div>}@if(!comments().length){<p class="muted empty-small">Chưa có bình luận. Hãy bắt đầu cuộc trò chuyện!</p>}</section>
</aside>`})
export class Sidebar {
 api=inject(Api);store=inject(Store);ranking=signal<Manga[]>([]);comments=signal<Comment[]>([]);sort=signal('hot');loading=signal(false);error=signal('');compact=compact;ago=ago;private epoch=0;
 tabs=[{key:'hot',label:'★ Top'},{key:'rating',label:'♥ Yêu thích'},{key:'new',label:'◷ Mới'}];
 constructor(){void this.load('hot');void this.api.request<Page<Comment>>('/comments').then(r=>this.comments.set(r.items.slice(0,5))).catch(()=>{});}
 async load(sort:string){const n=++this.epoch;this.sort.set(sort);this.loading.set(true);this.error.set('');try{const r=await this.api.request<Page<Manga>>('/catalog/search?'+this.api.query({sort,pageSize:7,language:this.store.settings().language}));if(n===this.epoch)this.ranking.set(r.items);}catch(e){if(n===this.epoch)this.error.set(message(e));}finally{if(n===this.epoch)this.loading.set(false);}}
}
