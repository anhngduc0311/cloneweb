import { Component, inject, signal, HostListener } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Store, Reader as ReaderData, message, proxyImage } from './core';
import { Icon } from './ui';

@Component({selector:'app-reader',imports:[FormsModule,RouterLink,Icon],template:`
@if(loading()){
  <div class="reader-loading-wrap">
    <div class="reader-loading-card">
      <div class="reader-spinner"></div>
      <h3>Đang tải chương truyện…</h3>
      <p>Đang chuẩn bị hình ảnh chất lượng cao và kết nối máy chủ</p>
    </div>
    <div class="reader-skeleton-pages" [style.max-width.px]="store.settings().width">
      @for(s of [1,2,3];track s){
        <div class="reader-page-skeleton shimmer">
          <div class="skeleton-content">
            <app-icon name="book"/>
            <span>Đang chuẩn bị trang {{s}}…</span>
          </div>
        </div>
      }
    </div>
  </div>
}
@if(error()){<div class="error-state"><app-icon name="info"/><h2>Chưa thể mở chương</h2><p>{{error()}}</p><button class="primary" (click)="load()">Thử lại</button><a routerLink="/nettrom">Về trang chủ</a></div>}
@if(data();as r){
<div class="reader-heading"><div class="breadcrumb"><a routerLink="/nettrom">Trang chủ</a><span>›</span><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]">{{r.manga.title}}</a></div><h1>{{r.manga.title}} <span>— {{r.chapter.title}}</span></h1><p>{{r.chapter.group}} · {{pages().length}} trang · {{r.chapter.language==='vi'?'Tiếng Việt':'Tiếng Anh'}}</p></div>
<div class="reader-toolbar" [class.is-busy]="loading()"><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]" class="icon-button" aria-label="Danh sách chương"><app-icon name="menu"/></a><button class="secondary" [disabled]="!previous()" (click)="move(-1)">‹ <span>Chương trước</span></button><select aria-label="Chọn chương" [ngModel]="r.chapter.id" (ngModelChange)="go($event)">@for(c of r.navigation;track c.id){<option [value]="c.id">{{c.title}}</option>}</select><button class="primary" [disabled]="!next()" (click)="move(1)"><span>Chương sau</span> ›</button><button class="icon-button" aria-label="Cài đặt trình đọc" (click)="showSettings=!showSettings"><app-icon name="settings"/></button></div>
@if(showSettings){<div class="reader-settings"><label><input type="checkbox" [ngModel]="store.settings().dataSaver" (ngModelChange)="quality($event)"> Tiết kiệm dung lượng ảnh</label><label>Chiều rộng <select [ngModel]="store.settings().width" (ngModelChange)="width($event)"><option [ngValue]="700">700px</option><option [ngValue]="900">900px</option><option [ngValue]="1200">1200px</option></select></label></div>}
<p class="reader-notice"><app-icon name="info"/> Dùng phím ← → để chuyển chương. Ảnh được tối ưu qua máy chủ proxy.</p>
@if(r.externalUrl){<div class="empty-state"><p>Chương này được phát hành trên trang của nhóm dịch.</p><a class="primary" [href]="r.externalUrl" target="_blank" rel="noopener noreferrer">Đọc tại nguồn ↗</a></div>}
<div class="reader-pages" [style.max-width.px]="store.settings().width">
@for(url of pages();track url;let i=$index){
  <div class="reader-page" [class.is-loading]="!isLoaded(i) && !failed().has(i)">
    @if(!isLoaded(i) && !failed().has(i)){
      <div class="reader-page-skeleton shimmer">
        <div class="skeleton-content">
          <div class="mini-spin"></div>
          <span>Đang tải trang {{i+1}} / {{pages().length}}…</span>
        </div>
      </div>
    }
    @if(failed().has(i)){
      <div class="image-error">
        <p>Không tải được trang {{i+1}} từ máy chủ ảnh.</p>
        <button class="secondary" (click)="retry(i)">Tải lại trang {{i+1}}</button>
      </div>
    }@else{
      <img [src]="pageUrl(url, i)" 
           [alt]="'Trang '+(i+1)+' — '+r.chapter.title" 
           [loading]="i<2?'eager':'lazy'" 
           decoding="async"
           [attr.fetchpriority]="i===0?'high':'auto'"
           referrerpolicy="no-referrer" 
           (load)="onLoaded(i)"
           (error)="imageError(i)"
           [class.loaded]="isLoaded(i)">
    }
  </div>
}
</div>
<div class="reader-end"><h2>Bạn đã đọc hết {{r.chapter.title}}</h2><div><button class="secondary" [disabled]="!previous()" (click)="move(-1)">‹ Chương trước</button><button class="primary" [disabled]="!next()" (click)="move(1)">Chương sau ›</button></div><a [routerLink]="['/nettrom/truyen-tranh',r.manga.id]">Về trang truyện và bình luận</a></div>
}`})
export class Reader {
 api=inject(Api);store=inject(Store);route=inject(ActivatedRoute);router=inject(Router);data=signal<ReaderData|null>(null);loading=signal(true);error=signal('');failed=signal(new Set<number>());loaded=signal(new Set<number>());fallbackUrls=signal<Map<number,string>>(new Map());showSettings=false;id='';epoch=0;
 private preloadedUrls=new Set<string>();
 private preloadedNextId='';
 constructor(){this.route.paramMap.subscribe(p=>{this.id=p.get('id')!;void this.load();});}
 async load(){const n=++this.epoch;this.data.set(null);this.loading.set(true);this.error.set('');this.failed.set(new Set());this.loaded.set(new Set());this.fallbackUrls.set(new Map());this.preloadedUrls.clear();this.preloadedNextId='';try{const r=await this.api.request<ReaderData>('/chapters/'+this.id,'GET',undefined,true);if(n!==this.epoch)return;this.data.set(r);void this.store.record(r).catch(e=>this.store.notify(message(e)));window.scrollTo(0,0);this.preloadUpcoming(0,4);}catch(e){if(n===this.epoch)this.error.set(message(e));}finally{if(n===this.epoch)this.loading.set(false);}}
 pages(){const r=this.data();const list=r?(this.store.settings().dataSaver&&r.dataSaverPages.length?r.dataSaverPages:r.pages):[];return list.map(proxyImage);}
 pageUrl(url:string,i:number):string{return this.fallbackUrls().get(i)??url;}
 onLoaded(i:number){
   this.loaded.update(s=>new Set(s).add(i));
   this.preloadUpcoming(i+1,3);
   const total=this.pages().length;
   if(total>0&&i>=total-4){void this.preloadNextChapter();}
 }
 isLoaded(i:number):boolean{return this.loaded().has(i);}
 preloadUpcoming(startIndex:number,count:number){
   const list=this.pages();
   for(let i=startIndex;i<Math.min(list.length,startIndex+count);i++){
     const url=this.pageUrl(list[i],i);
     if(url&&!this.preloadedUrls.has(url)){
       this.preloadedUrls.add(url);
       const img=new Image();
       img.src=url;
     }
   }
 }
 async preloadNextChapter(){
   const next=this.next();
   if(!next||this.preloadedNextId===next.id)return;
   this.preloadedNextId=next.id;
   try{
     const r=await this.api.request<ReaderData>('/chapters/'+next.id,'GET',undefined,true);
     if(!r)return;
     const list=(this.store.settings().dataSaver&&r.dataSaverPages?.length?r.dataSaverPages:r.pages)||[];
     list.slice(0,3).forEach(url=>{
       const p=proxyImage(url);
       if(!this.preloadedUrls.has(p)){
         this.preloadedUrls.add(p);
         const img=new Image();
         img.src=p;
       }
     });
   }catch{}
 }
 position(){return this.data()?.navigation.findIndex(c=>c.id===this.id)??-1;}
 previous(){return this.data()?.navigation[this.position()-1];}next(){return this.data()?.navigation[this.position()+1];}
 go(id:string){void this.router.navigate(['/nettrom/chuong',id]);}
 move(direction:number){const c=direction<0?this.previous():this.next();if(c)this.go(c.id);}
 imageError(i:number){const list=this.pages();const current=this.pageUrl(list[i],i);if(!current.startsWith('/api/catalog/image-proxy')){const fallback='/api/catalog/image-proxy?url='+encodeURIComponent(current);this.fallbackUrls.update(m=>new Map(m).set(i,fallback));return;}this.failed.update(s=>new Set(s).add(i));}
 retry(i:number){this.fallbackUrls.update(m=>{const n=new Map(m);n.delete(i);return n;});this.failed.update(s=>{const n=new Set(s);n.delete(i);return n;});this.loaded.update(s=>{const n=new Set(s);n.delete(i);return n;});}
 quality(dataSaver:boolean){this.failed.set(new Set());this.loaded.set(new Set());this.fallbackUrls.set(new Map());this.preloadedUrls.clear();this.store.saveSettings({...this.store.settings(),dataSaver});this.preloadUpcoming(0,4);}
 width(width:number){this.store.saveSettings({...this.store.settings(),width});}
 @HostListener('window:keydown',['$event']) key(e:KeyboardEvent){if(['INPUT','SELECT','TEXTAREA'].includes((e.target as HTMLElement).tagName))return;if(e.key==='ArrowLeft')this.move(-1);if(e.key==='ArrowRight')this.move(1);}
}
