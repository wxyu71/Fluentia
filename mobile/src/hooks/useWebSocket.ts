import { useState, useRef, useCallback, useEffect } from 'react';
import { CryptoService } from '../utils/crypto';
import type { WsMessage, ConnectionState, InputCommand, ConnectionInfo } from '../types';

const RECONNECT_DELAY = 3000;
const MAX_RECONNECT_ATTEMPTS = 5;

interface UseWebSocketReturn {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
  connect: (info: ConnectionInfo) => void;
  disconnect: () => void;
  sendEncrypted: (cmd: InputCommand) => void;
  lastError: string | null;
}

export function useWebSocket(deviceId: string): UseWebSocketReturn {
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const [peerConnected, setPeerConnected] = useState(false);
  const [encryptionReady, setEncryptionReady] = useState(false);
  const [lastError, setLastError] = useState<string | null>(null);

  const wsRef = useRef<WebSocket | null>(null);
  const cryptoRef = useRef<CryptoService>(new CryptoService());
  const connInfoRef = useRef<ConnectionInfo | null>(null);
  const reconnectAttemptRef = useRef(0);
  const reconnectTimerRef = useRef<number | null>(null);
  const intentionalCloseRef = useRef(false);

  const cleanup = useCallback(() => {
    if (reconnectTimerRef.current !== null) {
      clearTimeout(reconnectTimerRef.current);
      reconnectTimerRef.current = null;
    }
    if (wsRef.current) {
      intentionalCloseRef.current = true;
      wsRef.current.close();
      wsRef.current = null;
    }
    setPeerConnected(false);
    setEncryptionReady(false);
  }, []);

  const connectWs = useCallback((info: ConnectionInfo) => {
    cleanup();
    intentionalCloseRef.current = false;
    connInfoRef.current = info;

    // Reset crypto for new session
    cryptoRef.current.reset();
    cryptoRef.current.setPeerPublicKey(info.k);

    setConnectionState('connecting');
    setLastError(null);

    const ws = new WebSocket(info.s);
    wsRef.current = ws;

    ws.onopen = () => {
      reconnectAttemptRef.current = 0;
      // Join the room
      const joinMsg: WsMessage = {
        type: 'join_room',
        token: info.t,
        deviceId: deviceId,
      };
      ws.send(JSON.stringify(joinMsg));
    };

    ws.onmessage = (event) => {
      try {
        const msg: WsMessage = JSON.parse(event.data);
        handleMessage(msg);
      } catch {
        console.error('Failed to parse message');
      }
    };

    ws.onclose = () => {
      if (intentionalCloseRef.current) {
        setConnectionState('disconnected');
        return;
      }
      // Auto-reconnect
      if (
        connInfoRef.current &&
        connectionState !== 'preempted' &&
        reconnectAttemptRef.current < MAX_RECONNECT_ATTEMPTS
      ) {
        reconnectAttemptRef.current++;
        setConnectionState('connecting');
        reconnectTimerRef.current = window.setTimeout(() => {
          if (connInfoRef.current) {
            connectWs(connInfoRef.current);
          }
        }, RECONNECT_DELAY);
      } else {
        setConnectionState('disconnected');
      }
    };

    ws.onerror = () => {
      setLastError('Connection error');
    };
  }, [deviceId, cleanup, connectionState]);

  const handleMessage = useCallback((msg: WsMessage) => {
    switch (msg.type) {
      case 'joined':
        setConnectionState('connected');
        // PC created the room so it's already present — mark peer as connected
        setPeerConnected(true);
        // Send our public key to the PC
        if (wsRef.current?.readyState === WebSocket.OPEN) {
          const keyMsg: WsMessage = {
            type: 'key_exchange',
            publicKey: cryptoRef.current.getPublicKeyBase64(),
          };
          wsRef.current.send(JSON.stringify(keyMsg));
        }
        break;

      case 'peer_joined':
        setPeerConnected(true);
        break;

      case 'peer_left':
        setPeerConnected(false);
        setEncryptionReady(false);
        break;

      case 'key_exchange':
        // PC confirms key exchange by sending its key (redundant but confirms)
        if (msg.publicKey) {
          // We already have PC's key from QR code, but update if provided
          cryptoRef.current.setPeerPublicKey(msg.publicKey);
        }
        setEncryptionReady(true);
        break;

      case 'preempted':
        setConnectionState('preempted');
        setLastError(msg.error || 'Another device connected');
        intentionalCloseRef.current = true;
        break;

      case 'error':
        setLastError(msg.error || 'Unknown error');
        break;
    }
  }, []);

  const disconnect = useCallback(() => {
    intentionalCloseRef.current = true;
    cleanup();
    connInfoRef.current = null;
    setConnectionState('disconnected');
    setLastError(null);
  }, [cleanup]);

  const sendEncrypted = useCallback((cmd: InputCommand) => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
    if (!cryptoRef.current.isReady()) return;

    try {
      const plaintext = JSON.stringify(cmd);
      const { payload, nonce } = cryptoRef.current.encrypt(plaintext);
      const msg: WsMessage = {
        type: 'encrypted',
        payload,
        nonce,
      };
      wsRef.current.send(JSON.stringify(msg));
    } catch (err) {
      console.error('Encryption error:', err);
    }
  }, []);

  useEffect(() => {
    return () => cleanup();
  }, [cleanup]);

  return {
    connectionState,
    peerConnected,
    encryptionReady,
    connect: connectWs,
    disconnect,
    sendEncrypted,
    lastError,
  };
}
