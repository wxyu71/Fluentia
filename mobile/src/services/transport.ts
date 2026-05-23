export const TRANSPORT_READY_STATE = {
  CONNECTING: 0,
  OPEN: 1,
  CLOSING: 2,
  CLOSED: 3,
} as const;

export interface TransportMessageEvent {
  data: string;
}

export interface TransportConnection {
  readonly readyState: number;
  onopen: (() => void) | null;
  onmessage: ((event: TransportMessageEvent) => void) | null;
  onclose: (() => void) | null;
  onerror: (() => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}