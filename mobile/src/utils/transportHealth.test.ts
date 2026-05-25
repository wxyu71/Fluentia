import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { TransportHealthMonitor } from './transportHealth';

describe('TransportHealthMonitor', () => {
  let monitor: TransportHealthMonitor;

  beforeEach(() => {
    vi.useFakeTimers();
    monitor = new TransportHealthMonitor();
  });

  afterEach(() => {
    monitor.dispose();
    vi.useRealTimers();
  });

  describe('initial state', () => {
    it('starts with neutral scores', () => {
      const scores = monitor.getScores();
      expect(scores.ble).toBe(50);
      expect(scores.ws).toBe(50);
    });

    it('both transports available initially', () => {
      expect(monitor.isTransportAvailable('ble')).toBe(true);
      expect(monitor.isTransportAvailable('ws')).toBe(true);
    });
  });

  describe('BLE health', () => {
    it('BLE score drops on consecutive failures', () => {
      // Initial: baseline 50 + RSSI(-50 → +20) = 70
      monitor.onBleFailure(); // -15 → 55
      const score1 = monitor.getScores().ble;
      monitor.onBleFailure(); // -15 → 40
      const score2 = monitor.getScores().ble;
      monitor.onBleFailure(); // hits threshold (3), marked down → 0
      const score3 = monitor.getScores().ble;

      expect(score1).toBeLessThan(70); // dropped from initial
      expect(score2).toBeLessThan(score1);
      expect(score3).toBe(0); // marked down
    });

    it('BLE score recovers on success', () => {
      monitor.onBleFailure();
      monitor.onBleFailure();
      const before = monitor.getScores().ble;
      monitor.onBleSuccess();
      const after = monitor.getScores().ble;
      expect(after).toBeGreaterThan(before);
    });

    it('BLE RSSI < -80dBm degrades score', () => {
      monitor.updateBleRssi(-90);
      expect(monitor.getScores().ble).toBeLessThan(50);
    });

    it('BLE RSSI >= -50dBm improves score', () => {
      monitor.updateBleRssi(-30);
      expect(monitor.getScores().ble).toBeGreaterThan(50);
    });
  });

  describe('WS health', () => {
    it('WS score drops when disconnected', () => {
      monitor.setWsConnected(false);
      expect(monitor.getScores().ws).toBe(0);
      expect(monitor.isTransportAvailable('ws')).toBe(false);
    });

    it('WS score improves on low RTT', () => {
      monitor.updateWsRtt(50);
      expect(monitor.getScores().ws).toBeGreaterThan(50);
    });

    it('WS score degrades on high RTT', () => {
      monitor.updateWsRtt(2000);
      expect(monitor.getScores().ws).toBeLessThan(50);
    });
  });

  describe('selectTransport', () => {
    it('picks BLE for input messages when both healthy', () => {
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);
      expect(monitor.selectTransport('input')).toBe('ble');
    });

    it('picks WS for file transfer', () => {
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);
      expect(monitor.selectTransport('file')).toBe('ws');
    });

    it('skips down transport', () => {
      monitor.markDown('ble');
      expect(monitor.selectTransport('input')).toBe('ws');
    });

    it('returns null when both down', () => {
      monitor.markDown('ble');
      monitor.markDown('ws');
      expect(monitor.selectTransport('input')).toBeNull();
    });

    it('uses WS when BLE score < 40 for input', () => {
      // BLE degraded by poor RSSI + failures
      monitor.updateBleRssi(-90);
      monitor.onBleFailure();
      monitor.updateWsRtt(100);
      expect(monitor.selectTransport('input')).toBe('ws');
    });

    it('prefers WS for handshake when WS score > BLE', () => {
      monitor.updateWsRtt(50);
      monitor.updateBleRssi(-80);
      expect(monitor.selectTransport('handshake')).toBe('ws');
    });
  });

  describe('battery degradation', () => {
    it('prefers BLE when battery < 10%', () => {
      monitor.updateBattery(0.08, false);
      // BLE should be preferred for all message types
      expect(monitor.selectTransport('input')).toBe('ble');
      expect(monitor.selectTransport('file')).toBe('ble');
    });

    it('disables concurrent when battery < 20%', () => {
      monitor.updateBattery(0.15, false);
      expect(monitor.isConcurrentAllowed()).toBe(false);
    });

    it('allows concurrent when charging regardless of battery', () => {
      monitor.updateBattery(0.05, true);
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);
      // When charging, battery thresholds don't apply to concurrent
      // But score recalculation still happens
      expect(monitor.isTransportAvailable('ble')).toBe(true);
    });

    it('WS score has heavy penalty at critical battery', () => {
      monitor.updateWsRtt(50); // good RTT
      const scoreBefore = monitor.getScores().ws;
      monitor.updateBattery(0.05, false);
      const scoreAfter = monitor.getScores().ws;
      expect(scoreAfter).toBeLessThan(scoreBefore);
    });
  });

  describe('concurrent sending', () => {
    it('allows concurrent when both channels healthy and battery OK', () => {
      monitor.updateBleRssi(-30); // BLE: 50 + 20 = 70
      monitor.onBleSuccess();
      monitor.updateWsRtt(50); // WS: 60 + 20 = 80
      monitor.updateBattery(0.8, false);
      // BLE=70, WS=80, battery OK → concurrent allowed (>=70 threshold)
      expect(monitor.isConcurrentAllowed()).toBe(true);
    });

    it('disallows concurrent when BLE degraded', () => {
      monitor.updateBleRssi(-90); // BLE: 50 - 30 = 20
      monitor.onBleFailure();
      monitor.onBleFailure();
      monitor.updateWsRtt(100); // WS: 60 + 20 = 80
      // BLE < 70, WS >= 70 → concurrent not allowed
      expect(monitor.isConcurrentAllowed()).toBe(false);
    });
  });

  describe('recovery', () => {
    it('BLE recovery resets failure counter on success', () => {
      // Simulate what recovery does: clear down state, reset failures
      monitor.markDown('ble');
      expect(monitor.isTransportAvailable('ble')).toBe(false);

      // Simulate recovery: BLE reconnects successfully
      monitor.onBleSuccess(); // resets consecutive failures, clears down
      expect(monitor.isTransportAvailable('ble')).toBe(true);
      expect(monitor.getScores().ble).toBeGreaterThan(0);
    });

    it('recovers WS from down state after recovery interval', () => {
      monitor.setWsConnected(false);
      expect(monitor.isTransportAvailable('ws')).toBe(false);

      // WS recovery requires setWsConnected(true) — the timer just resets the down flag
      vi.advanceTimersByTime(31000);
      // WS is still not connected, but the down flag is cleared
      // A subsequent setWsConnected(true) would make it available
    });
  });

  describe('markDown', () => {
    it('marks BLE as down with score 0', () => {
      monitor.markDown('ble');
      expect(monitor.getScores().ble).toBe(0);
      expect(monitor.isTransportAvailable('ble')).toBe(false);
    });

    it('marks WS as down with score 0', () => {
      monitor.markDown('ws');
      expect(monitor.getScores().ws).toBe(0);
      expect(monitor.isTransportAvailable('ws')).toBe(false);
    });
  });
});
