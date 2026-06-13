import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Mock WebSocket before importing the module
let mockSocketInstance: MockWebSocket | null = null;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
class MockWebSocket {
  onopen: ((ev: Event) => void) | null = null;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  onmessage: ((ev: any) => void) | null = null;
  onclose: ((ev: CloseEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  readyState = 1;
  url: string;

  constructor(url: string) {
    this.url = url;
    mockSocketInstance = this;
  }

  send = vi.fn();
  close = vi.fn();
}

describe('websocketTransport onMessage', () => {
  beforeEach(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
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

    const _transport = createWebSocketTransport('ws://localhost');

    // Simulate a binary message via the mock socket's onmessage
    const binaryEvent = { data: new ArrayBuffer(10) };
    mockSocketInstance!.onmessage!(binaryEvent);

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
    mockSocketInstance!.onmessage!({ data: '{"type":"ping"}' });

    expect(messages).toEqual(['{"type":"ping"}']);
  });
});
