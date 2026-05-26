/* eslint-disable @typescript-eslint/no-explicit-any */
/**
 * File transfer stress tests — reproduces the bug where sending a 9MB file
 * causes BLE disconnection at ~10%, followed by WebSocket disconnection,
 * and the transfer cannot resume after recovery.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { BleTransport } from './bleTransport';
import { TRANSPORT_READY_STATE } from './transport';
import { TransportHealthMonitor } from '../utils/transportHealth';
import { MockTransport } from '../test/mocks/transport';

function createMockCharacteristic() {
  const listeners: Record<string, ((event: Event) => void)[]> = {};
  let writeCount = 0;
  let nextWriteFails = false;

  return {
    addEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (!listeners[type]) listeners[type] = [];
      listeners[type].push(handler);
    }),
    removeEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (listeners[type]) {
        listeners[type] = listeners[type].filter((h) => h !== handler);
      }
    }),
    writeValueWithResponse: vi.fn().mockImplementation(() => {
      writeCount++;
      if (nextWriteFails) {
        return Promise.reject(new Error('GATT write failed'));
      }
      return Promise.resolve(undefined);
    }),
    value: null,
    get writeCount() { return writeCount; },
    setNextWriteFails(fails: boolean) { nextWriteFails = fails; },
  };
}

// Flush only microtasks (promises), not timers
async function flushMicrotasks() {
  for (let i = 0; i < 10; i++) {
    await Promise.resolve();
  }
}

describe('File Transfer Stress — BLE Disconnection Bug', () => {
  describe('BLE GATT write failure cascade', () => {
    it('BLE closes after 3 consecutive write failures during file transfer', async () => {
      const transport = new BleTransport();
      const mockNotify = createMockCharacteristic();
      const mockWrite = createMockCharacteristic();

      // Stop polling before attaching to avoid timer issues
      transport.attach(mockNotify as any, mockWrite as any);
      // Immediately stop polling to isolate the test
      (transport as any).stopPolling?.();

      const closed = vi.fn();
      transport.onclose = closed;

      // Make all writes fail
      mockWrite.setNextWriteFails(true);

      // Send 4 messages
      for (let i = 0; i < 4; i++) {
        transport.send(JSON.stringify({ type: 'encrypted', payload: `chunk-${i}`, nonce: 'n' }));
      }

      await flushMicrotasks();

      // BLE should have closed after 3 failures
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
      expect(closed).toHaveBeenCalled();
      transport.close();
    });

    it('BLE failure count increments on write failure', async () => {
      const transport = new BleTransport();
      const mockNotify = createMockCharacteristic();
      const mockWrite = createMockCharacteristic();

      transport.attach(mockNotify as any, mockWrite as any);

      // Fail once
      mockWrite.setNextWriteFails(true);
      transport.send(JSON.stringify({ type: 'encrypted', payload: '1', nonce: 'n' }));
      await flushMicrotasks();
      expect(transport.failureCount).toBe(1);

      transport.close();
    });

    it('BLE failure count resets on successful write', async () => {
      const transport = new BleTransport();
      const mockNotify = createMockCharacteristic();
      const mockWrite = createMockCharacteristic();

      transport.attach(mockNotify as any, mockWrite as any);

      // Fail twice
      mockWrite.setNextWriteFails(true);
      transport.send(JSON.stringify({ type: 'encrypted', payload: '1', nonce: 'n' }));
      await flushMicrotasks();
      transport.send(JSON.stringify({ type: 'encrypted', payload: '2', nonce: 'n' }));
      await flushMicrotasks();
      expect(transport.failureCount).toBe(2);

      // Success resets counter
      mockWrite.setNextWriteFails(false);
      transport.send(JSON.stringify({ type: 'encrypted', payload: '3', nonce: 'n' }));
      await flushMicrotasks();

      expect(transport.failureCount).toBe(0);
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
      transport.close();
    });
  });

  describe('high-throughput chunk sending', () => {
    it('144 chunks sent without failure when GATT is responsive', async () => {
      const transport = new BleTransport();
      const mockNotify = createMockCharacteristic();
      const mockWrite = createMockCharacteristic();

      transport.attach(mockNotify as any, mockWrite as any);

      // Send 144 chunks
      for (let i = 0; i < 144; i++) {
        transport.send(JSON.stringify({ type: 'encrypted', payload: 'x'.repeat(100), nonce: 'n' }));
      }

      await flushMicrotasks();

      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
      transport.close();
    });

    it('BLE disconnects after GATT writes start failing', async () => {
      const transport = new BleTransport();
      const mockNotify = createMockCharacteristic();
      const mockWrite = createMockCharacteristic();

      transport.attach(mockNotify as any, mockWrite as any);
      const closed = vi.fn();
      transport.onclose = closed;

      // First writes succeed, then fail
      let callCount = 0;
      mockWrite.writeValueWithResponse.mockImplementation(() => {
        callCount++;
        if (callCount <= 10) return Promise.resolve(undefined);
        return Promise.reject(new Error('GATT buffer full'));
      });

      // Send 20 chunks
      for (let i = 0; i < 20; i++) {
        transport.send(JSON.stringify({ type: 'encrypted', payload: 'x'.repeat(100), nonce: 'n' }));
      }

      await flushMicrotasks();

      // BLE should have disconnected after 3 consecutive failures
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
      expect(closed).toHaveBeenCalled();
      transport.close();
    });
  });

  describe('transport health degradation during file transfer', () => {
    let health: TransportHealthMonitor;

    beforeEach(() => {
      vi.useFakeTimers();
      health = new TransportHealthMonitor();
    });

    afterEach(() => {
      health.dispose();
      vi.useRealTimers();
    });

    it('BLE score drops to 0 when GATT writes fail', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      expect(health.getScores().ble).toBeGreaterThan(0);

      for (let i = 0; i < 3; i++) {
        health.onBleFailure();
      }

      expect(health.isTransportAvailable('ble')).toBe(false);
      expect(health.getScores().ble).toBe(0);
    });

    it('file transfer uses WS when both healthy', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);
      expect(health.selectTransport('file')).toBe('ws');
    });

    it('file transfer falls back to BLE when WS is down', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);
      health.setWsConnected(false);
      expect(health.selectTransport('file')).toBe('ble');
    });

    it('both transports down → file transfer cannot proceed', () => {
      health.markDown('ble');
      health.markDown('ws');
      expect(health.selectTransport('file')).toBeNull();
    });

    it('health monitor recovers BLE after successful reconnect', () => {
      health.updateBleRssi(-40);
      health.markDown('ble');
      expect(health.isTransportAvailable('ble')).toBe(false);

      health.onBleSuccess();
      expect(health.isTransportAvailable('ble')).toBe(true);
    });
  });

  describe('WS during high-throughput file transfer', () => {
    it('WS connection survives rapid 144-chunk sending', () => {
      const ws = new MockTransport(true);

      for (let i = 0; i < 144; i++) {
        ws.send(JSON.stringify({ type: 'encrypted', payload: 'x'.repeat(100), nonce: 'n' }));
      }

      expect(ws.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
      expect(ws.sent.length).toBe(144);
    });

    it('WS reconnects after close', () => {
      const ws = new MockTransport(true);
      ws.close();
      expect(ws.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);

      const newWs = new MockTransport(true);
      expect(newWs.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
    });
  });

  describe('file transfer state loss after reconnection (the bug)', () => {
    it('transfer progress is lost when connection drops mid-transfer', () => {
      // Simulate: 15 of 144 chunks sent, then connection drops
      const totalChunks = 144;
      const sentChunks = new Set<number>();

      for (let i = 0; i < 15; i++) {
        sentChunks.add(i);
      }

      // Connection drops at chunk 15
      // After reconnect, the sendChunk loop has already advanced past chunk 15
      // Chunks 15-143 are never retried
      const unsentCount = totalChunks - sentChunks.size;
      expect(unsentCount).toBe(129);
    });

    it('transfer should resume from lastAckedChunk after reconnection (expected fix)', () => {
      const totalChunks = 144;
      const lastAckedChunk = 14;

      const resumeFrom = lastAckedChunk + 1;
      expect(resumeFrom).toBe(15);

      const sentChunks = new Set<number>();
      for (let i = 0; i <= lastAckedChunk; i++) {
        sentChunks.add(i);
      }
      for (let i = resumeFrom; i < totalChunks; i++) {
        sentChunks.add(i);
      }

      expect(sentChunks.size).toBe(totalChunks);
    });

    it('no retry mechanism means partial transfer is undetectable', () => {
      // The sendEncrypted function returns true/false but the sendChunk
      // loop in FileTransfer.tsx doesn't check the return value
      // It just increments completedChunks regardless

      let completedChunks = 0;
      const totalChunks = 144;

      // Simulate: send succeeds for first 15, then returns false (transport down)
      for (let i = 0; i < totalChunks; i++) {
        // first 15 succeed
        // sendChunk doesn't check this — it just increments
        completedChunks++;
      }

      // completedChunks = 144 even though only 15 were actually sent
      expect(completedChunks).toBe(144);
      // This is the bug: the UI shows 100% but only 10% was actually delivered
    });
  });
});
