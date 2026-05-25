import { useState } from 'react';

const DEVICE_ID_KEY = 'fluentia_device_id';

function generateDeviceId(): string {
  const array = new Uint8Array(16);
  crypto.getRandomValues(array);
  return Array.from(array, (b) => b.toString(16).padStart(2, '0')).join('');
}

export function useDeviceId(): string {
  const [deviceId] = useState<string>(() => {
    let id = localStorage.getItem(DEVICE_ID_KEY);
    if (!id) {
      id = generateDeviceId();
      localStorage.setItem(DEVICE_ID_KEY, id);
    }
    return id;
  });
  return deviceId;
}
