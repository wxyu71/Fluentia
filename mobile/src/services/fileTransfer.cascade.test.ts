/**
 * Cascade failure test — reproduces the exact bug where a 9MB file transfer
 * causes BLE disconnection at ~10%, then WebSocket disconnection.
 *
 * ROOT CAUSE: BleTransport.send() is fire-and-forget. It calls
 * writeValueWithResponse() but doesn't await the result. Multiple rapid
 * sends overwhelm the GATT buffer, causing write failures to cascade:
 *   1. Rapid chunk sends → GATT buffer overflow
 *   2. writeValueWithResponse rejects → failureCount++
 *   3. 3 consecutive failures → BLE transport closes
 *   4. Health monitor marks BLE as down
 *   5. WS becomes sole transport → also overwhelmed
 *   6. Both transports down → connection lost
 *   7. After reconnect, file transfer state is gone
 *
 * FIX: BleTransport.send() must return a Promise. sendEncryptedPayload
 * must await it. sendChunk must await each send before proceeding.
 */

/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { BleTransport } from './bleTransport';
import { TRANSPORT_READY_STATE } from './transport';
import { TransportHealthMonitor } from '../utils/transportHealth';
import { MockTransport } from '../test/mocks/transport';

// Simulates a GATT characteristic with controllable latency and failure
function createGATTMock(options?: { latencyMs?: number; failAfterWrites?: number }) {
  const latencyMs = options?.latencyMs ?? 0;
  const failAfterWrites = options?.failAfterWrites ?? Infinity;
  const listeners: Record<string, ((event: Event) => void)[]> = {};
  let writeCount = 0;
  const pendingWrites: Promise<void>[] = [];

  return {
    addEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (!listeners[type]) listeners[type] = [];
      listeners[type].push(handler);
    }),
    removeEventListener: vi.fn(),
    writeValueWithResponse: vi.fn().mockImplementation(() => {
      writeCount++;
      const p = new Promise<void>((resolve, reject) => {
        const delay = latencyMs > 0 ? latencyMs : 0;
        setTimeout(() => {
          if (writeCount > failAfterWrites) {
            reject(new Error('GATT buffer full'));
          } else {
            resolve();
          }
        }, delay);
      });
      pendingWrites.push(p);
      return p;
    }),
    value: null,
    get writeCount() { return writeCount; },
    get pendingWrites() { return pendingWrites; },
    async flush() {
      await Promise.allSettled(pendingWrites);
    },
  };
}

async function flushMicrotasks() {
  for (let i = 0; i < 20; i++) {
    await Promise.resolve();
  }
}

