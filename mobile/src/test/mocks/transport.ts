/**
 * Reusable mock transport for testing useWebSocket and related hooks.
 * Implements the TransportConnection interface.
 */

import type { TransportConnection, TransportMessageEvent } from '../../services/transport';
import { TRANSPORT_READY_STATE } from '../../services/transport';

export class MockTransport implements TransportConnection {
  readyState: (typeof TRANSPORT_READY_STATE)[keyof typeof TRANSPORT_READY_STATE] = TRANSPORT_READY_STATE.OPEN;
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((event: TransportMessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;

  private closeCode?: number;
  private closeReason?: string;

  constructor(autoOpen = true) {
    if (autoOpen) {
      this.readyState = TRANSPORT_READY_STATE.OPEN;
    } else {
      this.readyState = TRANSPORT_READY_STATE.CONNECTING;
    }
  }

  send(data: string): void {
    if (this.readyState !== TRANSPORT_READY_STATE.OPEN) {
      throw new Error('MockTransport: cannot send, not open');
    }
    this.sent.push(data);
  }

  close(code?: number, reason?: string): void {
    this.closeCode = code;
    this.closeReason = reason;
    this.readyState = TRANSPORT_READY_STATE.CLOSED;
    this.onclose?.();
  }

  /** Simulate receiving a message from the server */
  simulateMessage(data: string): void {
    this.onmessage?.({ data });
  }

  /** Simulate the connection opening */
  simulateOpen(): void {
    this.readyState = TRANSPORT_READY_STATE.OPEN;
    this.onopen?.();
  }

  /** Simulate a connection close */
  simulateClose(code = 1000, reason = 'normal'): void {
    this.readyState = TRANSPORT_READY_STATE.CLOSED;
    this.onclose?.();
  }

  /** Simulate a connection error */
  simulateError(): void {
    this.onerror?.();
  }

  /** Get the last sent message parsed as JSON */
  getLastSentMessage<T = Record<string, unknown>>(): T | null {
    if (this.sent.length === 0) return null;
    return JSON.parse(this.sent[this.sent.length - 1]) as T;
  }

  /** Get all sent messages parsed as JSON */
  getSentMessages<T = Record<string, unknown>>(): T[] {
    return this.sent.map((s) => JSON.parse(s) as T);
  }

  /** Clear sent messages history */
  clearSent(): void {
    this.sent = [];
  }
}
