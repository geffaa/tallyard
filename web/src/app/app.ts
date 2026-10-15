import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ToastsComponent } from './core/toast';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastsComponent],
  template: `<router-outlet /><app-toasts />`,
})
export class App {}
