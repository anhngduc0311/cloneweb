import { Component, signal, inject, HostListener } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router, NavigationEnd } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Icon } from './ui';
import { Api, Store, Settings } from './core';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet,RouterLink,RouterLinkActive,FormsModule,Icon],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
 store=inject(Store);api=inject(Api);router=inject(Router);query='';menu=signal(false);account=signal(false);settingsOpen=signal(false);genresOpen=signal(false);tags=signal<{id:string;name:string}[]>([]);draft:Settings={...this.store.settings()};reader=signal(false);
 constructor(){this.router.events.subscribe(e=>{if(e instanceof NavigationEnd){this.menu.set(false);this.account.set(false);this.genresOpen.set(false);this.reader.set(e.urlAfterRedirects.includes('/chuong/'));window.scrollTo(0,0);}});}
 search(){void this.router.navigate(['/nettrom/tim-truyen-nang-cao'],{queryParams:{q:this.query}});}
 openSettings(){this.draft={...this.store.settings()};this.settingsOpen.set(true);}
 saveSettings(){this.store.saveSettings(this.draft);this.settingsOpen.set(false);}
 async genres(){this.genresOpen.update(v=>!v);if(!this.tags().length){try{this.tags.set(await this.api.request('/catalog/tags'));}catch{this.store.notify('Không tải được thể loại. Vui lòng thử lại.');}}}
 @HostListener('window:keydown.escape') escape(){this.settingsOpen.set(false);this.menu.set(false);this.account.set(false);this.genresOpen.set(false);}
}
