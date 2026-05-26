/**
 * Tests that demonstrate the core bug: BleTransport.send() is fire-and-forget.
 *
 * ROOT CAUSE: send() calls writeEnvelope() which calls writeValueWithResponse().
 * The GATT write is async but send() doesn't await it. So send() returns void
 * immediately, and the caller has no way to know if the write will succeed.
 *
 * This causes the 9MB file transfer bug:
 * - 144 chunks are sent rapidly
 * - Each send() returns immediately (void)
 * - GATT writes pile up and eventually overflow
 * - 3 consecutive failures → BLE closes
 * - File transfer fails silently
 */

/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, it, expect, vi } from 'vitest';
import { BleTransport } from './bleTransport';
import { TRANSPORT_READY_STATE } from './transport';

function createGATTMock() {
  const listeners: Record<string, ((event: Event) => void)[]> = {};
  let writeCount = 0;

  return {
    addEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (!listeners[type]) listeners[type] = [];
      listeners[type].push(handler);
    }),
    removeEventListener: vi.fn(),
    writeValueWithResponse: vi.fn().mockResolvedValue(undefined),
    get writeCount() { return writeCount; },
    incrementWrite() { writeCount++; },
  };
}

describe('sendEncryptedPayload Bug — fire-and-forget', () => {
  it('FIX: send() returns Promise<boolean> — caller can detect GATT failure', async () => {
    const transport = new BleTransport();
    const mockNotify = createGATTMock();
    const mockWrite = createGATTMock();
    transport.attach(mockNotify as any, mockWrite as any);

    const result = transport.send(JSON.stringify({
      type: 'encrypted',
      payload: 'test',
      nonce: 'n',
    }));

    // After fix: send() returns Promise<boolean>
    expect(result).toBeInstanceOf(Promise);
    const success = await result;
    expect(success).toBe(true);

    transport.close();
  });

  it('multiple sends all return without error even when GATT will fail', () => {
    const transport = new BleTransport();
    const mockNotify = createGATTMock();
    const mockWrite = createGATTMock();
    transport.attach(mockNotify as any, mockWrite as any);

    let throwCount = 0;

    // Send 100 messages — none throw
    for (let i = 0; i < 100; i++) {
      try {
        transport.send(JSON.stringify({
          type: 'encrypted',
          payload: `chunk-${i}`,
          nonce: 'n',
        }));
      } catch {
        throwCount++;
      }
    }

    // All sends returned without throwing
    expect(throwCount).toBe(0);
    // But we have no idea if the GATT writes will succeed!
    expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);

    transport.close();
  });

  it('writeValueWithResponse is called for each send', () => {
    const transport = new BleTransport();
    const mockNotify = createGATTMock();
    const mockWrite = createGATTMock();
    transport.attach(mockNotify as any, mockWrite as any);

    // Send 5 messages
    for (let i = 0; i < 5; i++) {
      transport.send(JSON.stringify({
        type: 'encrypted',
        payload: `chunk-${i}`,
        nonce: 'n',
      }));
    }

    // writeValueWithResponse was called 5 times (once per send)
    expect(mockWrite.writeValueWithResponse).toHaveBeenCalledTimes(5);

    transport.close();
  });

  it('BLE transport state is OPEN during sends — no early detection of failure', () => {
    const transport = new BleTransport();
    const mockNotify = createGATTMock();
    const mockWrite = createGATTMock();
    transport.attach(mockNotify as any, mockWrite as any);

    // Transport is OPEN
    expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);

    // Send many messages — state remains OPEN
    for (let i = 0; i < 50; i++) {
      transport.send(JSON.stringify({
        type: 'encrypted',
        payload: `chunk-${i}`,
        nonce: 'n',
      }));
      // State is still OPEN — no way to detect GATT failure
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
    }

    transport.close();
  });

  it('close() during sends does not prevent pending writes', () => {
    const transport = new BleTransport();
    const mockNotify = createGATTMock();
    const mockWrite = createGATTMock();
    transport.attach(mockNotify as any, mockWrite as any);

    // Send 5 messages
    for (let i = 0; i < 5; i++) {
      transport.send(JSON.stringify({
        type: 'encrypted',
        payload: `chunk-${i}`,
        nonce: 'n',
      }));
    }

    // Close transport — but GATT writes are still pending!
    transport.close();
    expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);

    // writeValueWithResponse was already called 5 times
    // Those promises are still pending — nothing cancels them
    expect(mockWrite.writeValueWithResponse).toHaveBeenCalledTimes(5);
  });
});
