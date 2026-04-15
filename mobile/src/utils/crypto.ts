import nacl from 'tweetnacl';
import { encodeBase64, decodeBase64 } from 'tweetnacl-util';

export class CryptoService {
  private keyPair: nacl.BoxKeyPair;
  private peerPublicKey: Uint8Array | null = null;
  private ready = false;

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
  }
}
