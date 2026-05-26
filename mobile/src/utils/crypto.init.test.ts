/**
 * H: Startup & initialization edge cases for CryptoService.
 *
 * Tests behavior when local storage is empty, corrupted, or contains
 * invalid data. All tests run without any real browser storage.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { CryptoService } from './crypto';
import { encodeBase64, decodeBase64 } from 'tweetnacl-util';

describe('CryptoService Initialization', () => {
  describe('fresh instance (no persisted state)', () => {
    it('creates valid keypair on construction', () => {
      const svc = new CryptoService();
      const pub = svc.getPublicKeyBase64();
      expect(pub).toBeTruthy();
      expect(decodeBase64(pub).length).toBe(32);
    });

    it('isReady is false without peer key', () => {
      const svc = new CryptoService();
      expect(svc.isReady()).toBe(false);
    });

    it('isRatchetReady is false without init', () => {
      const svc = new CryptoService();
      expect(svc.isRatchetReady()).toBe(false);
    });

    it('isRecvRatchetReady is false without init', () => {
      const svc = new CryptoService();
      expect(svc.isRecvRatchetReady()).toBe(false);
    });

    it('getPeerPublicKeyBase64 returns null', () => {
      const svc = new CryptoService();
      expect(svc.getPeerPublicKeyBase64()).toBeNull();
    });
  });

  describe('importSession with corrupted data', () => {
    it('handles missing publicKey gracefully', () => {
      const svc = new CryptoService();
      expect(() => svc.importSession({
        publicKey: '',
        secretKey: encodeBase64(new Uint8Array(32)),
        peerPublicKey: null,
      })).not.toThrow();
    });

    it('handles corrupted secretKey', () => {
      const svc = new CryptoService();
      // Import with a valid-looking but different key should work
      // (it just replaces the keypair)
      const newKey = encodeBase64(new Uint8Array(32).fill(1));
      svc.importSession({
        publicKey: encodeBase64(new Uint8Array(32).fill(2)),
        secretKey: newKey,
        peerPublicKey: null,
      });
      expect(svc.getPublicKeyBase64()).toBeTruthy();
    });
  });

  describe('export then import roundtrip', () => {
    it('preserves keypair and peer key', () => {
      const svc1 = new CryptoService();
      svc1.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));

      const exported = svc1.exportSession();

      const svc2 = new CryptoService();
      svc2.importSession(exported);

      expect(svc2.getPublicKeyBase64()).toBe(exported.publicKey);
      expect(svc2.isReady()).toBe(true);
    });

    it('preserves keypair without ratchet', () => {
      const sender = new CryptoService();
      sender.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));

      const exported = sender.exportSession();
      const sender2 = new CryptoService();
      sender2.importSession(exported);

      // isReady should be true (has peer key)
      expect(sender2.isReady()).toBe(true);
      // Ratchet needs separate initialization
      expect(sender2.isRatchetReady()).toBe(false);
    });
  });

  describe('reset from any state', () => {
    it('reset from initialized state', () => {
      const svc = new CryptoService();
      svc.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));
      svc.initRatchet();

      svc.reset();
      expect(svc.isReady()).toBe(false);
      expect(svc.isRatchetReady()).toBe(false);
      expect(svc.getPublicKeyBase64()).toBeTruthy();
    });

    it('reset from fresh state', () => {
      const svc = new CryptoService();
      svc.reset();
      expect(svc.getPublicKeyBase64()).toBeTruthy();
    });

    it('reset produces different key', () => {
      const svc = new CryptoService();
      const oldPub = svc.getPublicKeyBase64();
      svc.reset();
      expect(svc.getPublicKeyBase64()).not.toBe(oldPub);
    });
  });

  describe('multiple instances isolation', () => {
    it('instances do not share state', () => {
      const svc1 = new CryptoService();
      const svc2 = new CryptoService();

      svc1.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));
      expect(svc1.isReady()).toBe(true);
      expect(svc2.isReady()).toBe(false);
    });

    it('resetPeerState on one does not affect another', () => {
      const svc1 = new CryptoService();
      svc1.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));
      const exported = svc1.exportSession();

      const svc2 = new CryptoService();
      svc2.importSession(exported);

      svc1.resetPeerState();
      expect(svc1.isReady()).toBe(false);
      expect(svc2.isReady()).toBe(true);
    });
  });
});
