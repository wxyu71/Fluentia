import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { PROTOCOL_VERSION, type ConnectionInfo, type WsMessage } from '../types';
import {
  BLE_NOTIFY_CHARACTERISTIC_UUID,
  BLE_SERVICE_UUID,
  BLE_WRITE_CHARACTERISTIC_UUID,
  createBlePairingHandshake,
  parseBleConnectionInfo,
  type BleEnvelope,
} from '../utils/ble';

export interface UseBlePairingResult {
  isSupported: boolean;
  isAvailable: boolean;
  status: string;
  error: string | null;
  deviceName: string | null;
  verificationCode: string | null;
  isConnecting: boolean;
  isTransportReady: boolean;
  requestPairing: () => Promise<void>;
  disconnect: () => Promise<void>;
  sendEncryptedMessage: (message: Pick<WsMessage, 'payload' | 'nonce' | 'seq'>) => boolean;
}

function decodeBleEnvelope(view: DataView): BleEnvelope | null {
  try {
    const bytes = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
    const text = new TextDecoder().decode(bytes);
    return JSON.parse(text) as BleEnvelope;
  } catch {
    return null;
  }
}

function encodeBleEnvelope(message: BleEnvelope): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(message));
}

function toArrayBuffer(bytes: Uint8Array): ArrayBuffer {
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
}

