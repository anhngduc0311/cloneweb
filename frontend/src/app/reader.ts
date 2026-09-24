import { Component, inject, signal, HostListener } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Store, Reader as ReaderData, message } from './core';
import { Icon } from './ui';
@Component({selector:'app-reader',imports:[FormsModule,RouterLink,Icon],template:`
@if(loading()){<div class="loading-panel">Đang tải chương truyện và máy chủ ảnh…</div>}
@if(error()){<div class="error-state"><app-icon name="info"/><h2>Chưa thể mở chương</h2><p>{{error()}}</p><button class="primary" (click)="load()">Thử lại</button><a routerLink="/nettrom">Về trang chủ</a></div>}
@if(data();as r){<div class="reader-heading"><div class="breadcrumb"><a routerLink="/nettrom">Trang chủ</a><span>›</span><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]">{{r.manga.title}}</a></div><h1>{{r.manga.title}} <span>— {{r.chapter.title}}</span></h1><p>{{r.chapter.group}} · {{pages().length}} trang · {{r.chapter.language==='vi'?'Tiếng Việt':'Tiếng Anh'}}</p></div>
<div class="reader-toolbar"><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]" class="icon-button" aria-label="Danh sách chương"><app-icon name="menu"/></a><button class="secondary" [disabled]="!previous()" (click)="move(-1)">‹ <span>Chương trước</span></button><select aria-label="Chọn chương" [ngModel]="r.chapter.id" (ngModelChange)="go($event)">@for(c of r.navigation;track c.id){<option [value]="c.id">{{c.title}}</option>}</select><button class="primary" [disabled]="!next()" (click)="move(1)"><span>Chương sau</span> ›</button><button class="icon-button" aria-label="Cài đặt trình đọc" (click)="showSettings=!showSettings"><app-icon name="settings"/></button></div>
@if(showSettings){<div class="reader-settings"><label><input type="checkbox" [ngModel]="store.settings().dataSaver" (ngModelChange)="quality($event)"> Tiết kiệm dung lượng ảnh</label><label>Chiều rộng <select [ngModel]="store.settings().width" (ngModelChange)="width($event)"><option [ngValue]="700">700px</option><option [ngValue]="900">900px</option><option [ngValue]="1200">1200px</option></select></label></div>}
<p class="reader-notice"><app-icon name="info"/> Dùng phím ← → để chuyển chương. Ảnh được tải trực tiếp từ nguồn MangaDex.</p>
@if(r.externalUrl){<div class="empty-state"><p>Chương này được phát hành trên trang của nhóm dịch.</p><a class="primary" [href]="r.externalUrl" target="_blank" rel="noopener noreferrer">Đọc tại nguồn ↗</a></div>}
<div class="reader-pages" [style.max-width.px]="store.settings().width">@for(url of pages();track url;let i=$index){<div class="reader-page">@if(failed().has(i)){<div class="image-error"><p>Không tải được trang {{i+1}} từ máy chủ ảnh.</p><button class="secondary" (click)="retry(i)">Tải lại trang {{i+1}}</button></div>}@else{<img [src]="url" [alt]="'Trang '+(i+1)+' — '+r.chapter.title" [loading]="i<2?'eager':'lazy'" referrerpolicy="no-referrer" (error)="imageError(i)">}<span class="page-number">{{i+1}} / {{pages().length}}</span></div>}</div>
<div class="reader-end"><h2>Bạn đã đọc hết {{r.chapter.title}}</h2><div><button class="secondary" [disabled]="!previous()" (click)="move(-1)">‹ Chương trước</button><button class="primary" [disabled]="!next()" (click)="move(1)">Chương sau ›</button></div><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]">Về trang truyện và bình luận</a></div>}`})
export class Reader {
 api=inject(Api);store=inject(Store);route=inject(ActivatedRoute);router=inject(Router);data=signal<ReaderData|null>(null);loading=signal(true);error=signal('');failed=signal(new Set<number>());showSettings=false;id='';epoch=0;
 constructor(){this.route.paramMap.subscribe(p=>{this.id=p.get('id')!;void this.load();});}
 async load(){const n=++this.epoch;this.data.set(null);this.loading.set(true);this.error.set('');this.failed.set(new Set());try{const r=await this.api.request<ReaderData>('/chapters/'+this.id);if(n!==this.epoch)return;this.data.set(r);void this.store.record(r).catch(e=>this.store.notify(message(e)));window.scrollTo(0,0);}catch(e){if(n===this.epoch)this.error.set(message(e));}finally{if(n===this.epoch)this.loading.set(false);}}
 pages(){const r=this.data();return r?(this.store.settings().dataSaver&&r.dataSaverPages.length?r.dataSaverPages:r.pages):[];}
 position(){return this.data()?.navigation.findIndex(c=>c.id===this.id)??-1;}
 previous(){return this.data()?.navigation[this.position()-1];}next(){return this.data()?.navigation[this.position()+1];}
 go(id:string){void this.router.navigate(['/nettrom/chuong',id]);}
 move(direction:number){const c=direction<0?this.previous():this.next();if(c)this.go(c.id);}
 imageError(i:number){this.failed.update(s=>new Set(s).add(i));}
 retry(i:number){this.failed.update(s=>{const n=new Set(s);n.delete(i);return n;});}
 quality(dataSaver:boolean){this.failed.set(new Set());this.store.saveSettings({...this.store.settings(),dataSaver});}
 width(width:number){this.store.saveSettings({...this.store.settings(),width});}
 @HostListener('window:keydown',['$event']) key(e:KeyboardEvent){if(['INPUT','SELECT','TEXTAREA'].includes((e.target as HTMLElement).tagName))return;if(e.key==='ArrowLeft')this.move(-1);if(e.key==='ArrowRight')this.move(1);}
}
