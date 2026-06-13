import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Mock WebSocket before importing the module
let mockSocketInstance: any = null;

class MockWebSocket {
  onopen: any = null;
  onmessage: any = null;
  onclose: any = null;
  onerror: any = null;
  readyState = 1;

  constructor(public url: string) {
    mockSocketInstance = this;
  }

  send = vi.fn();
  close = vi.fn();
}

describe('websocketTransport onMessage', () => {
  beforeEach(() => {
    vi.stubGlobal('WebSocket', MockWebSocket as any);
    mockSocketInstance = null;
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('onMessage_BinaryData_LogsWarning: non-string data logs warning', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { createWebSocketTransport } = await import('./websocketTransport');

    const transport = createWebSocketTransport('ws://localhost');

    // Simulate a binary message via the mock socket's onmessage
    const binaryEvent = { data: new ArrayBuffer(10) };
    mockSocketInstance.onmessage(binaryEvent);

    expect(warnSpy).toHaveBeenCalledWith(
      '[WebSocketTransport] Received non-string message, ignoring'
    );

    warnSpy.mockRestore();
  });

  it('onMessage_StringData_PassesThrough: string data is forwarded', async () => {
    const { createWebSocketTransport } = await import('./websocketTransport');

    const transport = createWebSocketTransport('ws://localhost');

    const messages: string[] = [];
    transport.onmessage = (event: { data: string }) => {
      messages.push(event.data);
    };

    // Simulate a string message
    mockSocketInstance.onmessage({ data: '{"type":"ping"}' });

    expect(messages).toEqual(['{"type":"ping"}']);
  });
});
