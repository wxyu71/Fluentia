/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { BleTransport } from './bleTransport';
import { TRANSPORT_READY_STATE } from './transport';

describe('BleTransport.send', () => {
  let transport: BleTransport;

  beforeEach(() => {
    transport = new BleTransport();
    // Simulate attached state by calling attach with mock characteristics
    // We need to mock the internals
    (transport as any)._readyState = TRANSPORT_READY_STATE.OPEN;
    (transport as any)._writeCharacteristic = {
      writeValueWithResponse: vi.fn().mockResolvedValue(undefined),
    };
  });

  it('send_InvalidJSON_ReturnsFalse: invalid JSON returns false', async () => {
    const result = await transport.send('not json');
    expect(result).toBe(false);
  });

  it('send_ValidJSON_ReturnsTrue: valid JSON returns true', async () => {
    const result = await transport.send('{"type":"ping"}');
    expect(result).toBe(true);
  });

  it('send_ValidEncryptedJSON_ReturnsTrue: encrypted message returns true', async () => {
    const result = await transport.send('{"type":"encrypted","payload":"abc","nonce":"xyz"}');
    expect(result).toBe(true);
  });

  it('send_ThrowsWhenNotOpen: closed transport throws', async () => {
    (transport as any)._readyState = TRANSPORT_READY_STATE.CLOSED;
    await expect(transport.send('{"type":"ping"}')).rejects.toThrow('BleTransport: not open');
  });
});
