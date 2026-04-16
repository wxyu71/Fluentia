import { useState, useRef, useCallback, useEffect } from 'react';
import { CryptoService } from '../utils/crypto';
import type { WsMessage, ConnectionState, InputCommand, ConnectionInfo } from '../types';
import { PROTOCOL_VERSION } from '../types';

const RECONNECT_DELAY = 3000;
const MAX_RECONNECT_ATTEMPTS = 300; // ~15 minutes at 3s intervals

interface UseWebSocketReturn {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
  connect: (info: ConnectionInfo) => void;
  disconnect: () => void;
  sendEncrypted: (cmd: InputCommand) => void;
  lastError: string | null;
  onPcCommand: React.MutableRefObject<((cmd: { type: string }) => void) | null>;
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
  const connectionStateRef = useRef<ConnectionState>('disconnected');
  const pcCommandRef = useRef<((cmd: { type: string }) => void) | null>(null);

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
    connectionStateRef.current = 'connecting';
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
        version: PROTOCOL_VERSION,
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
        connectionStateRef.current = 'disconnected';
        return;
      }
      // Auto-reconnect — use ref to avoid stale closure over connectionState
      if (
        connInfoRef.current &&
        connectionStateRef.current !== 'preempted' &&
        reconnectAttemptRef.current < MAX_RECONNECT_ATTEMPTS
      ) {
        reconnectAttemptRef.current++;
        setConnectionState('connecting');
        connectionStateRef.current = 'connecting';
        reconnectTimerRef.current = window.setTimeout(() => {
          if (connInfoRef.current) {
            connectWs(connInfoRef.current);
          }
        }, RECONNECT_DELAY);
      } else {
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
      }
    };

    ws.onerror = () => {
      setLastError('Connection error');
    };
  }, [deviceId, cleanup]);

  const handleMessage = useCallback((msg: WsMessage) => {
    switch (msg.type) {
      case 'joined':
        setConnectionState('connected');
        connectionStateRef.current = 'connected';
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

        // Initialize symmetric ratchet for forward secrecy
        try {
          const { seed } = cryptoRef.current.initRatchet();
          const initCmd = JSON.stringify({ type: 'ratchet_init', seed });
          const { payload: initPayload, nonce: initNonce } = cryptoRef.current.encrypt(initCmd);
          if (wsRef.current?.readyState === WebSocket.OPEN) {
            const initMsg: WsMessage = { type: 'encrypted', payload: initPayload, nonce: initNonce };
            wsRef.current.send(JSON.stringify(initMsg));
          }
        } catch (err) {
          console.error('Failed to init ratchet:', err);
        }
        break;

      case 'encrypted': {
        // Handle PC → mobile encrypted messages
        if (msg.payload && msg.nonce) {
          try {
            let plaintext: string;
            if (msg.seq !== undefined && cryptoRef.current.isRecvRatchetReady()) {
              plaintext = cryptoRef.current.decryptRatcheted(msg.payload, msg.nonce, msg.seq);
            } else {
              plaintext = cryptoRef.current.decrypt(msg.payload, msg.nonce);
            }
            const parsed = JSON.parse(plaintext);
            if (parsed.type === 'pc_ratchet_init' && parsed.seed) {
              cryptoRef.current.initRecvRatchet(parsed.seed);
            } else if (pcCommandRef.current) {
              pcCommandRef.current(parsed);
            }
          } catch {
            // Non-parseable encrypted messages are ignored
          }
        }
        break;
      }

      case 'preempted':
        setConnectionState('preempted');
        connectionStateRef.current = 'preempted';
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
    connectionStateRef.current = 'disconnected';
    setLastError(null);
  }, [cleanup]);

  const sendEncrypted = useCallback((cmd: InputCommand) => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
    if (!cryptoRef.current.isReady()) return;

    try {
      const plaintext = JSON.stringify(cmd);
      let msg: WsMessage;

      if (cryptoRef.current.isRatchetReady()) {
        // Forward-secret ratcheted encryption
        const { payload, nonce, seq } = cryptoRef.current.encryptRatcheted(plaintext);
        msg = { type: 'encrypted', payload, nonce, seq };
      } else {
        // Fallback: legacy crypto_box
        const { payload, nonce } = cryptoRef.current.encrypt(plaintext);
        msg = { type: 'encrypted', payload, nonce };
      }

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
    onPcCommand: pcCommandRef,
  };
}
