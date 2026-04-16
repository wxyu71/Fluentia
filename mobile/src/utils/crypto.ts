import nacl from 'tweetnacl';
import { encodeBase64, decodeBase64 } from 'tweetnacl-util';

/**
 * KDF: SHA-512(key || label)[0:32]
 * Must match the C# implementation exactly.
 */
function kdf(key: Uint8Array, label: string): Uint8Array {
  const labelBytes = new TextEncoder().encode(label);
  const input = new Uint8Array(key.length + labelBytes.length);
  input.set(key);
  input.set(labelBytes, key.length);
  return nacl.hash(input).subarray(0, 32);
}

function ratchetStep(chainKey: Uint8Array): { messageKey: Uint8Array; nextChainKey: Uint8Array } {
  return {
    messageKey: kdf(chainKey, 'msg'),
    nextChainKey: kdf(chainKey, 'chain'),
  };
}

export class CryptoService {
  private keyPair: nacl.BoxKeyPair;
  private peerPublicKey: Uint8Array | null = null;
  private ready = false;

  // Symmetric ratchet state for forward secrecy
  private sendChainKey: Uint8Array | null = null;
  private sendSeq = 0;
  private _ratchetReady = false;

  constructor() {
    this.keyPair = nacl.box.keyPair();
  }

  getPublicKeyBase64(): string {
    return encodeBase64(this.keyPair.publicKey);
  }

  setPeerPublicKey(base64Key: string): void {
    this.peerPublicKey = decodeBase64(base64Key);
    this.ready = true;
  }

  isReady(): boolean {
    return this.ready;
  }

  isRatchetReady(): boolean {
    return this._ratchetReady;
  }

  /**
   * Initialize the symmetric ratchet. Returns the seed that must be sent
   * to the peer (encrypted with crypto_box) so they can initialize their chain.
   */
  initRatchet(): { seed: string } {
    const seed = nacl.randomBytes(32);
    this.sendChainKey = kdf(seed, 'fluentia_chain_v1');
    this.sendSeq = 0;
    this._ratchetReady = true;
    return { seed: encodeBase64(seed) };
  }

  /**
   * Legacy crypto_box encryption (used for ratchet_init and pre-ratchet messages).
   */
  encrypt(plaintext: string): { payload: string; nonce: string } {
    if (!this.peerPublicKey) {
      throw new Error('Peer public key not set');
    }
    const nonce = nacl.randomBytes(nacl.box.nonceLength);
    const messageBytes = new TextEncoder().encode(plaintext);
    const encrypted = nacl.box(messageBytes, nonce, this.peerPublicKey, this.keyPair.secretKey);
    if (!encrypted) {
      throw new Error('Encryption failed');
    }
    return {
      payload: encodeBase64(encrypted),
      nonce: encodeBase64(nonce),
    };
  }

  /**
   * Ratcheted encryption using nacl.secretbox with forward secrecy.
   * Each message advances the chain — old keys are irrecoverable.
   */
  encryptRatcheted(plaintext: string): { payload: string; nonce: string; seq: number } {
    if (!this.sendChainKey) throw new Error('Ratchet not initialized');

    const { messageKey, nextChainKey } = ratchetStep(this.sendChainKey);
    this.sendChainKey = nextChainKey;
    const seq = this.sendSeq++;

    const nonce = nacl.randomBytes(nacl.secretbox.nonceLength);
    const messageBytes = new TextEncoder().encode(plaintext);
    const encrypted = nacl.secretbox(messageBytes, nonce, messageKey);
    if (!encrypted) throw new Error('Ratchet encryption failed');

    // Wipe message key for forward secrecy
    messageKey.fill(0);

    return {
      payload: encodeBase64(encrypted),
      nonce: encodeBase64(nonce),
      seq,
    };
  }

  decrypt(payloadBase64: string, nonceBase64: string): string {
    if (!this.peerPublicKey) {
      throw new Error('Peer public key not set');
    }
    const decrypted = nacl.box.open(
      decodeBase64(payloadBase64),
      decodeBase64(nonceBase64),
      this.peerPublicKey,
      this.keyPair.secretKey
    );
    if (!decrypted) {
      throw new Error('Decryption failed - invalid key or corrupted data');
    }
    return new TextDecoder().decode(decrypted);
  }

  reset(): void {
    this.keyPair = nacl.box.keyPair();
    this.peerPublicKey = null;
    this.ready = false;
    this.sendChainKey = null;
    this.sendSeq = 0;
    this._ratchetReady = false;
  }
}