export function useBlePairing(
  onConnectionInfo: (info: ConnectionInfo) => void,
  deviceId: string,
  onAuthorizePublicKey: (publicKey: string) => void,
  authorizedPublicKey: string | null,
): UseBlePairingResult {
  const [isAvailable, setIsAvailable] = useState(false);
  const [status, setStatus] = useState('BLE not requested');
  const [error, setError] = useState<string | null>(null);
  const [deviceName, setDeviceName] = useState<string | null>(null);
  const [verificationCode, setVerificationCode] = useState<string | null>(null);
  const [isConnecting, setIsConnecting] = useState(false);
  const [isTransportReady, setIsTransportReady] = useState(false);

  const deviceRef = useRef<BluetoothDevice | null>(null);
  const serverRef = useRef<BluetoothRemoteGATTServer | null>(null);
  const notifyCharacteristicRef = useRef<BluetoothRemoteGATTCharacteristic | null>(null);
  const writeCharacteristicRef = useRef<BluetoothRemoteGATTCharacteristic | null>(null);
  const handshakeRef = useRef(createBlePairingHandshake());
  const helloSentRef = useRef(false);
  const authTimeoutRef = useRef<number | null>(null);
  const [isBleChannelReady, setIsBleChannelReady] = useState(false);

  const isSupported = useMemo(() => typeof navigator !== 'undefined' && 'bluetooth' in navigator, []);

  const clearAuthTimeout = useCallback(() => {
    if (authTimeoutRef.current !== null) {
      window.clearTimeout(authTimeoutRef.current);
      authTimeoutRef.current = null;
    }
  }, []);

  useEffect(() => {
    if (!isSupported || typeof navigator.bluetooth.getAvailability !== 'function') {
      setIsAvailable(false);
      return;
    }

    let mounted = true;
    navigator.bluetooth.getAvailability()
      .then((available: boolean) => {
        if (mounted) {
          setIsAvailable(available);
        }
      })
      .catch(() => {
        if (mounted) {
          setIsAvailable(false);
        }
      });

    return () => {
      mounted = false;
    };
  }, [isSupported]);

  const disconnect = useCallback(async () => {
    clearAuthTimeout();
    try {
      notifyCharacteristicRef.current?.removeEventListener('characteristicvaluechanged', () => undefined);
    } catch {
    }

    if (serverRef.current?.connected) {
      serverRef.current.disconnect();
    }

    deviceRef.current = null;
    serverRef.current = null;
    notifyCharacteristicRef.current = null;
    writeCharacteristicRef.current = null;
    helloSentRef.current = false;
    setIsBleChannelReady(false);
    setDeviceName(null);
    setVerificationCode(null);
    setIsTransportReady(false);
    setIsConnecting(false);
    setStatus('BLE disconnected');
  }, [clearAuthTimeout]);

  const sendClientHelloIfAuthorized = useCallback(async (candidatePublicKey: string | null) => {
    const writeCharacteristic = writeCharacteristicRef.current;
    if (!writeCharacteristic || !isBleChannelReady || helloSentRef.current) {
      return;
    }

    if (!candidatePublicKey || candidatePublicKey !== handshakeRef.current.publicKey) {
      return;
    }

    helloSentRef.current = true;
    clearAuthTimeout();

    try {
      await writeCharacteristic.writeValueWithResponse(toArrayBuffer(encodeBleEnvelope({
        type: 'client_hello',
        publicKey: handshakeRef.current.publicKey,
        payload: deviceId,
        version: PROTOCOL_VERSION,
      })));

      setStatus('Waiting for PC response');
      setError(null);
    } catch (caughtError) {
      helloSentRef.current = false;
      const message = caughtError instanceof Error ? caughtError.message : 'BLE pairing failed';
      setError(message);
      setStatus('BLE unavailable');
      await disconnect();
    }
  }, [clearAuthTimeout, deviceId, disconnect, isBleChannelReady]);

  useEffect(() => {
    void sendClientHelloIfAuthorized(authorizedPublicKey);
  }, [authorizedPublicKey, sendClientHelloIfAuthorized]);

  const handleNotify = useCallback((event: Event) => {
    const target = event.target as BluetoothRemoteGATTCharacteristic | null;
    if (!target?.value) {
      return;
    }

    const message = decodeBleEnvelope(target.value);
    if (!message) {
      return;
    }

    if (message.type === 'verified') {
      const info = parseBleConnectionInfo(message);
      if (info) {
        setError(null);
        setVerificationCode(null);
        setStatus('BLE pairing approved');
        setIsTransportReady(true);
        onConnectionInfo(info);
      }
      return;
    }

    if (message.type === 'error') {
      setError(message.payload || 'BLE pairing failed');
      setStatus('BLE error');
    }
  }, [onConnectionInfo]);

  const requestPairing = useCallback(async () => {
    if (!isSupported) {
      setError('This browser does not support Web Bluetooth.');
      return;
    }

    setIsConnecting(true);
    setError(null);
    setStatus('Searching nearby PC');
    setVerificationCode(null);
    clearAuthTimeout();
    handshakeRef.current = createBlePairingHandshake();
    helloSentRef.current = false;
    setIsBleChannelReady(false);

    try {
      const device = await navigator.bluetooth.requestDevice({
        filters: [{ services: [BLE_SERVICE_UUID] }],
        optionalServices: [BLE_SERVICE_UUID],
      });

      deviceRef.current = device;
      setDeviceName(device.name ?? 'Fluentia nearby PC');
      setStatus('Connecting over BLE');

      const server = await device.gatt?.connect();
      if (!server) {
        throw new Error('BLE GATT connection failed');
      }

      serverRef.current = server;
      const service = await server.getPrimaryService(BLE_SERVICE_UUID).catch(() => null);
      if (!service) {
        throw new Error('Selected device does not expose the Fluentia BLE service');
      }

      const notifyCharacteristic = await service.getCharacteristic(BLE_NOTIFY_CHARACTERISTIC_UUID);
      const writeCharacteristic = await service.getCharacteristic(BLE_WRITE_CHARACTERISTIC_UUID);

      notifyCharacteristicRef.current = notifyCharacteristic;
      writeCharacteristicRef.current = writeCharacteristic;

      await notifyCharacteristic.startNotifications();
      notifyCharacteristic.addEventListener('characteristicvaluechanged', handleNotify);

      setIsBleChannelReady(true);
      setStatus('Authorizing with PC');
      onAuthorizePublicKey(handshakeRef.current.publicKey);
      authTimeoutRef.current = window.setTimeout(() => {
        if (!helloSentRef.current) {
          setError('PC did not confirm BLE authorization. Retry from the QR-paired session.');
          setStatus('BLE authorization timeout');
        }
      }, 8000);

      await sendClientHelloIfAuthorized(authorizedPublicKey);
    } catch (caughtError) {
      const message = caughtError instanceof Error ? caughtError.message : 'BLE pairing failed';
      setError(message);
      setStatus('BLE unavailable');
      await disconnect();
    } finally {
      setIsConnecting(false);
    }
  }, [authorizedPublicKey, clearAuthTimeout, deviceId, disconnect, handleNotify, isSupported, onAuthorizePublicKey, sendClientHelloIfAuthorized]);

  const sendEncryptedMessage = useCallback((message: Pick<WsMessage, 'payload' | 'nonce' | 'seq'>) => {
    const writeCharacteristic = writeCharacteristicRef.current;
    if (!writeCharacteristic || !isTransportReady || !message.payload || !message.nonce) {
      return false;
    }

    void writeCharacteristic.writeValueWithResponse(toArrayBuffer(encodeBleEnvelope({
      type: 'encrypted',
      payload: message.payload,
      nonce: message.nonce,
      seq: message.seq,
      version: PROTOCOL_VERSION,
    }))).catch(() => {
      setError('BLE transport send failed');
      setStatus('BLE transport error');
      setIsTransportReady(false);
    });

    return true;
  }, [isTransportReady]);

  return {
    isSupported,
    isAvailable,
    status,
    error,
    deviceName,
    verificationCode,
    isConnecting,
    isTransportReady,
    requestPairing,
    disconnect,
    sendEncryptedMessage,
  };
}