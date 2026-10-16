import { effect, inject, Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { Auth } from './auth';

export interface BalanceChanged {
  productId: string;
  warehouseId: string;
  onHand: number;
  reserved: number;
}

/**
 * Live stock: one SignalR connection while signed in. The server sends each committed change
 * to everyone in the tenant; screens patch the rows they show and briefly highlight them.
 */
@Injectable({ providedIn: 'root' })
export class StockLive {
  readonly changes = new Subject<BalanceChanged[]>();
  readonly connected = signal(false);
  private hub: HubConnection | null = null;

  constructor() {
    const auth = inject(Auth);
    effect(() => (auth.me() ? this.start() : this.stop()));
  }

  private async start() {
    if (this.hub) return;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/stock')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('balances', (list: BalanceChanged[]) => this.changes.next(list));
    this.hub.onreconnecting(() => this.connected.set(false));
    this.hub.onreconnected(() => this.connected.set(true));
    this.hub.onclose(() => this.connected.set(false));
    try {
      await this.hub.start();
      this.connected.set(this.hub.state === HubConnectionState.Connected);
    } catch {
      this.connected.set(false); // the screens still work; they just don't update by themselves
    }
  }

  private async stop() {
    const hub = this.hub;
    this.hub = null;
    this.connected.set(false);
    await hub?.stop();
  }
}
