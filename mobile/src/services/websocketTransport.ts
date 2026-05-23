import type { TransportConnection, TransportMessageEvent } from './transport';

class BrowserWebSocketTransport implements TransportConnection {
  private readonly socket: WebSocket;

  onopen: (() => void) | null = null;
  onmessage: ((event: TransportMessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(url: string) {
    this.socket = new WebSocket(url);
    this.socket.onopen = () => this.onopen?.();
    this.socket.onmessage = (event) => this.onmessage?.({ data: typeof event.data === 'string' ? event.data : '' });
    this.socket.onclose = () => this.onclose?.();
    this.socket.onerror = () => this.onerror?.();
  }

  get readyState(): number {
    return this.socket.readyState;
  }

  send(data: string): void {
    this.socket.send(data);
  }

  close(code?: number, reason?: string): void {
    this.socket.close(code, reason);
  }
}

export function createWebSocketTransport(url: string): TransportConnection {
  return new BrowserWebSocketTransport(url);
}