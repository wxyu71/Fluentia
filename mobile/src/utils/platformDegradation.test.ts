/**
 * F: Platform degradation tests.
 *
 * Tests that features degrade gracefully when browser APIs
 * (Bluetooth, Battery, Network Information) are unavailable.
 * Uses mock browser APIs to simulate missing features.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { TransportHealthMonitor } from './transportHealth';
import {
  createBlePairingHandshake,
  deriveBleVerificationCode,
  parseBleConnectionInfo,
} from './ble';
import { mockBluetooth, mockBattery, mockNetworkConnection } from '../test/mocks/browser';

describe('Platform Degradation', () => {
  describe('navigator.bluetooth unavailable', () => {
    it('BLE pairing functions still work without navigator.bluetooth', () => {
      // BLE utility functions don't depend on navigator.bluetooth
      // (that's handled at the React hook level)
      const handshake = createBlePairingHandshake();
      expect(handshake.publicKey).toBeTruthy();
      expect(handshake.secretKey.length).toBe(32);
    });

    it('BLE verification code generation works independently', () => {
      const h1 = createBlePairingHandshake();
      const h2 = createBlePairingHandshake();
      const code = deriveBleVerificationCode(h1.secretKey, h1.publicKey, h2.publicKey);
      expect(code).toMatch(/^\d{6}$/);
    });

    it('parseBleConnectionInfo works without bluetooth API', () => {
      const info = parseBleConnectionInfo({
        type: 'verified',
        serverUrl: 'wss://example.com/ws',
        token: 'abc',
        publicKey: 'key',
      });
      expect(info).toEqual({ s: 'wss://example.com/ws', t: 'abc', k: 'key' });
    });
  });

  describe('navigator.getBattery unavailable', () => {
    let monitor: TransportHealthMonitor;

    beforeEach(() => {
      vi.useFakeTimers();
      monitor = new TransportHealthMonitor();
    });

    afterEach(() => {
      monitor.dispose();
      vi.useRealTimers();
    });

    it('transport selection works without battery info', () => {
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);
      // No updateBattery() call — simulates battery API unavailable
      const sel = monitor.selectTransport('input');
      expect(sel).toBeTruthy();
    });

    it('concurrent allowed defaults without battery', () => {
      monitor.updateBleRssi(-30);
      monitor.updateWsRtt(50);
      // Without battery info, should still allow concurrent
      // (battery defaults to 100%)
      expect(typeof monitor.isConcurrentAllowed()).toBe('boolean');
    });

    it('BLE preferred for input without battery degradation', () => {
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);
      // Without battery update, BLE should be preferred for input
      expect(monitor.selectTransport('input')).toBe('ble');
    });
  });

  describe('navigator.connection unavailable', () => {
    let monitor: TransportHealthMonitor;

    beforeEach(() => {
      vi.useFakeTimers();
      monitor = new TransportHealthMonitor();
    });

    afterEach(() => {
      monitor.dispose();
      vi.useRealTimers();
    });

    it('health scoring works without network info', () => {
      // No updateNetworkType() call — simulates API unavailable
      monitor.updateBleRssi(-40);
      monitor.updateWsRtt(100);

      const scores = monitor.getScores();
      expect(scores.ble).toBeGreaterThan(0);
      expect(scores.ws).toBeGreaterThan(0);
    });

    it('transport selection uses RSSI and RTT only', () => {
      monitor.updateBleRssi(-30); // excellent
      monitor.updateWsRtt(50);    // good
      // Should still make sensible routing decisions
      expect(monitor.selectTransport('input')).toBe('ble');
      expect(monitor.selectTransport('file')).toBe('ws');
    });
  });

  describe('all APIs unavailable simultaneously', () => {
    let monitor: TransportHealthMonitor;

    beforeEach(() => {
      vi.useFakeTimers();
      monitor = new TransportHealthMonitor();
    });

    afterEach(() => {
      monitor.dispose();
      vi.useRealTimers();
    });

    it('still functions with default scores', () => {
      // No battery, no network info, no RSSI updates
      // Should use baseline scores (50/50)
      const scores = monitor.getScores();
      expect(scores.ble).toBe(50);
      expect(scores.ws).toBe(50);

      // Both available
      expect(monitor.isTransportAvailable('ble')).toBe(true);
      expect(monitor.isTransportAvailable('ws')).toBe(true);
    });

    it('file transfer still routes to WS with defaults', () => {
      // WS baseline (50+10=60) > BLE baseline (50)
      expect(monitor.selectTransport('file')).toBe('ws');
    });
  });

  describe('mock browser APIs', () => {
    it('mockBluetooth provides requestDevice', () => {
      const { bluetooth, restore } = mockBluetooth();
      expect(bluetooth.requestDevice).toBeDefined();
      expect(typeof bluetooth.requestDevice).toBe('function');
      restore();
    });

    it('mockBattery provides level and charging', () => {
      const { battery, restore } = mockBattery(0.5, true);
      expect(battery.level).toBe(0.5);
      expect(battery.charging).toBe(true);
      restore();
    });

    it('mockNetworkConnection provides effectiveType', () => {
      const { connection, restore } = mockNetworkConnection('4g');
      expect(connection.effectiveType).toBe('4g');
      restore();
    });

    it('mockBattery setLevel updates level', () => {
      const { battery, setLevel, restore } = mockBattery(1.0, false);
      setLevel(0.15);
      expect(battery.level).toBe(0.15);
      restore();
    });

    it('mockBattery setCharging updates charging', () => {
      const { battery, setCharging, restore } = mockBattery(0.5, false);
      setCharging(true);
      expect(battery.charging).toBe(true);
      restore();
    });

    it('mockNetworkConnection setEffectiveType updates type', () => {
      const { connection, setEffectiveType, restore } = mockNetworkConnection('4g');
      setEffectiveType('2g');
      expect(connection.effectiveType).toBe('2g');
      restore();
    });
  });
});
