import { Injectable, signal, computed } from '@angular/core';

export interface Chapter { id:string; mangaId:string; title:string; number:number; language:string; publishedAt:string; group:string; }
export interface Manga { id:string; title:string; alternativeTitle:string; author:string; cover:string; description:string; genres:string[]; status:string; country:string; demographic:string; contentRating:string; year:number|null; rating:number; follows:number; updatedAt:string; chapters:Chapter[]; }
export interface Page<T> { items:T[]; total:number; page:number; pageSize:number; }
export interface Reader { chapter:Chapter; manga:Manga; pages:string[]; dataSaverPages:string[]; externalUrl:string|null; navigation:Chapter[]; }
export interface User { id:string; name:string; email:string; role:string; }
export interface LibraryItem { mangaId:string; title:string; cover:string; chapterId?:string; chapterTitle?:string; readAt?:string; }
export interface Comment { id:string; mangaId:string; mangaTitle:string; userId:string; name:string; body:string; createdAt:string; }
export interface Settings { language:string; dataSaver:boolean; width:number; theme:string; }
export function readStored<T>(key:string, fallback:T):T { try { return JSON.parse(localStorage.getItem(key) || 'null') ?? fallback; } catch { return fallback; } }
export const message = (e:unknown) => e instanceof Error ? e.message : 'Không thể kết nối. Vui lòng thử lại.';
export function compact(n:number):string { return n>=1000000?(n/1000000).toFixed(1)+'m':n>=1000?(n/1000).toFixed(1)+'k':String(n); }
export function ago(date:string):string { const m=Math.max(0,Math.floor((Date.now()-new Date(date).getTime())/60000)); return m<1?'Vừa xong':m<60?`${m} phút`:m<1440?`${Math.floor(m/60)} giờ`:`${Math.floor(m/1440)} ngày`; }
export const statuses:Record<string,string>={ongoing:'Đang tiến hành',completed:'Đã hoàn thành',hiatus:'Tạm ngưng',cancelled:'Đã hủy'};

export function proxyImage(url: string): string {
  if (!url || url.startsWith('https://services.f-ck.me/') || url.startsWith('/api/')) return url;
  if (url.includes('.mangadex.network/') || url.includes('mangadex.org/')) {
    return 'https://services.f-ck.me/v1/image/' + btoa(url).replace(/\+/g, '-').replace(/\//g, '_');
  }
  if (url.includes('hinhtruyen.com') || url.includes('hinhhinh.com') || url.includes('truyenggvn.com') || url.includes('truyenvua') || url.includes('tintruyen') || url.includes('blogspot.com')) {
    return '/api/catalog/image-proxy?url=' + encodeURIComponent(url);
  }
  return url;
}

@Injectable({providedIn:'root'})
export class Api {
  private cache = new Map<string, { data: unknown; expiry: number }>();

  clearCache(prefix?: string) {
    if (!prefix) this.cache.clear();
    else { for (const k of this.cache.keys()) if (k.includes(prefix)) this.cache.delete(k); }
  }

  async request<T>(path:string, method='GET', body?:unknown, useCache = false):Promise<T> {
    const isGet = method === 'GET';
    const shouldCache = isGet && (useCache || path.startsWith('/chapters/') || path.startsWith('/catalog/'));
    if (shouldCache) {
      const hit = this.cache.get(path);
      if (hit && hit.expiry > Date.now()) {
        return hit.data as T;
      }
    }
    const token=sessionStorage.getItem('td-token');
    const response=await fetch('/api'+path,{method,headers:{...(body!==undefined?{'Content-Type':'application/json'}:{}),...(token?{Authorization:`Bearer ${token}`}:{})},body:body!==undefined?JSON.stringify(body):undefined});
    if(!response.ok){ let info; try{info=await response.json();}catch{} throw new Error(info?.message || (response.status===401?'Vui lòng đăng nhập để tiếp tục.':response.status===429?'Bạn thao tác quá nhanh. Vui lòng thử lại sau một phút.':'Không tải được dữ liệu. Vui lòng thử lại.')); }
    const result = response.status===204?undefined as T:await response.json();
    if (shouldCache) {
      this.cache.set(path, { data: result, expiry: Date.now() + 5 * 60 * 1000 });
    }
    return result;
  }
  query(params:Record<string,unknown>){const q=new URLSearchParams();Object.entries(params).forEach(([k,v])=>{if(v!==''&&v!==undefined&&v!==null)q.set(k,String(v));});return q.toString();}
}
@Injectable({providedIn:'root'})
export class Store {
  user=signal<User|null>(null);
  follows=signal<LibraryItem[]>([]);
  history=signal<LibraryItem[]>(readStored('td-history',[]));
  settings=signal<Settings>(readStored('td-settings',{language:'vi',dataSaver:true,width:900,theme:'dark'}));
  toast=signal('');
  private timer?:ReturnType<typeof setTimeout>;
  constructor(private api:Api){this.applyTheme();if(sessionStorage.getItem('td-token'))void this.restore();}
  notify(text:string){this.toast.set(text);clearTimeout(this.timer);this.timer=setTimeout(()=>this.toast.set(''),4500);}
  async restore(){try{this.user.set(await this.api.request<User>('/auth/me'));await this.refreshLibrary();}catch{sessionStorage.removeItem('td-token');this.user.set(null);}}
  async login(email:string,password:string,name?:string){const s=await this.api.request<{token:string;user:User}>('/auth/'+(name!==undefined?'register':'login'),'POST',{email,password,name});sessionStorage.setItem('td-token',s.token);this.user.set(s.user);await this.refreshLibrary();}
  logout(){sessionStorage.removeItem('td-token');this.user.set(null);this.follows.set([]);this.history.set(readStored('td-history',[]));this.notify('Đã đăng xuất.');}
  async refreshLibrary(){if(!this.user())return;const r=await this.api.request<{follows:LibraryItem[];history:LibraryItem[]}>('/library');this.follows.set(r.follows);this.history.set(r.history);}
  isFollowed(id:string){return this.follows().some(x=>x.mangaId===id);}
  async follow(m:Manga){if(!this.user())throw new Error('Vui lòng đăng nhập để theo dõi truyện.');const followed=!this.isFollowed(m.id);await this.api.request(`/library/follows/${m.id}`,'PUT',{followed});await this.refreshLibrary();this.notify(followed?'Đã thêm vào truyện theo dõi.':'Đã bỏ theo dõi truyện.');}
  async record(r:Reader){const item:LibraryItem={mangaId:r.manga.id,title:r.manga.title,cover:r.manga.cover,chapterId:r.chapter.id,chapterTitle:r.chapter.title,readAt:new Date().toISOString()};if(this.user()){await this.api.request(`/library/history/${r.chapter.id}`,'PUT',{});await this.refreshLibrary();}else{this.history.update(v=>[item,...v.filter(x=>x.mangaId!==item.mangaId)].slice(0,200));localStorage.setItem('td-history',JSON.stringify(this.history()));}}
  async removeHistory(id:string){if(this.user())await this.api.request('/library/history/'+id,'DELETE');this.history.update(v=>v.filter(x=>x.mangaId!==id));if(!this.user())localStorage.setItem('td-history',JSON.stringify(this.history()));}
  saveSettings(s:Settings){this.settings.set({...s});localStorage.setItem('td-settings',JSON.stringify(s));this.applyTheme();this.notify('Đã lưu tùy chỉnh.');}
  applyTheme(){document.documentElement.dataset['theme']=this.settings().theme;}
}
