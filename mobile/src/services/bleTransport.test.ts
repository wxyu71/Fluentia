import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { BleTransport } from './bleTransport';
import { TRANSPORT_READY_STATE } from './transport';

// Mock GATT characteristic
function createMockCharacteristic() {
  const listeners: Record<string, ((event: Event) => void)[]> = {};
  return {
    addEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (!listeners[type]) listeners[type] = [];
      listeners[type].push(handler);
    }),
    removeEventListener: vi.fn((type: string, handler: (event: Event) => void) => {
      if (listeners[type]) {
        listeners[type] = listeners[type].filter((h) => h !== handler);
      }
    }),
    writeValueWithResponse: vi.fn().mockResolvedValue(undefined),
    value: null,
    _listeners: listeners,
    _simulateNotification(data: Record<string, unknown>) {
      const json = JSON.stringify(data);
      const bytes = new TextEncoder().encode(json);
      const view = {
        buffer: bytes.buffer,
        byteOffset: bytes.byteOffset,
        byteLength: bytes.byteLength,
      };
      // Create a mock event
      const event = { target: { value: view } } as unknown as Event;
      for (const handler of listeners['characteristicvaluechanged'] ?? []) {
        handler(event);
      }
    },
  };
}

describe('BleTransport', () => {
  let transport: BleTransport;
  let mockNotify: ReturnType<typeof createMockCharacteristic>;
  let mockWrite: ReturnType<typeof createMockCharacteristic>;

  beforeEach(() => {
    vi.useFakeTimers();
    transport = new BleTransport();
    mockNotify = createMockCharacteristic();
    mockWrite = createMockCharacteristic();
  });

  afterEach(() => {
    transport.close();
    vi.useRealTimers();
  });

  describe('initial state', () => {
    it('starts closed', () => {
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
    });
  });

  describe('attach', () => {
    it('sets readyState to OPEN', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.OPEN);
    });

    it('calls onopen callback', () => {
      const onopen = vi.fn();
      transport.onopen = onopen;
      transport.attach(mockNotify as any, mockWrite as any);
      expect(onopen).toHaveBeenCalledOnce();
    });

    it('registers notification listener', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      expect(mockNotify.addEventListener).toHaveBeenCalledWith(
        'characteristicvaluechanged',
        expect.any(Function),
      );
    });

    it('starts polling', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      // Advance timer to trigger poll
      vi.advanceTimersByTime(500);
      expect(mockWrite.writeValueWithResponse).toHaveBeenCalled();
    });
  });

  describe('send', () => {
    it('writes envelope to characteristic', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      transport.send(JSON.stringify({ payload: 'test', nonce: 'n', seq: 1 }));

      expect(mockWrite.writeValueWithResponse).toHaveBeenCalled();
      const call = mockWrite.writeValueWithResponse.mock.calls[0][0];
      const sent = JSON.parse(new TextDecoder().decode(new Uint8Array(call)));
      expect(sent.type).toBe('encrypted');
      expect(sent.payload).toBe('test');
    });

    it('throws when not open', () => {
      expect(() => transport.send('test')).toThrow('BleTransport: not open');
    });
  });

  describe('close', () => {
    it('sets readyState to CLOSED', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      transport.close();
      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
    });

    it('calls onclose callback', () => {
      const onclose = vi.fn();
      transport.onclose = onclose;
      transport.attach(mockNotify as any, mockWrite as any);
      transport.close();
      expect(onclose).toHaveBeenCalledOnce();
    });

    it('removes notification listener', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      transport.close();
      expect(mockNotify.removeEventListener).toHaveBeenCalledWith(
        'characteristicvaluechanged',
        expect.any(Function),
      );
    });

    it('stops polling', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      transport.close();
      mockWrite.writeValueWithResponse.mockClear();
      vi.advanceTimersByTime(2000);
      expect(mockWrite.writeValueWithResponse).not.toHaveBeenCalled();
    });
  });

  describe('notification handling', () => {
    it('forwards encrypted notifications to onmessage', () => {
      const onmessage = vi.fn();
      transport.onmessage = onmessage;
      transport.attach(mockNotify as any, mockWrite as any);

      mockNotify._simulateNotification({
        type: 'encrypted',
        payload: 'encrypted-data',
        nonce: 'nonce-data',
        seq: 5,
      });

      expect(onmessage).toHaveBeenCalledOnce();
      const event = onmessage.mock.calls[0][0];
      const msg = JSON.parse(event.data);
      expect(msg.type).toBe('encrypted');
      expect(msg.payload).toBe('encrypted-data');
      expect(msg.seq).toBe(5);
    });

    it('calls onBleSuccess on successful notification', () => {
      const onSuccess = vi.fn();
      transport.onBleSuccess = onSuccess;
      transport.attach(mockNotify as any, mockWrite as any);

      mockNotify._simulateNotification({
        type: 'encrypted',
        payload: 'data',
        nonce: 'n',
      });

      expect(onSuccess).toHaveBeenCalled();
    });

    it('ignores non-encrypted notifications', () => {
      const onmessage = vi.fn();
      transport.onmessage = onmessage;
      transport.attach(mockNotify as any, mockWrite as any);

      mockNotify._simulateNotification({ type: 'error', payload: 'some error' });

      expect(onmessage).not.toHaveBeenCalled();
    });
  });

  describe('failure tracking', () => {
    it('increments failure count on write failure', async () => {
      mockWrite.writeValueWithResponse.mockRejectedValue(new Error('write failed'));
      transport.attach(mockNotify as any, mockWrite as any);

      transport.send(JSON.stringify({ payload: 'test' }));
      // Wait for the promise to reject
      await vi.advanceTimersByTimeAsync(10);

      expect(transport.failureCount).toBe(1);
    });

    it('closes after 3 consecutive failures', async () => {
      mockWrite.writeValueWithResponse.mockRejectedValue(new Error('write failed'));
      const onclose = vi.fn();
      transport.onclose = onclose;
      transport.attach(mockNotify as any, mockWrite as any);

      // Trigger 3 failures
      transport.send(JSON.stringify({ payload: '1' }));
      await vi.advanceTimersByTimeAsync(10);
      transport.send(JSON.stringify({ payload: '2' }));
      await vi.advanceTimersByTimeAsync(10);
      transport.send(JSON.stringify({ payload: '3' }));
      await vi.advanceTimersByTimeAsync(10);

      expect(transport.readyState).toBe(TRANSPORT_READY_STATE.CLOSED);
    });
  });

  describe('poll mechanism', () => {
    it('sends poll envelopes periodically', () => {
      transport.attach(mockNotify as any, mockWrite as any);
      mockWrite.writeValueWithResponse.mockClear();

      vi.advanceTimersByTime(500);
      expect(mockWrite.writeValueWithResponse).toHaveBeenCalled();

      const call = mockWrite.writeValueWithResponse.mock.calls[0][0];
      const sent = JSON.parse(new TextDecoder().decode(new Uint8Array(call)));
      expect(sent.type).toBe('poll');
    });

    it('sendPoll does nothing when closed', () => {
      transport.sendPoll(); // should not throw
      expect(mockWrite.writeValueWithResponse).not.toHaveBeenCalled();
    });
  });
});
