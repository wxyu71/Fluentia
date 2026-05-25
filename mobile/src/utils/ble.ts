import nacl from 'tweetnacl';
import { encodeBase64, decodeBase64 } from 'tweetnacl-util';
import type { ConnectionInfo } from '../types';

export const BLE_SERVICE_UUID = '21e2f7d4-4dc0-4b0d-a145-5f9b6459be10';
export const BLE_NOTIFY_CHARACTERISTIC_UUID = '21e2f7d4-4dc0-4b0d-a145-5f9b6459be11';
export const BLE_WRITE_CHARACTERISTIC_UUID = '21e2f7d4-4dc0-4b0d-a145-5f9b6459be12';

export interface BleEnvelope {
  type: string;
  publicKey?: string;
  token?: string;
  serverUrl?: string;
  payload?: string;
  nonce?: string;
  seq?: number;
  code?: string;
  approved?: boolean;
  version?: string;
}

export interface BlePairingHandshake {
  publicKey: string;
  secretKey: Uint8Array;
}

export function createBlePairingHandshake(): BlePairingHandshake {
  const keyPair = nacl.box.keyPair();
  return {
    publicKey: encodeBase64(keyPair.publicKey),
    secretKey: keyPair.secretKey,
  };
}

export function deriveBleVerificationCode(localSecretKey: Uint8Array, localPublicKeyBase64: string, remotePublicKeyBase64: string): string {
  const localPublicKey = decodeBase64(localPublicKeyBase64);
  const remotePublicKey = decodeBase64(remotePublicKeyBase64);
  const shared = nacl.scalarMult(localSecretKey.subarray(0, 32), remotePublicKey);

  const combined = new Uint8Array(shared.length + localPublicKey.length + remotePublicKey.length);
  combined.set(shared, 0);
  combined.set(localPublicKey, shared.length);
  combined.set(remotePublicKey, shared.length + localPublicKey.length);

  const hash = nacl.hash(combined);
  const numeric = ((hash[0] << 16) | (hash[1] << 8) | hash[2]) % 1000000;
  return numeric.toString().padStart(6, '0');
}

export function parseBleConnectionInfo(message: BleEnvelope): ConnectionInfo | null {
  if (!message.serverUrl || !message.token || !message.publicKey) {
    return null;
  }

  return {
    s: message.serverUrl,
    t: message.token,
    k: message.publicKey,
  };
}