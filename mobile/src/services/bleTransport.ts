/**
 * BLE Transport: implements TransportConnection over Web Bluetooth GATT.
 *
 * Architecture:
 * - Mobile writes to the write characteristic (mobile → PC)
 * - PC writes to the notify characteristic (PC → mobile), mobile receives via notifications
 * - For PC→mobile data, mobile sends periodic "poll" requests;
 *   PC responds with queued messages via the notify characteristic
 *
 * This allows the TransportHealthMonitor to route messages through BLE
 * using the same interface as WebSocket.
 */

import type { TransportConnection, TransportMessageEvent } from './transport';
import { TRANSPORT_READY_STATE } from './transport';
import type { BleEnvelope } from '../utils/ble';

const POLL_INTERVAL_MS = 500; // poll PC for pending messages every 500ms

export class BleTransport implements TransportConnection {
  private _readyState: (typeof TRANSPORT_READY_STATE)[keyof typeof TRANSPORT_READY_STATE] = TRANSPORT_READY_STATE.CLOSED;
  private _notifyCharacteristic: BluetoothRemoteGATTCharacteristic | null = null;
  private _writeCharacteristic: BluetoothRemoteGATTCharacteristic | null = null;
  private _pollTimer: ReturnType<typeof setInterval> | null = null;
  private _failureCount = 0;

  onopen: (() => void) | null = null;
  onmessage: ((event: TransportMessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;

  /** Health tracking callback — called on success/failure */
  onBleSuccess: (() => void) | null = null;
  onBleFailure: (() => void) | null = null;

  get readyState(): number {
    return this._readyState;
  }

  get failureCount(): number {
    return this._failureCount;
  }

  /**
   * Initialize the transport with existing GATT characteristics
   * (already connected and discovered by useBlePairing).
   */
  attach(
    notifyCharacteristic: BluetoothRemoteGATTCharacteristic,
    writeCharacteristic: BluetoothRemoteGATTCharacteristic,
  ): void {
    this._notifyCharacteristic = notifyCharacteristic;
    this._writeCharacteristic = writeCharacteristic;

    // Listen for notifications (PC → mobile messages)
    this._notifyCharacteristic.addEventListener(
      'characteristicvaluechanged',
      this.handleNotification,
    );

    this._readyState = TRANSPORT_READY_STATE.OPEN;
    this._failureCount = 0;
    this.onopen?.();

    // Start polling for pending messages from PC
    this.startPolling();
  }

  async send(data: string): Promise<boolean> {
    if (this._readyState !== TRANSPORT_READY_STATE.OPEN) {
      throw new Error('BleTransport: not open');
    }

    const envelope: BleEnvelope = {
      type: 'encrypted',
      ...JSON.parse(data),
    };

    return this.writeEnvelope(envelope);
  }

  close(_code?: number, _reason?: string): void {
    this.stopPolling();

    if (this._notifyCharacteristic) {
      this._notifyCharacteristic.removeEventListener(
        'characteristicvaluechanged',
        this.handleNotification,
      );
    }

    this._notifyCharacteristic = null;
    this._writeCharacteristic = null;
    this._readyState = TRANSPORT_READY_STATE.CLOSED;
    this.onclose?.();
  }

  /** Send a poll request to the PC to retrieve pending messages */
  sendPoll(): void {
    if (this._readyState !== TRANSPORT_READY_STATE.OPEN) return;

    const envelope: BleEnvelope = { type: 'poll' };
    this.writeEnvelope(envelope);
  }

  private handleNotification = (event: Event): void => {
    const characteristic = event.target as BluetoothRemoteGATTCharacteristic;
    const value = characteristic.value;
    if (!value) return;

    try {
      const bytes = new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
      const text = new TextDecoder().decode(bytes);
      const envelope = JSON.parse(text) as BleEnvelope;

      if (envelope.type === 'encrypted' && envelope.payload) {
        // Forward as a standard transport message
        const msg = {
          type: 'encrypted',
          payload: envelope.payload,
          nonce: envelope.nonce,
          seq: envelope.seq,
        };
        this.onmessage?.({ data: JSON.stringify(msg) });
        this._failureCount = 0;
        this.onBleSuccess?.();
      } else if (envelope.type === 'error') {
        console.error('[BleTransport] PC error:', envelope.payload);
      }
    } catch {
      // Malformed notification — ignore
    }
  };

  private async writeEnvelope(envelope: BleEnvelope): Promise<boolean> {
    if (!this._writeCharacteristic) return false;

    const json = JSON.stringify(envelope);
    const bytes = new TextEncoder().encode(json);
    const buffer = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(buffer).set(bytes);

    try {
      await this._writeCharacteristic.writeValueWithResponse(buffer);
      this._failureCount = 0;
      this.onBleSuccess?.();
      return true;
    } catch (err) {
      this._failureCount++;
      this.onBleFailure?.();
      if (this._failureCount >= 3) {
        console.error('[BleTransport] 3 consecutive failures, closing');
        this.close();
      } else {
        console.warn('[BleTransport] write failed:', err);
      }
      return false;
    }
  }

  private startPolling(): void {
    this.stopPolling();
    this._pollTimer = setInterval(() => {
      this.sendPoll();
    }, POLL_INTERVAL_MS);
  }

  private stopPolling(): void {
    if (this._pollTimer) {
      clearInterval(this._pollTimer);
      this._pollTimer = null;
    }
  }
}
