import { useState, useRef, useCallback, useEffect } from 'react';
import { CryptoService } from '../utils/crypto';
import type { WsMessage, ConnectionState, InputCommand, ConnectionInfo } from '../types';
import { PROTOCOL_VERSION } from '../types';

const RECONNECT_DELAY = 1200;
const MAX_RECONNECT_ATTEMPTS = 300;
const HANDSHAKE_TIMEOUT_MS = 12000;

interface UseWebSocketReturn {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
  connect: (info: ConnectionInfo) => void;
  disconnect: () => void;
  sendEncrypted: (cmd: InputCommand) => void;
  lastError: string | null;
  pendingStatus: string | null;
  inputResetVersion: number;
}

export function useWebSocket(deviceId: string): UseWebSocketReturn {
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const [peerConnected, setPeerConnected] = useState(false);
  const [encryptionReady, setEncryptionReady] = useState(false);
  const [lastError, setLastError] = useState<string | null>(null);
  const [pendingStatus, setPendingStatus] = useState<string | null>(null);
  const [inputResetVersion, setInputResetVersion] = useState(0);

  const wsRef = useRef<WebSocket | null>(null);
  const cryptoRef = useRef<CryptoService>(new CryptoService());
  const connInfoRef = useRef<ConnectionInfo | null>(null);
  const reconnectAttemptRef = useRef(0);
  const reconnectTimerRef = useRef<number | null>(null);
  const handshakeTimerRef = useRef<number | null>(null);
  const intentionalCloseRef = useRef(false);
  const suspendedRef = useRef(false);
  const connectionStateRef = useRef<ConnectionState>('disconnected');
  const encryptionReadyRef = useRef(false);
  const incomingFilesRef = useRef(new Map<string, { fileName: string; mimeType: string; chunks: string[] }>());

  const decodeBase64Chunk = useCallback((chunkData: string) => {
    if (!chunkData) {
      return new Uint8Array();
    }

    const binary = window.atob(chunkData);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index);
    }

    return bytes;
  }, []);

  const triggerFileDownload = useCallback((fileName: string, mimeType: string, chunks: string[]) => {
    const blob = new Blob(chunks.map(decodeBase64Chunk), { type: mimeType || 'application/octet-stream' });
    const downloadUrl = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = downloadUrl;
    anchor.download = fileName || 'fluentia-download';
    anchor.rel = 'noopener';
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => URL.revokeObjectURL(downloadUrl), 1500);
  }, [decodeBase64Chunk]);

  const setEncryptionState = useCallback((ready: boolean) => {
    encryptionReadyRef.current = ready;
    setEncryptionReady(ready);
    if (ready) {
      setPendingStatus(null);
    }
  }, []);

  const clearReconnectTimer = useCallback(() => {
    if (reconnectTimerRef.current !== null) {
      clearTimeout(reconnectTimerRef.current);
      reconnectTimerRef.current = null;
    }
  }, []);

  const clearHandshakeTimer = useCallback(() => {
    if (handshakeTimerRef.current !== null) {
      clearTimeout(handshakeTimerRef.current);
      handshakeTimerRef.current = null;
    }
  }, []);

  const closeSocket = useCallback((reason: string) => {
    const ws = wsRef.current;
    wsRef.current = null;
    if (!ws) return;

    try {
      ws.close(1000, reason);
    } catch {
      // Ignore close errors for stale sockets.
    }
  }, []);

  const failHandshake = useCallback((message: string) => {
    if (encryptionReadyRef.current) return;

    intentionalCloseRef.current = true;
    clearReconnectTimer();
    clearHandshakeTimer();
    closeSocket('handshake-timeout');
    setPeerConnected(false);
    setEncryptionState(false);
    setPendingStatus(null);
    setLastError(message);
    setConnectionState('disconnected');
    connectionStateRef.current = 'disconnected';
  }, [clearHandshakeTimer, clearReconnectTimer, closeSocket, setEncryptionState]);

  const startHandshakeTimeout = useCallback((message: string) => {
    clearHandshakeTimer();
    handshakeTimerRef.current = window.setTimeout(() => {
      failHandshake(message);
    }, HANDSHAKE_TIMEOUT_MS);
  }, [clearHandshakeTimer, failHandshake]);

  const cleanup = useCallback((clearConnectionInfo = false) => {
    clearReconnectTimer();
    clearHandshakeTimer();
    incomingFilesRef.current.clear();
    if (wsRef.current) {
      intentionalCloseRef.current = true;
      closeSocket('cleanup');
    }
    setPeerConnected(false);
    setEncryptionState(false);
    setPendingStatus(null);
    if (clearConnectionInfo) {
      connInfoRef.current = null;
    }
  }, [clearHandshakeTimer, clearReconnectTimer, closeSocket, setEncryptionState]);

  const sendEncryptedPayload = useCallback((plaintext: string) => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    if (!cryptoRef.current.isReady()) return false;

    try {
      let msg: WsMessage;
      if (cryptoRef.current.isRatchetReady()) {
        const { payload, nonce, seq } = cryptoRef.current.encryptRatcheted(plaintext);
        msg = { type: 'encrypted', payload, nonce, seq };
      } else {
        const { payload, nonce } = cryptoRef.current.encrypt(plaintext);
        msg = { type: 'encrypted', payload, nonce };
      }
      ws.send(JSON.stringify(msg));
      return true;
    } catch (err) {
      console.error('Encryption error:', err);
      return false;
    }
  }, []);

  const handleMessage = useCallback((msg: WsMessage) => {
    switch (msg.type) {
      case 'joined':
        if (msg.version && msg.version !== PROTOCOL_VERSION) {
          setLastError(`Protocol mismatch: mobile ${PROTOCOL_VERSION}, server ${msg.version}. Please update.`);
          intentionalCloseRef.current = true;
          closeSocket('protocol-mismatch');
          setConnectionState('disconnected');
          connectionStateRef.current = 'disconnected';
          return;
        }

        setConnectionState('connected');
        connectionStateRef.current = 'connected';
        setPeerConnected(true);
        setPendingStatus('Key exchange');
        startHandshakeTimeout('Secure pairing timed out. Go back and reconnect.');

        if (wsRef.current?.readyState === WebSocket.OPEN) {
          wsRef.current.send(JSON.stringify({
            type: 'key_exchange',
            publicKey: cryptoRef.current.getPublicKeyBase64(),
          } satisfies WsMessage));
        }
        break;

      case 'peer_joined':
        setPeerConnected(true);
        if (msg.role === 'pc' && wsRef.current?.readyState === WebSocket.OPEN) {
          setEncryptionState(false);
          setPendingStatus('Key exchange');
          startHandshakeTimeout('Secure pairing timed out. Go back and reconnect.');
          cryptoRef.current.reset();
          if (connInfoRef.current) {
            cryptoRef.current.setPeerPublicKey(connInfoRef.current.k);
          }
          wsRef.current.send(JSON.stringify({
            type: 'key_exchange',
            publicKey: cryptoRef.current.getPublicKeyBase64(),
          } satisfies WsMessage));
        }
        break;

      case 'peer_left':
        setPeerConnected(false);
        setEncryptionState(false);
        clearHandshakeTimer();
        if (msg.role === 'pc' && msg.error !== 'temporary') {
          intentionalCloseRef.current = true;
          connInfoRef.current = null;
          setPendingStatus(null);
          setConnectionState('disconnected');
          connectionStateRef.current = 'disconnected';
        } else if (msg.role === 'pc') {
          setPendingStatus('Waiting for your PC');
        }
        break;

      case 'key_exchange':
        if (msg.publicKey && !cryptoRef.current.hasPeerKey()) {
          cryptoRef.current.setPeerPublicKey(msg.publicKey);
        }
        setPendingStatus('Securing session');
        startHandshakeTimeout('Secure pairing timed out. Go back and reconnect.');

        try {
          const { seed } = cryptoRef.current.initRatchet();
          const initCmd = JSON.stringify({ type: 'ratchet_init', seed });
          const { payload, nonce } = cryptoRef.current.encrypt(initCmd);
          if (wsRef.current?.readyState === WebSocket.OPEN) {
            wsRef.current.send(JSON.stringify({ type: 'encrypted', payload, nonce } satisfies WsMessage));
          }
        } catch (err) {
          console.error('Failed to initialize ratchet:', err);
        }
        break;

      case 'encrypted': {
        if (!msg.payload || !msg.nonce) break;

        try {
          const plaintext = msg.seq !== undefined && cryptoRef.current.isRecvRatchetReady()
            ? cryptoRef.current.decryptRatcheted(msg.payload, msg.nonce, msg.seq)
            : cryptoRef.current.decrypt(msg.payload, msg.nonce);

          const parsed = JSON.parse(plaintext) as InputCommand;
          if (parsed.type === 'pc_ratchet_init' && parsed.seed) {
            cryptoRef.current.initRecvRatchet(parsed.seed);
            setEncryptionState(true);
            clearHandshakeTimer();
            sendEncryptedPayload(JSON.stringify({ type: 'handshake_ack' } satisfies InputCommand));
          } else if (parsed.type === 'clear') {
            setInputResetVersion((version) => version + 1);
          } else if (parsed.type === 'file_start' && parsed.transferId) {
            incomingFilesRef.current.set(parsed.transferId, {
              fileName: parsed.fileName || 'fluentia-download',
              mimeType: parsed.mimeType || 'application/octet-stream',
              chunks: [],
            });
          } else if (parsed.type === 'file_chunk' && parsed.transferId) {
            const transfer = incomingFilesRef.current.get(parsed.transferId) ?? {
              fileName: parsed.fileName || 'fluentia-download',
              mimeType: parsed.mimeType || 'application/octet-stream',
              chunks: [],
            };

            transfer.chunks[parsed.chunkIndex ?? transfer.chunks.length] = parsed.chunkData || '';
            incomingFilesRef.current.set(parsed.transferId, transfer);

            if (parsed.isLast) {
              triggerFileDownload(
                transfer.fileName,
                transfer.mimeType,
                transfer.chunks.filter((chunk): chunk is string => typeof chunk === 'string'),
              );
              incomingFilesRef.current.delete(parsed.transferId);
            }
          } else if (parsed.type === 'file_abort' && parsed.transferId) {
            incomingFilesRef.current.delete(parsed.transferId);
          }
        } catch {
          // Ignore malformed or stale encrypted payloads.
        }
        break;
      }

      case 'preempted':
        clearHandshakeTimer();
        setConnectionState('preempted');
        connectionStateRef.current = 'preempted';
        setLastError(msg.error || 'Another device connected');
        setPendingStatus(null);
        intentionalCloseRef.current = true;
        closeSocket('preempted');
        break;

      case 'error':
        clearHandshakeTimer();
        setLastError(msg.error || 'Unknown error');
        setPendingStatus(null);
        if (!encryptionReadyRef.current) {
          intentionalCloseRef.current = true;
          closeSocket('server-error');
          setPeerConnected(false);
          setEncryptionState(false);
          setConnectionState('disconnected');
          connectionStateRef.current = 'disconnected';
        }
        break;
    }
  }, [clearHandshakeTimer, closeSocket, sendEncryptedPayload, setEncryptionState, startHandshakeTimeout, triggerFileDownload]);

  const connectWs = useCallback((info: ConnectionInfo) => {
    cleanup();
    intentionalCloseRef.current = false;
    connInfoRef.current = info;

    cryptoRef.current.reset();
    cryptoRef.current.setPeerPublicKey(info.k);

    setConnectionState('connecting');
    connectionStateRef.current = 'connecting';
    setPeerConnected(false);
    setEncryptionState(false);
    setLastError(null);
    setPendingStatus('Joining session');

    const ws = new WebSocket(info.s);
    wsRef.current = ws;

    ws.onopen = () => {
      suspendedRef.current = false;
      reconnectAttemptRef.current = 0;
      ws.send(JSON.stringify({
        type: 'join_session',
        token: info.t,
        deviceId,
        version: PROTOCOL_VERSION,
      } satisfies WsMessage));
    };

    ws.onmessage = (event) => {
      try {
        handleMessage(JSON.parse(event.data) as WsMessage);
      } catch {
        // Ignore malformed messages.
      }
    };

    ws.onclose = () => {
      if (wsRef.current !== ws) return;

      wsRef.current = null;
      clearHandshakeTimer();

      if (intentionalCloseRef.current) {
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
        return;
      }

      if (document.visibilityState !== 'visible' || suspendedRef.current) {
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
        return;
      }

      if (
        connInfoRef.current &&
        connectionStateRef.current !== 'preempted' &&
        reconnectAttemptRef.current < MAX_RECONNECT_ATTEMPTS
      ) {
        reconnectAttemptRef.current += 1;
        setConnectionState('connecting');
        connectionStateRef.current = 'connecting';
        setPendingStatus('Reconnecting');
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
      if (wsRef.current === ws) {
        setLastError('Connection error');
      }
    };
  }, [cleanup, clearHandshakeTimer, deviceId, handleMessage, setEncryptionState]);

  const disconnect = useCallback(() => {
    intentionalCloseRef.current = true;
    cleanup(true);
    setConnectionState('disconnected');
    connectionStateRef.current = 'disconnected';
    setLastError(null);
  }, [cleanup]);

  const sendEncrypted = useCallback((cmd: InputCommand) => {
    sendEncryptedPayload(JSON.stringify(cmd));
  }, [sendEncryptedPayload]);

  useEffect(() => {
    const reconnectIfNeeded = () => {
      if (!connInfoRef.current) return;
      if (intentionalCloseRef.current) return;
      if (connectionStateRef.current === 'preempted') return;

      const ws = wsRef.current;
      if (!ws || ws.readyState === WebSocket.CLOSED || ws.readyState === WebSocket.CLOSING) {
        reconnectAttemptRef.current = 0;
        connectWs(connInfoRef.current);
      }
    };

    const handleVisibility = () => {
      if (document.visibilityState !== 'visible') return;
      suspendedRef.current = false;
      reconnectIfNeeded();
    };

    const handleOnline = () => {
      suspendedRef.current = false;
      reconnectIfNeeded();
    };

    document.addEventListener('visibilitychange', handleVisibility);
    window.addEventListener('online', handleOnline);

    return () => {
      document.removeEventListener('visibilitychange', handleVisibility);
      window.removeEventListener('online', handleOnline);
    };
  }, [connectWs]);

  useEffect(() => {
    const handlePageHide = () => {
      clearReconnectTimer();
      clearHandshakeTimer();
      suspendedRef.current = true;
      if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
      closeSocket('pagehide');
    };

    window.addEventListener('pagehide', handlePageHide);
    return () => window.removeEventListener('pagehide', handlePageHide);
  }, [clearHandshakeTimer, clearReconnectTimer, closeSocket]);

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
    pendingStatus,
    inputResetVersion,
  };
}
