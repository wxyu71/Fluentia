import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { PROTOCOL_VERSION, type ConnectionInfo, type WsMessage } from '../types';
import {
  BLE_NOTIFY_CHARACTERISTIC_UUID,
  BLE_SERVICE_UUID,
  BLE_WRITE_CHARACTERISTIC_UUID,
  createBlePairingHandshake,
  deriveBleVerificationCode,
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

async function requestGrantedDevice(): Promise<BluetoothDevice | null> {
  if (typeof navigator.bluetooth.getDevices !== 'function') {
    return null;
  }

  const devices = await navigator.bluetooth.getDevices();
  return devices[0] ?? null;
}

export function useBlePairing(onConnectionInfo: (info: ConnectionInfo) => void, deviceId: string): UseBlePairingResult {
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

  const isSupported = useMemo(() => typeof navigator !== 'undefined' && 'bluetooth' in navigator, []);

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
    setDeviceName(null);
    setVerificationCode(null);
    setIsTransportReady(false);
    setIsConnecting(false);
    setStatus('BLE disconnected');
  }, []);

  const handleNotify = useCallback((event: Event) => {
    const target = event.target as BluetoothRemoteGATTCharacteristic | null;
    if (!target?.value) {
      return;
    }

    const message = decodeBleEnvelope(target.value);
    if (!message) {
      return;
    }

    if (message.type === 'desktop_hello' && message.publicKey) {
      setVerificationCode(deriveBleVerificationCode(handshakeRef.current.secretKey, handshakeRef.current.publicKey, message.publicKey));
      setStatus('Compare the 6-digit code with your PC');
      return;
    }

    if (message.type === 'verification_code' && message.code) {
      setVerificationCode(message.code);
      setStatus('Compare the 6-digit code with your PC');
      return;
    }

    if (message.type === 'verified') {
      const info = parseBleConnectionInfo(message);
      if (info) {
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
    setStatus('Requesting Bluetooth device');
    setVerificationCode(null);
    handshakeRef.current = createBlePairingHandshake();

    try {
      const device = await requestGrantedDevice() ?? await navigator.bluetooth.requestDevice({
        acceptAllDevices: true,
        optionalServices: [BLE_SERVICE_UUID],
      });

      deviceRef.current = device;
      setDeviceName(device.name ?? 'Fluentia BLE device');
      setStatus('Connecting to BLE device');

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

      await writeCharacteristic.writeValueWithResponse(toArrayBuffer(encodeBleEnvelope({
        type: 'client_hello',
        publicKey: handshakeRef.current.publicKey,
        payload: deviceId,
        version: PROTOCOL_VERSION,
      })));

      setStatus('BLE connected. Waiting for PC verification');
    } catch (caughtError) {
      const message = caughtError instanceof Error ? caughtError.message : 'BLE pairing failed';
      setError(message);
      setStatus('BLE unavailable');
      await disconnect();
    } finally {
      setIsConnecting(false);
    }
  }, [deviceId, disconnect, handleNotify, isSupported]);

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