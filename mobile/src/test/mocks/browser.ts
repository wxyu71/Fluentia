/**
 * Reusable browser API mocks for testing.
 * Each factory returns a cleanup function to restore the original.
 */

/** Mock navigator.bluetooth (Web Bluetooth API) */
export function mockBluetooth(deviceName = 'Fluentia-PC') {
  const mockCharacteristic = {
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    startNotifications: vi.fn().mockResolvedValue(undefined),
    stopNotifications: vi.fn().mockResolvedValue(undefined),
    writeValueWithResponse: vi.fn().mockResolvedValue(undefined),
    value: null,
  };

  const mockService = {
    getCharacteristic: vi.fn().mockResolvedValue(mockCharacteristic),
  };

  const mockServer = {
    connected: true,
    getPrimaryService: vi.fn().mockResolvedValue(mockService),
    disconnect: vi.fn(),
  };

  const mockDevice = {
    name: deviceName,
    gatt: {
      connect: vi.fn().mockResolvedValue(mockServer),
    },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };

  const bluetooth = {
    requestDevice: vi.fn().mockResolvedValue(mockDevice),
    getAvailability: vi.fn().mockResolvedValue(true),
  };

  const original = navigator.bluetooth;
  Object.defineProperty(navigator, 'bluetooth', {
    value: bluetooth,
    writable: true,
    configurable: true,
  });

  return {
    bluetooth,
    mockDevice,
    mockServer,
    mockService,
    mockCharacteristic,
    restore() {
      Object.defineProperty(navigator, 'bluetooth', {
        value: original,
        writable: true,
        configurable: true,
      });
    },
  };
}

/** Mock navigator.getBattery */
export function mockBattery(level = 1.0, charging = false) {
  const battery = {
    level,
    charging,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };

  const original = navigator.getBattery;
  Object.defineProperty(navigator, 'getBattery', {
    value: vi.fn().mockResolvedValue(battery),
    writable: true,
    configurable: true,
  });

  return {
    battery,
    setLevel(l: number) {
      battery.level = l;
    },
    setCharging(c: boolean) {
      battery.charging = c;
    },
    restore() {
      Object.defineProperty(navigator, 'getBattery', {
        value: original,
        writable: true,
        configurable: true,
      });
    },
  };
}

/** Mock navigator.connection (Network Information API) */
export function mockNetworkConnection(effectiveType = '4g') {
  const connection = {
    effectiveType,
    downlink: 10,
    rtt: 50,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };

  const original = (navigator as any).connection;
  Object.defineProperty(navigator, 'connection', {
    value: connection,
    writable: true,
    configurable: true,
  });

  return {
    connection,
    setEffectiveType(t: string) {
      connection.effectiveType = t;
    },
    restore() {
      Object.defineProperty(navigator, 'connection', {
        value: original,
        writable: true,
        configurable: true,
      });
    },
  };
}

/** Mock localStorage */
export function mockLocalStorage() {
  const store: Record<string, string> = {};
  const original = window.localStorage;

  const mock: Storage = {
    get length() { return Object.keys(store).length; },
    clear() { for (const k in store) delete store[k]; },
    getItem(k) { return store[k] ?? null; },
    setItem(k, v) { store[k] = String(v); },
    removeItem(k) { delete store[k]; },
    key(i) { return Object.keys(store)[i] ?? null; },
  };

  Object.defineProperty(window, 'localStorage', { value: mock, configurable: true });

  return {
    store,
    restore() {
      Object.defineProperty(window, 'localStorage', { value: original, configurable: true });
    },
  };
}
