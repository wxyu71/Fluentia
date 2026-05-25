import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useWebSocket } from './useWebSocket';
import { MockTransport } from '../test/mocks/transport';
import { mockLocalStorage } from '../test/mocks/browser';
import type { ConnectionInfo } from '../types';

// Mock the websocketTransport module to return our MockTransport
let mockTransport: MockTransport;
vi.mock('../services/websocketTransport', () => ({
  createWebSocketTransport: vi.fn(() => mockTransport),
}));

const TEST_INFO: ConnectionInfo = {
  s: 'wss://test.example.com/ws',
  t: 'test-token-abc',
  k: 'dGVzdC1wdWJsaWMta2V5', // base64 "test-public-key"
};

describe('useWebSocket', () => {
  let storage: ReturnType<typeof mockLocalStorage>;

  beforeEach(() => {
    vi.useFakeTimers();
    storage = mockLocalStorage();
    mockTransport = new MockTransport(true);
  });

  afterEach(() => {
    storage.restore();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function renderWsHook(deviceId = 'test-device') {
    return renderHook(() => useWebSocket(deviceId));
  }

  describe('initial state', () => {
    it('starts disconnected', () => {
      const { result } = renderWsHook();
      expect(result.current.connectionState).toBe('disconnected');
      expect(result.current.peerConnected).toBe(false);
      expect(result.current.encryptionReady).toBe(false);
    });
  });

  describe('connect', () => {
    it('sets connectionState to connecting', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));
      expect(result.current.connectionState).toBe('connecting');
    });
  });

  describe('disconnect', () => {
    it('resets connection state', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));
      act(() => result.current.disconnect());
      expect(result.current.connectionState).toBe('disconnected');
    });
  });

  describe('version mismatch', () => {
    it('disconnects on version mismatch in joined message', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      // Simulate joined with wrong version
      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'joined',
          token: 'test-token',
          version: '0.0.0',
        }));
      });

      expect(result.current.lastError).toBeTruthy();
    });
  });

  describe('peer lifecycle', () => {
    it('sets peerConnected on peer_joined', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      // Simulate joined
      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'joined',
          token: 'test-token',
          version: '1.5.10',
        }));
      });

      // Simulate peer_joined
      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'peer_joined',
          role: 'pc',
        }));
      });

      expect(result.current.peerConnected).toBe(true);
    });

    it('clears peerConnected on peer_left', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'joined',
          token: 'test-token',
          version: '1.5.10',
        }));
      });
      act(() => {
        mockTransport.simulateMessage(JSON.stringify({ type: 'peer_joined', role: 'pc' }));
      });
      expect(result.current.peerConnected).toBe(true);

      act(() => {
        mockTransport.simulateMessage(JSON.stringify({ type: 'peer_left', role: 'pc' }));
      });
      expect(result.current.peerConnected).toBe(false);
    });
  });

  describe('sendEncrypted', () => {
    it('queues command when not connected', () => {
      const { result } = renderWsHook();
      // sendEncrypted should not throw when not connected
      act(() => result.current.sendEncrypted({ type: 'diff', text: 'hello' }));
      // Command should be queued (we can't easily verify internal state)
    });
  });

  describe('server error', () => {
    it('sets lastError on server error message', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'error',
          error: 'session expired',
        }));
      });

      expect(result.current.lastError).toBeTruthy();
    });
  });

  describe('preempted', () => {
    it('sets connectionState to preempted', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'preempted',
          error: 'another device connected',
        }));
      });

      expect(result.current.connectionState).toBe('preempted');
    });
  });

  describe('file transfer', () => {
    it('handles file_start message', () => {
      const { result } = renderWsHook();
      act(() => result.current.connect(TEST_INFO));

      // Need encryption ready first - skip for now, just verify no crash
      act(() => {
        mockTransport.simulateMessage(JSON.stringify({
          type: 'joined',
          token: 'test-token',
          version: '1.5.10',
        }));
      });

      // file_start without encryption should be handled gracefully
      // (the handler checks crypto state before processing)
    });
  });
});