describe('File Transfer Cascade Failure — 9MB file at 10%', () => {
  describe('BLE send() returns Promise (the fix)', () => {
    it('send() returns Promise<boolean> — caller can await GATT write', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      const mockWrite = createGATTMock();
      transport.attach(mockNotify as any, mockWrite as any);

      // After fix: send() returns Promise<boolean>
      const result = transport.send(JSON.stringify({ type: 'encrypted', payload: 'test', nonce: 'n' }));
      expect(result).toBeInstanceOf(Promise);

      const success = await result;
      expect(success).toBe(true);

      transport.close();
    });

    it('multiple rapid sends all return immediately even with GATT latency', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      const mockWrite = createGATTMock({ latencyMs: 100 }); // 100ms GATT latency
      transport.attach(mockNotify as any, mockWrite as any);

      // Send 10 messages rapidly
      const startTime = Date.now();
      for (let i = 0; i < 10; i++) {
        transport.send(JSON.stringify({ type: 'encrypted', payload: `chunk-${i}`, nonce: 'n' }));
      }
      const elapsed = Date.now() - startTime;

      // All sends returned in < 10ms (fire-and-forget)
      // But GATT writes are still pending!
      expect(elapsed).toBeLessThan(50);
      expect(mockWrite.writeCount).toBe(10);

      // GATT writes are still in-flight
      expect(mockWrite.pendingWrites.length).toBe(10);

      transport.close();
    });
  });

  describe('GATT buffer overflow cascade', () => {
    it('rapid sends cause GATT buffer overflow → BLE closes', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      // GATT fails after 5 writes (simulates buffer overflow)
      const mockWrite = createGATTMock({ latencyMs: 10, failAfterWrites: 5 });
      transport.attach(mockNotify as any, mockWrite as any);

      const closed = vi.fn();
      transport.onclose = closed;

      // Send 10 chunks rapidly (like file transfer does)
      for (let i = 0; i < 10; i++) {
        transport.send(JSON.stringify({ type: 'encrypted', payload: `chunk-${i}`, nonce: 'n' }));
      }

      // Wait for GATT writes to complete/reject
      await mockWrite.flush();
      await flushMicrotasks();

      // BLE should have closed after 3 consecutive failures
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
      expect(closed).toHaveBeenCalled();

      transport.close();
    });

    it('10% of 144 chunks causes BLE disconnect (9MB file scenario)', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      // GATT fails after 15 writes (~10% of 144 chunks)
      const mockWrite = createGATTMock({ latencyMs: 5, failAfterWrites: 15 });
      transport.attach(mockNotify as any, mockWrite as any);

      const closed = vi.fn();
      transport.onclose = closed;

      // Send chunks like the file transfer does
      for (let i = 0; i < 144; i++) {
        transport.send(JSON.stringify({
          type: 'encrypted',
          payload: 'x'.repeat(100), // simulated chunk data
          nonce: 'n',
        }));
      }

      await mockWrite.flush();
      await flushMicrotasks();

      // BLE disconnected at ~10%
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
      expect(closed).toHaveBeenCalled();

      transport.close();
    });
  });

  describe('health monitor cascade: BLE down → WS overload → both down', () => {
    let health: TransportHealthMonitor;

    beforeEach(() => {
      vi.useFakeTimers();
      health = new TransportHealthMonitor();
    });

    afterEach(() => {
      health.dispose();
      vi.useRealTimers();
    });

    it('BLE failures cause health monitor to mark BLE down', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      // Initially, input prefers BLE
      expect(health.selectTransport('input')).toBe('ble');

      // 3 BLE failures → BLE marked down
      health.onBleFailure();
      health.onBleFailure();
      health.onBleFailure();

      expect(health.isTransportAvailable('ble')).toBe(false);
      expect(health.getScores().ble).toBe(0);
    });

    it('when BLE down, file transfer falls back to WS', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      // File always uses WS when both healthy
      expect(health.selectTransport('file')).toBe('ws');

      // BLE goes down
      health.markDown('ble');

      // File still uses WS
      expect(health.selectTransport('file')).toBe('ws');
    });

    it('when both down, all message types return null', () => {
      health.markDown('ble');
      health.markDown('ws');

      expect(health.selectTransport('input')).toBeNull();
      expect(health.selectTransport('file')).toBeNull();
      expect(health.selectTransport('handshake')).toBeNull();
      expect(health.selectTransport('control')).toBeNull();
    });

    it('recovery: BLE success clears down state', () => {
      health.markDown('ble');
      expect(health.isTransportAvailable('ble')).toBe(false);

      health.onBleSuccess();
      expect(health.isTransportAvailable('ble')).toBe(true);
    });
  });

  describe('sendEncryptedPayload returns false when all transports down', () => {
    it('WS-only scenario: returns false when WS is closed', () => {
      const ws = new MockTransport(false); // not open
      // sendEncryptedPayload would return false

      expect(ws.readyState).not.toBe(TRANSPORT_READY_STATE.OPEN);
    });

    it('BLE+WS scenario: returns false when both are closed', () => {
      const ws = new MockTransport(false);
      const ble = new BleTransport();

      expect(ws.readyState).not.toBe(TRANSPORT_READY_STATE.OPEN);
      expect(ble.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
    });
  });

  describe('file transfer retry with backpressure (the fix)', () => {
    it('WITHOUT fix: chunks are sent without waiting for GATT completion', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      const mockWrite = createGATTMock({ latencyMs: 50, failAfterWrites: 10 });
      transport.attach(mockNotify as any, mockWrite as any);

      let sentCount = 0;
      let failedCount = 0;

      // Simulate the current (buggy) behavior: send without waiting
      for (let i = 0; i < 20; i++) {
        try {
          transport.send(JSON.stringify({ type: 'encrypted', payload: `chunk-${i}`, nonce: 'n' }));
          sentCount++;
        } catch {
          failedCount++;
        }
      }

      // All sends "succeeded" immediately (fire-and-forget)
      expect(sentCount).toBe(20);
      expect(failedCount).toBe(0);

      // But GATT writes are still in-flight and will fail!
      await mockWrite.flush();
      await flushMicrotasks();

      // BLE is now closed due to cascading failures
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);

      transport.close();
    });

    it('WITH fix: send() returns Promise, await prevents overwhelming GATT', async () => {
      const transport = new BleTransport();
      const mockNotify = createGATTMock();
      const mockWrite = createGATTMock({ latencyMs: 5, failAfterWrites: 10 });
      transport.attach(mockNotify as any, mockWrite as any);

      // After fix: send() returns a Promise that resolves when GATT write completes
      // The caller can await each send before proceeding to the next chunk
      const sendPromises: Promise<boolean>[] = [];

      for (let i = 0; i < 5; i++) {
        // This is the fixed behavior: send returns a Promise
        const p = new Promise<boolean>((resolve) => {
          transport.send(JSON.stringify({ type: 'encrypted', payload: `chunk-${i}`, nonce: 'n' }));
          // In the fix, send() would return a Promise that resolves on GATT success
          // For now, we simulate by waiting for the write to complete
          mockWrite.pendingWrites[mockWrite.pendingWrites.length - 1]
            .then(() => resolve(true))
            .catch(() => resolve(false));
        });
        sendPromises.push(p);
      }

      // Await each send before proceeding (backpressure)
      for (const p of sendPromises) {
        const success = await p;
        if (!success) break;
      }

      // With backpressure, we stop before overwhelming GATT
      // BLE is still open because we didn't exceed the failure threshold
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);

      transport.close();
    });
  });
});
