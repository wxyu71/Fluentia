/**
 * P2: Performance benchmark tests for CryptoService and diff algorithm.
 *
 * These tests establish performance baselines and detect regressions.
 * They run as part of the test suite but focus on timing rather than correctness.
 */

import { describe, it, expect } from 'vitest';
import { CryptoService } from './crypto';
import { computeDiff } from './diff';
import { encodeBase64 } from 'tweetnacl-util';

function measureMs(fn: () => void): number {
  const start = performance.now();
  fn();
  return performance.now() - start;
}

describe('CryptoService Performance', () => {
  it('single encrypt completes within 20ms', () => {
    const svc = new CryptoService();
    svc.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));

    const ms = measureMs(() => svc.encrypt('test message'));
    expect(ms).toBeLessThan(20);
  });

  it('100 sequential encryptions complete within 500ms', () => {
    const svc = new CryptoService();
    svc.setPeerPublicKey(encodeBase64(new Uint8Array(32).fill(1)));

    const ms = measureMs(() => {
      for (let i = 0; i < 100; i++) {
        svc.encrypt(`message ${i}`);
      }
    });
    expect(ms).toBeLessThan(500);
  });

  it('ratchet encrypt 1000 messages completes within 2s', () => {
    const sender = new CryptoService();
    const { seed } = sender.initRatchet();
    const receiver = new CryptoService();
    receiver.initRecvRatchet(seed);

    const ms = measureMs(() => {
      for (let i = 0; i < 1000; i++) {
        sender.encryptRatcheted(`msg ${i}`);
      }
    });
    expect(ms).toBeLessThan(2000);
  });

  it('ratchet decrypt 1000 messages completes within 2s', () => {
    const sender = new CryptoService();
    const { seed } = sender.initRatchet();
    const receiver = new CryptoService();
    receiver.initRecvRatchet(seed);

    const messages: { payload: string; nonce: string; seq: number }[] = [];
    for (let i = 0; i < 1000; i++) {
      messages.push(sender.encryptRatcheted(`msg ${i}`));
    }

    const ms = measureMs(() => {
      for (const msg of messages) {
        receiver.decryptRatcheted(msg.payload, msg.nonce, msg.seq);
      }
    });
    expect(ms).toBeLessThan(2000);
  });

  it('encrypt/decrypt 100KB payload within 100ms', () => {
    const sender = new CryptoService();
    const { seed } = sender.initRatchet();
    const receiver = new CryptoService();
    receiver.initRecvRatchet(seed);

    const payload = 'x'.repeat(100 * 1024);

    const encMs = measureMs(() => {
      sender.encryptRatcheted(payload);
    });
    expect(encMs).toBeLessThan(100);

    const msg = sender.encryptRatcheted(payload);
    const decMs = measureMs(() => {
      receiver.decryptRatcheted(msg.payload, msg.nonce, msg.seq);
    });
    expect(decMs).toBeLessThan(100);
  });

  it('keypair generation completes within 10ms', () => {
    const ms = measureMs(() => {
      new CryptoService();
    });
    expect(ms).toBeLessThan(10);
  });
});

describe('Diff Algorithm Performance', () => {
  it('computeDiff for 1KB strings completes within 1ms', () => {
    const old = 'a'.repeat(1000);
    const new_ = 'a'.repeat(999) + 'b';

    const ms = measureMs(() => computeDiff(old, new_));
    expect(ms).toBeLessThan(1);
  });

  it('computeDiff for 100KB strings completes within 50ms', () => {
    const old = 'a'.repeat(100000);
    const new_ = old + 'b';

    const ms = measureMs(() => computeDiff(old, new_));
    expect(ms).toBeLessThan(50);
  });

  it('computeDiff for identical strings completes within 0.1ms', () => {
    const text = 'a'.repeat(10000);
    const ms = measureMs(() => computeDiff(text, text));
    expect(ms).toBeLessThan(0.1);
  });

  it('1000 diff operations on typical input complete within 100ms', () => {
    const ms = measureMs(() => {
      for (let i = 0; i < 1000; i++) {
        computeDiff('hello world', 'hello there world');
      }
    });
    expect(ms).toBeLessThan(100);
  });
});
