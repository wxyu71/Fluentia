/**
 * End-to-end smoke tests for dual-channel routing logic.
 *
 * These tests verify the TransportHealthMonitor + BleTransport integration:
 * - Both channels healthy → input goes to BLE, file goes to WS
 * - BLE down → auto-switch to WS
 * - WS down → auto-switch to BLE
 * - File transfer always uses WS when available
 * - Handshake uses single most reliable channel
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { TransportHealthMonitor } from './transportHealth';
import { MockTransport } from '../test/mocks/transport';
import { TRANSPORT_READY_STATE } from '../services/transport';

describe('Dual-Channel Routing Smoke Tests', () => {
  let health: TransportHealthMonitor;
  let wsTransport: MockTransport;
  let bleTransport: MockTransport;

  beforeEach(() => {
    vi.useFakeTimers();
    health = new TransportHealthMonitor();
    wsTransport = new MockTransport(true);
    bleTransport = new MockTransport(true);
  });

  afterEach(() => {
    health.dispose();
    vi.useRealTimers();
  });

  /** Simulate sending a message through the health-routed transport selection */
  function routeAndSend(messageType: 'input' | 'file' | 'handshake' | 'control'): {
    sentVia: 'ws' | 'ble' | null;
    wsMessages: string[];
    bleMessages: string[];
  } {
    const selected = health.selectTransport(messageType);
    if (!selected) return { sentVia: null, wsMessages: [], bleMessages: [] };

    const testMsg = JSON.stringify({ type: 'test', payload: 'data' });

    if (selected === 'ble' && bleTransport.readyState === TRANSPORT_READY_STATE.OPEN) {
      bleTransport.send(testMsg);
      health.onBleSuccess();
      return { sentVia: 'ble', wsMessages: [], bleMessages: bleTransport.sent };
    }

    if (selected === 'ws' && wsTransport.readyState === TRANSPORT_READY_STATE.OPEN) {
      wsTransport.send(testMsg);
      return { sentVia: 'ws', wsMessages: wsTransport.sent, bleMessages: [] };
    }

    return { sentVia: null, wsMessages: [], bleMessages: [] };
  }

  describe('Both channels healthy', () => {
    it('input messages prefer BLE', () => {
      health.updateBleRssi(-40); // good signal
      health.updateWsRtt(100);

      const result = routeAndSend('input');
      expect(result.sentVia).toBe('ble');
      expect(result.bleMessages.length).toBe(1);
      expect(result.wsMessages.length).toBe(0);
    });

    it('file transfer always uses WS', () => {
      health.updateBleRssi(-30); // excellent BLE signal
      health.updateWsRtt(200); // decent WS

      const result = routeAndSend('file');
      expect(result.sentVia).toBe('ws');
      expect(result.wsMessages.length).toBe(1);
      expect(result.bleMessages.length).toBe(0);
    });

    it('handshake uses most reliable channel (WS by default)', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      const result = routeAndSend('handshake');
      // WS baseline (60+20=80) > BLE baseline (50+20=70)
      expect(result.sentVia).toBe('ws');
    });

    it('control messages use best available', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      const result = routeAndSend('control');
      // WS (80) > BLE (70)
      expect(result.sentVia).toBe('ws');
    });
  });

  describe('BLE down → auto-switch to WS', () => {
    it('input messages fall back to WS when BLE is down', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      // BLE goes down
      health.markDown('ble');

      const result = routeAndSend('input');
      expect(result.sentVia).toBe('ws');
      expect(result.wsMessages.length).toBe(1);
    });

    it('file transfer still uses WS when BLE is down', () => {
      health.markDown('ble');
      health.updateWsRtt(100);

      const result = routeAndSend('file');
      expect(result.sentVia).toBe('ws');
    });
  });

  describe('WS down → auto-switch to BLE', () => {
    it('file transfer falls back to BLE when WS is down', () => {
      health.updateBleRssi(-40);
      health.setWsConnected(false); // WS goes down

      const result = routeAndSend('file');
      expect(result.sentVia).toBe('ble');
      expect(result.bleMessages.length).toBe(1);
    });

    it('input messages use BLE when WS is down', () => {
      health.updateBleRssi(-40);
      health.setWsConnected(false);

      const result = routeAndSend('input');
      expect(result.sentVia).toBe('ble');
    });
  });

  describe('Both channels down', () => {
    it('returns null when no transport available', () => {
      health.markDown('ble');
      health.markDown('ws');

      const result = routeAndSend('input');
      expect(result.sentVia).toBeNull();
    });
  });

  describe('Failover recovery', () => {
    it('BLE recovers after consecutive failures', () => {
      // BLE goes down from failures
      health.onBleFailure();
      health.onBleFailure();
      health.onBleFailure();
      expect(health.isTransportAvailable('ble')).toBe(false);

      // Recovery: BLE reconnects successfully
      health.onBleSuccess();
      expect(health.isTransportAvailable('ble')).toBe(true);

      const result = routeAndSend('input');
      expect(result.sentVia).toBe('ble');
    });

    it('WS recovers after reconnection', () => {
      health.setWsConnected(false);
      expect(health.isTransportAvailable('ws')).toBe(false);

      health.setWsConnected(true);
      health.updateWsRtt(100);
      expect(health.isTransportAvailable('ws')).toBe(true);

      const result = routeAndSend('file');
      expect(result.sentVia).toBe('ws');
    });
  });

  describe('Battery-aware routing', () => {
    it('low battery prefers BLE for all message types', () => {
      health.updateBattery(0.08, false);
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      expect(routeAndSend('input').sentVia).toBe('ble');
      expect(routeAndSend('file').sentVia).toBe('ble');
      expect(routeAndSend('handshake').sentVia).toBe('ble');
    });

    it('charging ignores battery thresholds', () => {
      health.updateBattery(0.05, true);
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      // File should still use WS when charging
      expect(routeAndSend('file').sentVia).toBe('ws');
    });
  });

  describe('Message routing guarantees', () => {
    it('file transfer NEVER uses BLE when WS is available', () => {
      // Even with excellent BLE and poor WS
      health.updateBleRssi(-30);
      health.updateWsRtt(900); // degraded but still available

      const result = routeAndSend('file');
      expect(result.sentVia).toBe('ws');
    });

    it('each message type produces consistent routing', () => {
      health.updateBleRssi(-40);
      health.updateWsRtt(100);

      // Route the same type 10 times — should always pick the same channel
      for (let i = 0; i < 10; i++) {
        const result = routeAndSend('input');
        expect(result.sentVia).toBe('ble');
      }
    });
  });
});
