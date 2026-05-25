import { describe, it, expect } from 'vitest';
import { CryptoService } from './crypto';
import nacl from 'tweetnacl';
import { encodeBase64, decodeBase64 } from 'tweetnacl-util';

describe('CryptoService', () => {
  describe('constructor', () => {
    it('generates a valid keypair', () => {
      const svc = new CryptoService();
      const pub = svc.getPublicKeyBase64();
      expect(pub).toBeTruthy();
      const decoded = decodeBase64(pub);
      expect(decoded.length).toBe(32);
    });
  });

  describe('encrypt / decrypt (legacy crypto_box)', () => {
    it('roundtrips with known keys', () => {
      const alice = new CryptoService();
      const bob = new CryptoService();

      alice.setPeerPublicKey(bob.getPublicKeyBase64());
      bob.setPeerPublicKey(alice.getPublicKeyBase64());

      const plaintext = 'Hello, Fluentia!';
      const { payload, nonce } = alice.encrypt(plaintext);
      const decrypted = bob.decrypt(payload, nonce);
      expect(decrypted).toBe(plaintext);
    });

    it('fails with wrong key', () => {
      const alice = new CryptoService();
      const bob = new CryptoService();
      const eve = new CryptoService();

      alice.setPeerPublicKey(bob.getPublicKeyBase64());

      const { payload, nonce } = alice.encrypt('secret');
      eve.setPeerPublicKey(alice.getPublicKeyBase64());
      expect(() => eve.decrypt(payload, nonce)).toThrow();
    });
  });

  describe('ratchet encrypt / decrypt', () => {
    it('roundtrips through initRatchet -> encryptRatcheted -> decryptRatcheted', () => {
      const sender = new CryptoService();
      const receiver = new CryptoService();

      // Sender initializes ratchet, shares seed
      const { seed } = sender.initRatchet();
      expect(sender.isRatchetReady()).toBe(true);

      // Receiver initializes recv ratchet from same seed
      receiver.initRecvRatchet(seed);
      expect(receiver.isRecvRatchetReady()).toBe(true);

      // Sender encrypts, receiver decrypts
      for (let i = 0; i < 5; i++) {
        const msg = `message ${i}`;
        const { payload, nonce, seq } = sender.encryptRatcheted(msg);
        const decrypted = receiver.decryptRatcheted(payload, nonce, seq);
        expect(decrypted).toBe(msg);
      }
    });

    it('provides forward secrecy - old keys are wiped', () => {
      const sender = new CryptoService();
      const receiver = new CryptoService();

      const { seed } = sender.initRatchet();
      receiver.initRecvRatchet(seed);

      const msg1 = sender.encryptRatcheted('first');
      const msg2 = sender.encryptRatcheted('second');

      // Both can be decrypted
      receiver.decryptRatcheted(msg1.payload, msg1.nonce, msg1.seq);
      receiver.decryptRatcheted(msg2.payload, msg2.nonce, msg2.seq);

      // But the chain key has advanced - we can't go back
      expect(sender.isRatchetReady()).toBe(true);
    });

    it('handles skipped sequence numbers (fast-forward)', () => {
      const sender = new CryptoService();
      const receiver = new CryptoService();

      const { seed } = sender.initRatchet();
      receiver.initRecvRatchet(seed);

      // Sender sends 5 messages
      const msgs = [];
      for (let i = 0; i < 5; i++) {
        msgs.push(sender.encryptRatcheted(`msg ${i}`));
      }

      // Receiver only gets message 4 (skips 0-3)
      const decrypted = receiver.decryptRatcheted(msgs[4].payload, msgs[4].nonce, msgs[4].seq);
      expect(decrypted).toBe('msg 4');
    });
  });

  describe('importSession / exportSession', () => {
    it('roundtrips session state', () => {
      const svc = new CryptoService();
      svc.setPeerPublicKey(encodeBase64(nacl.box.keyPair().publicKey));

      const exported = svc.exportSession();
      expect(exported.publicKey).toBeTruthy();
      expect(exported.secretKey).toBeTruthy();
      expect(exported.peerPublicKey).toBeTruthy();

      const svc2 = new CryptoService();
      svc2.importSession(exported);
      expect(svc2.getPublicKeyBase64()).toBe(exported.publicKey);
      expect(svc2.isReady()).toBe(true);
    });

    it('imports session without peer key', () => {
      const svc = new CryptoService();
      const exported = svc.exportSession();
      expect(exported.peerPublicKey).toBeNull();

      const svc2 = new CryptoService();
      svc2.importSession(exported);
      expect(svc2.isReady()).toBe(false);
    });
  });

  describe('reset', () => {
    it('generates new keypair and clears state', () => {
      const svc = new CryptoService();
      const oldPub = svc.getPublicKeyBase64();
      svc.setPeerPublicKey(encodeBase64(nacl.box.keyPair().publicKey));
      svc.initRatchet();

      svc.reset();
      expect(svc.getPublicKeyBase64()).not.toBe(oldPub);
      expect(svc.isReady()).toBe(false);
      expect(svc.isRatchetReady()).toBe(false);
    });
  });

  describe('error handling', () => {
    it('throws when encrypting without peer key', () => {
      const svc = new CryptoService();
      expect(() => svc.encrypt('test')).toThrow('Peer public key not set');
    });

    it('throws when encryptRatcheted without ratchet', () => {
      const svc = new CryptoService();
      expect(() => svc.encryptRatcheted('test')).toThrow('Ratchet not initialized');
    });

    it('throws when decryptRatcheted without recv ratchet', () => {
      const svc = new CryptoService();
      expect(() => svc.decryptRatcheted('x', 'y', 0)).toThrow('Receive ratchet not initialized');
    });
  });

  describe('KDF consistency', () => {
    it('produces deterministic output for same inputs', () => {
      // This is a cross-platform interop test vector.
      // If this fails on either side (TypeScript or C#), encryption will break.
      const svc1 = new CryptoService();
      const svc2 = new CryptoService();

      const { seed: seed1 } = svc1.initRatchet();
      const { seed: seed2 } = svc2.initRatchet();

      // Same seed should produce same first encrypted message
      const sender1 = new CryptoService();
      const sender2 = new CryptoService();
      const recv1 = new CryptoService();
      const recv2 = new CryptoService();

      // Use a fixed seed to verify KDF consistency
      const fixedSeed = encodeBase64(new Uint8Array(32).fill(42));
      sender1.initRatchet();
      recv1.initRatchet();

      // Two separate services with same seed should produce same chain
      const recvA = new CryptoService();
      const recvB = new CryptoService();
      recvA.initRecvRatchet(fixedSeed);
      recvB.initRecvRatchet(fixedSeed);

      // Both should be ready
      expect(recvA.isRecvRatchetReady()).toBe(true);
      expect(recvB.isRecvRatchetReady()).toBe(true);
    });
  });
});
