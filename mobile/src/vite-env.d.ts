/// <reference types="vite/client" />

declare module 'tweetnacl-util' {
  export function encodeUTF8(arr: Uint8Array): string;
  export function decodeUTF8(str: string): Uint8Array;
  export function encodeBase64(arr: Uint8Array): string;
  export function decodeBase64(str: string): Uint8Array;
}

declare const __APP_VERSION__: string;

// Intl.Segmenter is available in modern browsers but not in TypeScript's default lib
declare namespace Intl {
  interface SegmenterOptions {
    granularity?: 'grapheme' | 'word' | 'sentence';
    localeMatcher?: 'lookup' | 'best fit';
  }
  interface Segment {
    segment: string;
    index: number;
    input: string;
    isWordLike?: boolean;
  }
  class Segmenter {
    constructor(locale?: string, options?: SegmenterOptions);
    segment(input: string): Iterable<Segment>;
  }
}

interface Navigator {
  bluetooth: Bluetooth;
  getBattery?: () => Promise<BatteryManager>;
}

interface BatteryManager extends EventTarget {
  charging: boolean;
  chargingTime: number;
  dischargingTime: number;
  level: number;
}

interface Bluetooth {
  requestDevice(options?: BluetoothRequestDeviceOptions): Promise<BluetoothDevice>;
  getAvailability?: () => Promise<boolean>;
  getDevices?: () => Promise<BluetoothDevice[]>;
}

interface BluetoothRequestDeviceOptions {
  filters?: BluetoothLEScanFilter[];
  optionalServices?: BluetoothServiceUUID[];
  acceptAllDevices?: boolean;
}

interface BluetoothLEScanFilter {
  services?: BluetoothServiceUUID[];
  name?: string;
  namePrefix?: string;
}

type BluetoothServiceUUID = string;

interface BluetoothDevice extends EventTarget {
  id: string;
  name?: string;
  gatt?: BluetoothRemoteGATTServer;
}

interface BluetoothRemoteGATTServer {
  connected: boolean;
  connect(): Promise<BluetoothRemoteGATTServer>;
  disconnect(): void;
  getPrimaryService(service: BluetoothServiceUUID): Promise<BluetoothRemoteGATTService>;
}

interface BluetoothRemoteGATTService {
  getCharacteristic(characteristic: BluetoothServiceUUID): Promise<BluetoothRemoteGATTCharacteristic>;
}

interface BluetoothRemoteGATTCharacteristic extends EventTarget {
  value?: DataView;
  startNotifications(): Promise<BluetoothRemoteGATTCharacteristic>;
  writeValueWithResponse(value: BufferSource): Promise<void>;
}
