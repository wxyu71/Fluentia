import { useState, useRef, useCallback, useEffect } from 'react';
import { TRANSPORT_READY_STATE, type TransportConnection } from '../services/transport';
import { createWebSocketTransport } from '../services/websocketTransport';
import { CryptoService, type PersistedCryptoSession } from '../utils/crypto';
import { TransportHealthMonitor, type MessageType } from '../utils/transportHealth';
import type {
  WsMessage,
  ConnectionState,
  InputCommand,
  ConnectionInfo,
  TransferBatchProgress,
} from '../types';
import { PROTOCOL_VERSION } from '../types';

const STORED_CONN_KEY = 'fluentia_conn';
// SECURITY NOTE: The crypto session (NaCl keypair + ratchet state) is stored in
// localStorage for session resumption across page reloads. Both localStorage and
// IndexedDB are equally accessible to any JS on the same origin, so migrating to
// IndexedDB would not meaningfully improve XSS resistance. True protection requires
// Web Crypto non-extractable keys, but Curve25519 is not supported by Web Crypto.
// The session is short-lived (cleared on disconnect/timeout) and the secret key is
// never transmitted over the network.
const STORED_CRYPTO_KEY = 'fluentia_crypto_session_v1';
const FIXED_RECONNECT_DELAY_MS = 2000;
const MAX_RECONNECT_ATTEMPTS = 30;
const CONNECT_TIMEOUT_MS = 8000;
const HANDSHAKE_TIMEOUT_MS = 12000;
const HEARTBEAT_INTERVAL_MS = 3000;
const HEARTBEAT_TIMEOUT_MS = 2500;
const OFFLINE_GRACE_MS = 10000;

function validateInputCommand(data: any): data is InputCommand {
  return (
    typeof data === 'object' &&
    data !== null &&
    typeof data.type === 'string'
  );
}

function sanitizeFileName(fileName: string): string {
  return fileName.replace(/[\/\\]/g, '_').replace(/^\.+/, '');
}

function buildSessionKey(info: ConnectionInfo): string {
  return `${info.s}|${info.t}|${info.k}`;
}

function loadPersistedCrypto(info: ConnectionInfo): PersistedCryptoSession | null {
  try {
    const raw = localStorage.getItem(STORED_CRYPTO_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as { sessionKey?: string; crypto?: PersistedCryptoSession };
    if (parsed.sessionKey !== buildSessionKey(info) || !parsed.crypto) {
      return null;
    }

    return parsed.crypto;
  } catch {
    // Invalid or missing persisted crypto session.
    return null;
  }
}

interface UseWebSocketReturn {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
  connect: (info: ConnectionInfo) => void;
  disconnect: () => void;
  sendEncrypted: (cmd: InputCommand) => Promise<boolean>;
  lastError: string | null;
  pendingStatus: string | null;
  bufferedInputActive: boolean;
  queuedCommandCount: number;
  inputResetVersion: number;
  incomingTransferBatch: TransferBatchProgress | null;
}

export function useWebSocket(
  deviceId: string,
  sendViaBle?: (message: Pick<WsMessage, 'payload' | 'nonce' | 'seq'>) => boolean,
  onEncryptedCommand?: (cmd: InputCommand) => void,
  bleTransportReady?: boolean,
  healthMonitor?: TransportHealthMonitor,
  bleTransport?: { send(data: string): void; readyState: number } | null,
): UseWebSocketReturn {
  const [connectionState, setConnectionState] = useState<ConnectionState>('disconnected');
  const [peerConnected, setPeerConnected] = useState(false);
  const [encryptionReady, setEncryptionReady] = useState(false);
  const [lastError, setLastError] = useState<string | null>(null);
  const [pendingStatus, setPendingStatus] = useState<string | null>(null);
  const [bufferedInputActive, setBufferedInputActive] = useState(false);
  const [queuedCommandCount, setQueuedCommandCount] = useState(0);
  const [inputResetVersion, setInputResetVersion] = useState(0);
  const [incomingTransferBatch, setIncomingTransferBatch] = useState<TransferBatchProgress | null>(null);

  const wsRef = useRef<TransportConnection | null>(null);
  const cryptoRef = useRef<CryptoService>(new CryptoService());
  const connInfoRef = useRef<ConnectionInfo | null>(null);
  const reconnectAttemptRef = useRef(0);
  const reconnectTimerRef = useRef<number | null>(null);
  const handshakeTimerRef = useRef<number | null>(null);
  const connectTimeoutRef = useRef<number | null>(null);
  const heartbeatIntervalRef = useRef<number | null>(null);
  const heartbeatTimeoutRef = useRef<number | null>(null);
  const offlineGraceTimerRef = useRef<number | null>(null);
  const intentionalCloseRef = useRef(false);
  const suspendedRef = useRef(false);
  const handshakeStartedRef = useRef(false);
  const connectionStateRef = useRef<ConnectionState>('disconnected');
  const encryptionReadyRef = useRef(false);
  const hadEncryptedSessionRef = useRef(false);
  const bufferedInputActiveRef = useRef(false);
  const queuedCommandsRef = useRef<InputCommand[]>([]);
  const incomingFilesRef = useRef(new Map<string, { fileName: string; mimeType: string; chunks: string[] }>());
  const incomingTransferBatchRef = useRef<TransferBatchProgress | null>(null);
  const incomingTransferHideTimerRef = useRef<number | null>(null);
  const bleTransportReadyRef = useRef(false);

  // Stores latest versions of all callbacks so handleMessage and connectWs
  // can read via the ref instead of capturing closures, keeping their deps empty.
  const callbacksRef = useRef<{
    clearConnectTimeout: () => void;
    clearHeartbeatTimeout: () => void;
    clearHandshakeTimer: () => void;
    clearHeartbeatInterval: () => void;
    closeSocket: (reason: string) => void;
    persistCryptoState: () => void;
    setEncryptionState: (ready: boolean) => void;
    startConnectTimeout: () => void;
    startHandshakeTimeout: (message: string) => void;
    beginSecureHandshake: () => void;
    startHeartbeat: (ws: TransportConnection) => void;
    startOfflineGrace: (status?: string) => void;
    sendEncryptedPayload: (plaintext: string, messageType?: MessageType) => Promise<boolean>;
    flushQueuedCommands: () => void;
    cleanup: (clearConnectionInfo?: boolean) => void;
    setBufferedInputMode: (active: boolean) => void;
    decodeBase64Chunk: (chunkData: string) => Uint8Array;
    triggerFileDownload: (fileName: string, mimeType: string, chunks: string[]) => void;
    updateIncomingTransferBatch: (updater: TransferBatchProgress | null | ((prev: TransferBatchProgress | null) => TransferBatchProgress | null)) => void;
    clearIncomingTransferHideTimer: () => void;
    scheduleIncomingTransferHide: () => void;
    handleMessage: (msg: WsMessage) => void;
    onEncryptedCommand?: (cmd: InputCommand) => void;
    deviceId: string;
  }>({} as any);

  useEffect(() => {
    bleTransportReadyRef.current = bleTransportReady ?? false;
  }, [bleTransportReady]);

  const persistCryptoState = useCallback(() => {
    const info = connInfoRef.current;
    if (!info) {
      return;
    }

    try {
      localStorage.setItem(STORED_CRYPTO_KEY, JSON.stringify({
        sessionKey: buildSessionKey(info),
        crypto: cryptoRef.current.exportSession(),
      }));
    } catch {
      // Ignore storage failures.
    }
  }, []);

  const updateQueuedCommandCount = useCallback(() => {
    setQueuedCommandCount(queuedCommandsRef.current.length);
  }, []);

  const clearOfflineGraceTimer = useCallback(() => {
    if (offlineGraceTimerRef.current !== null) {
      clearTimeout(offlineGraceTimerRef.current);
      offlineGraceTimerRef.current = null;
    }
  }, []);

  const setBufferedInputMode = useCallback((active: boolean) => {
    bufferedInputActiveRef.current = active;
    setBufferedInputActive(active);
  }, []);

  const dropQueuedCommands = useCallback(() => {
    queuedCommandsRef.current = [];
    updateQueuedCommandCount();
  }, [updateQueuedCommandCount]);

  const clearIncomingTransferHideTimer = useCallback(() => {
    if (incomingTransferHideTimerRef.current !== null) {
      clearTimeout(incomingTransferHideTimerRef.current);
      incomingTransferHideTimerRef.current = null;
    }
  }, []);

  const updateIncomingTransferBatch = useCallback((updater: TransferBatchProgress | null | ((prev: TransferBatchProgress | null) => TransferBatchProgress | null)) => {
    setIncomingTransferBatch((prev) => {
      const next = typeof updater === 'function'
        ? (updater as (value: TransferBatchProgress | null) => TransferBatchProgress | null)(prev)
        : updater;
      incomingTransferBatchRef.current = next;
      return next;
    });
  }, []);

  const scheduleIncomingTransferHide = useCallback(() => {
    clearIncomingTransferHideTimer();
    incomingTransferHideTimerRef.current = window.setTimeout(() => {
      incomingTransferBatchRef.current = null;
      setIncomingTransferBatch(null);
    }, 2600);
  }, [clearIncomingTransferHideTimer]);

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
    anchor.download = sanitizeFileName(fileName) || 'fluentia-download';
    anchor.rel = 'noopener';
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => URL.revokeObjectURL(downloadUrl), 1500);
  }, [decodeBase64Chunk]);

  const setEncryptionState = useCallback((ready: boolean) => {
    encryptionReadyRef.current = ready;
    if (ready) {
      handshakeStartedRef.current = false;
      hadEncryptedSessionRef.current = true;
      clearOfflineGraceTimer();
      setBufferedInputMode(false);
    }
    setEncryptionReady(ready);
    if (ready) {
      setPendingStatus(null);
    }
  }, [clearOfflineGraceTimer, setBufferedInputMode]);

  const startOfflineGrace = useCallback((status = 'Network unstable, reconnecting...') => {
    if (!hadEncryptedSessionRef.current || connectionStateRef.current === 'preempted') {
      return;
    }

    clearOfflineGraceTimer();
    setBufferedInputMode(true);
    setPendingStatus(status);
    setConnectionState('connecting');
    connectionStateRef.current = 'connecting';

    offlineGraceTimerRef.current = window.setTimeout(() => {
      setBufferedInputMode(false);
      dropQueuedCommands();
      setPeerConnected(false);
      setEncryptionState(false);
      setPendingStatus(null);
      setLastError('Reconnection timed out. Buffered input was not sent.');
      setConnectionState('disconnected');
      connectionStateRef.current = 'disconnected';
    }, OFFLINE_GRACE_MS);
  }, [clearOfflineGraceTimer, dropQueuedCommands, setBufferedInputMode, setEncryptionState]);

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

  const clearConnectTimeout = useCallback(() => {
    if (connectTimeoutRef.current !== null) {
      clearTimeout(connectTimeoutRef.current);
      connectTimeoutRef.current = null;
    }
  }, []);

  const clearHeartbeatInterval = useCallback(() => {
    if (heartbeatIntervalRef.current !== null) {
      clearInterval(heartbeatIntervalRef.current);
      heartbeatIntervalRef.current = null;
    }
  }, []);

  const clearHeartbeatTimeout = useCallback(() => {
    if (heartbeatTimeoutRef.current !== null) {
      clearTimeout(heartbeatTimeoutRef.current);
      heartbeatTimeoutRef.current = null;
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

  const startHeartbeat = useCallback((ws: TransportConnection) => {
    const sendPing = () => {
      if (wsRef.current !== ws || ws.readyState !== TRANSPORT_READY_STATE.OPEN) {
        return;
      }

      clearHeartbeatTimeout();
      heartbeatTimeoutRef.current = window.setTimeout(() => {
        if (wsRef.current !== ws) {
          return;
        }

        if (bleTransportReadyRef.current) {
          setConnectionState('connecting');
          connectionStateRef.current = 'connecting';
          setPendingStatus('Reconnecting...');
        } else {
          setPeerConnected(false);
          setEncryptionState(false);
          setPendingStatus('Reconnecting');
          setConnectionState('connecting');
          connectionStateRef.current = 'connecting';
        }

        try {
          ws.close(1000, 'heartbeat-timeout');
        } catch {
          // Ignore close errors for stale sockets.
        }
      }, HEARTBEAT_TIMEOUT_MS);

      try {
        ws.send(JSON.stringify({ type: 'ping' } satisfies WsMessage));
      } catch {
        clearHeartbeatTimeout();
        try {
          ws.close(1000, 'ping-failed');
        } catch {
          // Ignore close errors for stale sockets.
        }
      }
    };

    clearHeartbeatInterval();
    clearHeartbeatTimeout();
    sendPing();
    heartbeatIntervalRef.current = window.setInterval(sendPing, HEARTBEAT_INTERVAL_MS);
  }, [clearHeartbeatInterval, clearHeartbeatTimeout, setEncryptionState]);

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

  const startConnectTimeout = useCallback(() => {
    clearConnectTimeout();
    connectTimeoutRef.current = window.setTimeout(() => {
      if (encryptionReadyRef.current || connectionStateRef.current === 'preempted') {
        return;
      }

      intentionalCloseRef.current = true;
      closeSocket('connect-timeout');
      setPeerConnected(false);
      setEncryptionState(false);
      setPendingStatus(null);
      setLastError('Connection failed. Check your network and try again.');
      setConnectionState('disconnected');
      connectionStateRef.current = 'disconnected';
    }, CONNECT_TIMEOUT_MS);
  }, [clearConnectTimeout, closeSocket, setEncryptionState]);

  const cleanup = useCallback((clearConnectionInfo = false) => {
    clearReconnectTimer();
    clearHandshakeTimer();
    clearConnectTimeout();
    clearOfflineGraceTimer();
    clearHeartbeatInterval();
    clearHeartbeatTimeout();
    clearIncomingTransferHideTimer();
    handshakeStartedRef.current = false;
    incomingFilesRef.current.clear();
    incomingTransferBatchRef.current = null;
    setIncomingTransferBatch(null);
    if (wsRef.current) {
      intentionalCloseRef.current = true;
      closeSocket('cleanup');
    }
    if (!bleTransportReadyRef.current) {
      setPeerConnected(false);
      setEncryptionState(false);
      hadEncryptedSessionRef.current = false;
    }
    setBufferedInputMode(false);
    dropQueuedCommands();
    setPendingStatus(null);
    if (clearConnectionInfo) {
      connInfoRef.current = null;
    }
  }, [clearConnectTimeout, clearHandshakeTimer, clearHeartbeatInterval, clearHeartbeatTimeout, clearIncomingTransferHideTimer, clearOfflineGraceTimer, clearReconnectTimer, closeSocket, dropQueuedCommands, setBufferedInputMode, setEncryptionState]);

  const sendEncryptedPayload = useCallback(async (plaintext: string, messageType: MessageType = 'control') => {
    const ws = wsRef.current;
    if (!cryptoRef.current.isReady()) return false;

    try {
      // Encrypt once — same ciphertext goes to whichever transport we pick
      let msg: WsMessage;
      if (cryptoRef.current.isRatchetReady()) {
        const { payload, nonce, seq } = cryptoRef.current.encryptRatcheted(plaintext);
        msg = { type: 'encrypted', payload, nonce, seq };
      } else {
        const { payload, nonce } = cryptoRef.current.encrypt(plaintext);
        msg = { type: 'encrypted', payload, nonce };
      }

      // Use health monitor to select transport if available
      if (healthMonitor) {
        const selected = healthMonitor.selectTransport(messageType);
        const serialized = JSON.stringify(msg);

        if (selected === 'ble' && bleTransport && bleTransport.readyState === TRANSPORT_READY_STATE.OPEN) {
          bleTransport.send(serialized);
          return true;
        }

        if (selected === 'ws' && ws && ws.readyState === TRANSPORT_READY_STATE.OPEN) {
          ws.send(serialized);
          return true;
        }

        // Fallback: try the other transport
        if (selected === 'ble' && ws && ws.readyState === TRANSPORT_READY_STATE.OPEN) {
          ws.send(serialized);
          return true;
        }
        if (selected === 'ws' && bleTransport && bleTransport.readyState === TRANSPORT_READY_STATE.OPEN) {
          bleTransport.send(serialized);
          return true;
        }

        return false;
      }

      // Legacy path: no health monitor, WS-first fallback
      if (ws && ws.readyState === TRANSPORT_READY_STATE.OPEN) {
        ws.send(JSON.stringify(msg));
        return true;
      }

      if (sendViaBle && sendViaBle({ payload: msg.payload, nonce: msg.nonce, seq: msg.seq })) {
        return true;
      }

      return false;
    } catch {
      // Encryption or send failed — return false to signal transport failure.
      return false;
    }
  }, [sendViaBle, healthMonitor, bleTransport]);

  const flushQueuedCommands = useCallback(() => {
    if (!encryptionReadyRef.current) {
      return;
    }

    const remaining: InputCommand[] = [];
    for (const command of queuedCommandsRef.current) {
      const sent = sendEncryptedPayload(JSON.stringify(command), 'input');
      if (!sent) {
        remaining.push(command);
      }
    }

    queuedCommandsRef.current = remaining;
    updateQueuedCommandCount();
  }, [sendEncryptedPayload, updateQueuedCommandCount]);

  const beginSecureHandshake = useCallback(() => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== TRANSPORT_READY_STATE.OPEN) return;
    if (!cryptoRef.current.isReady()) return;
    if (handshakeStartedRef.current) return;

    handshakeStartedRef.current = true;
    setPendingStatus('Securing session');
    startHandshakeTimeout('Secure pairing timed out. Go back and reconnect.');

    try {
      const { seed } = cryptoRef.current.initRatchet();
      persistCryptoState();
      const initCmd = JSON.stringify({ type: 'ratchet_init', seed });
      const { payload, nonce } = cryptoRef.current.encrypt(initCmd);
      ws.send(JSON.stringify({ type: 'encrypted', payload, nonce } satisfies WsMessage));
    } catch {
      // Handshake initiation failed — reset flag so it can be retried.
      handshakeStartedRef.current = false;
    }
  }, [persistCryptoState, startHandshakeTimeout]);

  const handleMessage = useCallback((msg: WsMessage) => {
    const {
      clearHeartbeatTimeout,
      clearConnectTimeout,
      closeSocket,
      persistCryptoState,
      clearHandshakeTimer,
      setEncryptionState,
      startHandshakeTimeout,
      beginSecureHandshake,
      startConnectTimeout,
      sendEncryptedPayload,
      flushQueuedCommands,
      decodeBase64Chunk,
      clearIncomingTransferHideTimer,
      updateIncomingTransferBatch,
      triggerFileDownload,
      scheduleIncomingTransferHide,
      onEncryptedCommand,
    } = callbacksRef.current;

    switch (msg.type) {
      case 'joined':
        clearConnectTimeout();
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
        setLastError(null);
        setPeerConnected(bleTransportReadyRef.current);
        if (msg.publicKey && !cryptoRef.current.hasPeerKey()) {
          cryptoRef.current.setPeerPublicKey(msg.publicKey);
          persistCryptoState();
        }
        if (msg.publicKey && connInfoRef.current && connInfoRef.current.k !== msg.publicKey) {
          connInfoRef.current = { ...connInfoRef.current, k: msg.publicKey };
          localStorage.setItem(STORED_CONN_KEY, JSON.stringify(connInfoRef.current));
          persistCryptoState();
        }
        setPendingStatus('Waiting for your PC');
        break;

      case 'pong':
        break;

      case 'peer_joined':
        clearConnectTimeout();
        setPeerConnected(true);
        if (msg.role === 'pc' && wsRef.current?.readyState === TRANSPORT_READY_STATE.OPEN) {
          if (bleTransportReadyRef.current) {
            // BLE is active — preserve ratchet state so BLE messages stay in sync.
            // Skip key exchange entirely; the existing ratchet handles both WS and BLE.
            break;
          }
          setEncryptionState(false);
          setPendingStatus('Key exchange');
          handshakeStartedRef.current = false;
          startHandshakeTimeout('Secure pairing timed out. Go back and reconnect.');
          cryptoRef.current.resetPeerState();
          if (connInfoRef.current) {
            cryptoRef.current.setPeerPublicKey(connInfoRef.current.k);
            persistCryptoState();
          }
          wsRef.current.send(JSON.stringify({
            type: 'key_exchange',
            publicKey: cryptoRef.current.getPublicKeyBase64(),
          } satisfies WsMessage));
          beginSecureHandshake();
        }
        break;

      case 'peer_left':
        handshakeStartedRef.current = false;
        clearHandshakeTimer();
        if (msg.role === 'pc' && msg.error !== 'temporary') {
          intentionalCloseRef.current = true;
          connInfoRef.current = null;
          setPendingStatus(null);
          setPeerConnected(false);
          setEncryptionState(false);
          setConnectionState('disconnected');
          connectionStateRef.current = 'disconnected';
        } else if (msg.role === 'pc') {
          if (!bleTransportReadyRef.current) {
            setPeerConnected(false);
            setEncryptionState(false);
          }
          startConnectTimeout();
          setConnectionState('connecting');
          connectionStateRef.current = 'connecting';
          setPendingStatus('Waiting for your PC');
        }
        break;

      case 'key_exchange':
        if (msg.publicKey && !cryptoRef.current.hasPeerKey()) {
          cryptoRef.current.setPeerPublicKey(msg.publicKey);
          persistCryptoState();
        }
        beginSecureHandshake();
        break;

      case 'encrypted': {
        if (!msg.payload || !msg.nonce) break;

        try {
          const plaintext = msg.seq !== undefined && cryptoRef.current.isRecvRatchetReady()
            ? cryptoRef.current.decryptRatcheted(msg.payload, msg.nonce, msg.seq)
            : cryptoRef.current.decrypt(msg.payload, msg.nonce);

          const parsed = JSON.parse(plaintext);
          if (!validateInputCommand(parsed)) break;
          if (parsed.type === 'pc_ratchet_init' && parsed.seed) {
            cryptoRef.current.initRecvRatchet(parsed.seed);
            persistCryptoState();
            setEncryptionState(true);
            clearHandshakeTimer();
            sendEncryptedPayload(JSON.stringify({ type: 'handshake_ack' } satisfies InputCommand), 'handshake');
            flushQueuedCommands();
          } else if (parsed.type === 'ble_auth_ok') {
            onEncryptedCommand?.(parsed);
          } else if (parsed.type === 'clear') {
            setInputResetVersion((version) => version + 1);
          } else if (parsed.type === 'file_start' && parsed.transferId) {
            const transferId = parsed.transferId;
            clearIncomingTransferHideTimer();
            incomingFilesRef.current.set(transferId, {
              fileName: parsed.fileName || 'fluentia-download',
              mimeType: parsed.mimeType || 'application/octet-stream',
              chunks: [],
            });

            updateIncomingTransferBatch((current) => {
              const next = current && current.status !== 'completed'
                ? current
                : {
                    id: `incoming-${Date.now().toString(36)}`,
                    direction: 'download' as const,
                    status: 'active' as const,
                    files: [],
                    startedAt: Date.now(),
                    updatedAt: Date.now(),
                  };

              const nextFiles: TransferBatchProgress['files'] = next.files.some((file) => file.id === transferId)
                ? next.files.map((file) => file.id === transferId
                  ? {
                      ...file,
                      name: parsed.fileName || file.name,
                      totalBytes: parsed.fileSize ?? file.totalBytes,
                      status: 'active' as const,
                      updatedAt: Date.now(),
                    }
                  : file)
                : [
                    ...next.files,
                    {
                      id: transferId,
                      name: parsed.fileName || 'fluentia-download',
                      transferredBytes: 0,
                      totalBytes: parsed.fileSize ?? 0,
                      status: 'active' as const,
                      startedAt: Date.now(),
                      updatedAt: Date.now(),
                    },
                  ];

              return {
                ...next,
                status: 'active' as const,
                files: nextFiles,
                updatedAt: Date.now(),
              };
            });
          } else if (parsed.type === 'file_chunk' && parsed.transferId) {
            const transferId = parsed.transferId;
            const transfer = incomingFilesRef.current.get(transferId) ?? {
              fileName: parsed.fileName || 'fluentia-download',
              mimeType: parsed.mimeType || 'application/octet-stream',
              chunks: [],
            };

            const chunkBytes = decodeBase64Chunk(parsed.chunkData || '').length;

            transfer.chunks[parsed.chunkIndex ?? transfer.chunks.length] = parsed.chunkData || '';
            incomingFilesRef.current.set(transferId, transfer);

            updateIncomingTransferBatch((current) => {
              if (!current) {
                return current;
              }

              const nextFiles: TransferBatchProgress['files'] = current.files.map((file) => file.id === transferId
                ? {
                    ...file,
                    transferredBytes: Math.min(
                      file.totalBytes > 0 ? file.totalBytes : file.transferredBytes + chunkBytes,
                      file.transferredBytes + chunkBytes,
                    ),
                    status: parsed.isLast ? ('completed' as const) : file.status,
                    updatedAt: Date.now(),
                  }
                : file);

              const nextStatus = nextFiles.every((file) => file.status === 'completed')
                ? ('completed' as const)
                : current.status;

              return {
                ...current,
                status: nextStatus,
                files: nextFiles,
                updatedAt: Date.now(),
              };
            });

            if (parsed.isLast) {
              triggerFileDownload(
                transfer.fileName,
                transfer.mimeType,
                transfer.chunks.filter((chunk): chunk is string => typeof chunk === 'string'),
              );
              incomingFilesRef.current.delete(transferId);
              scheduleIncomingTransferHide();
            }
          } else if (parsed.type === 'file_abort' && parsed.transferId) {
            const transferId = parsed.transferId;
            incomingFilesRef.current.delete(transferId);
            updateIncomingTransferBatch((current) => current ? {
              ...current,
              status: 'cancelled',
              updatedAt: Date.now(),
              files: current.files.map((file) =>
                file.id === transferId
                  ? { ...file, status: 'cancelled', updatedAt: Date.now() }
                  : file),
            } : current);
            scheduleIncomingTransferHide();
          }
        } catch {
          // Ignore malformed or stale encrypted payloads.
        }
        break;
      }

      case 'preempted':
        clearConnectTimeout();
        clearHandshakeTimer();
        setConnectionState('preempted');
        connectionStateRef.current = 'preempted';
        setLastError(msg.error || 'Another device connected');
        setPendingStatus(null);
        intentionalCloseRef.current = true;
        closeSocket('preempted');
        break;

      case 'error':
        clearConnectTimeout();
        clearHandshakeTimer();
        setLastError(msg.error || 'Unknown error');
        if (!encryptionReadyRef.current && msg.error === 'no peer connected') {
          setPeerConnected(false);
          setPendingStatus('Waiting for your PC');
          setConnectionState('connected');
          connectionStateRef.current = 'connected';
          return;
        }
        setPendingStatus(null);
        handshakeStartedRef.current = false;
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
  }, []);

  const connectWs = useCallback((info: ConnectionInfo) => {
    const {
      cleanup,
      clearConnectTimeout,
      clearHandshakeTimer,
      clearHeartbeatInterval,
      clearHeartbeatTimeout,
      deviceId,
      handleMessage,
      persistCryptoState,
      setBufferedInputMode,
      setEncryptionState,
      startConnectTimeout,
      startHeartbeat,
      startOfflineGrace,
    } = callbacksRef.current;

    const activeSocket = wsRef.current;
    const activeInfo = connInfoRef.current;
    if (
      activeSocket &&
      (activeSocket.readyState === TRANSPORT_READY_STATE.CONNECTING || activeSocket.readyState === TRANSPORT_READY_STATE.OPEN) &&
      activeInfo?.s === info.s &&
      activeInfo?.t === info.t &&
      activeInfo?.k === info.k
    ) {
      return;
    }

    cleanup();
    intentionalCloseRef.current = false;
    handshakeStartedRef.current = false;
    connInfoRef.current = info;

    if (!bleTransportReadyRef.current) {
      const persistedCrypto = loadPersistedCrypto(info);
      if (persistedCrypto) {
        cryptoRef.current.importSession(persistedCrypto);
      } else {
        cryptoRef.current.reset();
      }
      if (info.k) {
        cryptoRef.current.setPeerPublicKey(info.k);
      }
      persistCryptoState();
    }

    setConnectionState('connecting');
    connectionStateRef.current = 'connecting';
    if (!bleTransportReadyRef.current) {
      setPeerConnected(false);
      setEncryptionState(false);
    }
    setLastError(null);
    setPendingStatus(bleTransportReadyRef.current ? 'Reconnecting...' : 'Joining session');
    startConnectTimeout();

    const ws = createWebSocketTransport(info.s);
    wsRef.current = ws;

    ws.onopen = () => {
      suspendedRef.current = false;
      reconnectAttemptRef.current = 0;
      startHeartbeat(ws);
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
      clearConnectTimeout();
      clearHandshakeTimer();
      clearHeartbeatInterval();
      clearHeartbeatTimeout();

      if (intentionalCloseRef.current) {
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
        return;
      }

      if (document.visibilityState !== 'visible' || suspendedRef.current) {
        setBufferedInputMode(false);
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
        return;
      }

      if (bleTransportReadyRef.current) {
        // BLE transport is still active — keep encryption state intact,
        // just mark WS as reconnecting so the UI shows BLE-only mode.
        setLastError(null);
        setConnectionState('connecting');
        connectionStateRef.current = 'connecting';
        setPendingStatus('Reconnecting...');
      } else if (encryptionReadyRef.current || bufferedInputActiveRef.current) {
        startOfflineGrace();
      }

      if (
        connInfoRef.current &&
        connectionStateRef.current !== 'preempted' &&
        reconnectAttemptRef.current < MAX_RECONNECT_ATTEMPTS
      ) {
        reconnectAttemptRef.current += 1;
        setConnectionState('connecting');
        connectionStateRef.current = 'connecting';
        setPendingStatus('Reconnecting...');
        const backoffDelay = Math.min(FIXED_RECONNECT_DELAY_MS * Math.pow(2, reconnectAttemptRef.current - 1), 30000);
        reconnectTimerRef.current = window.setTimeout(() => {
          if (connInfoRef.current) {
            connectWs(connInfoRef.current);
          }
        }, backoffDelay);
      } else {
        setConnectionState('disconnected');
        connectionStateRef.current = 'disconnected';
      }
    };

    ws.onerror = () => {
      if (bleTransportReadyRef.current) {
        setConnectionState('connecting');
        connectionStateRef.current = 'connecting';
        setPendingStatus('Reconnecting...');
        return;
      }

      if (encryptionReadyRef.current || bufferedInputActiveRef.current) {
        startOfflineGrace();
      }

      if (encryptionReadyRef.current) {
        setPendingStatus('Reconnecting...');
        return;
      }

      setLastError('Connection failed. Check your network and try again.');
    };
  }, []);

  // Update ref every render so handleMessage and connectWs always see latest callbacks.
  callbacksRef.current = {
    clearConnectTimeout,
    clearHeartbeatTimeout,
    clearHandshakeTimer,
    clearHeartbeatInterval,
    closeSocket,
    persistCryptoState,
    setEncryptionState,
    startConnectTimeout,
    startHandshakeTimeout,
    beginSecureHandshake,
    startHeartbeat,
    startOfflineGrace,
    sendEncryptedPayload,
    flushQueuedCommands,
    cleanup,
    setBufferedInputMode,
    decodeBase64Chunk,
    triggerFileDownload,
    updateIncomingTransferBatch,
    clearIncomingTransferHideTimer,
    scheduleIncomingTransferHide,
    handleMessage,
    onEncryptedCommand,
    deviceId,
  };

  const disconnect = useCallback(() => {
    intentionalCloseRef.current = true;
    cleanup(true);
    setConnectionState('disconnected');
    connectionStateRef.current = 'disconnected';
    setLastError(null);
    // Clear persisted crypto session so secret key doesn't outlive the connection.
    try { localStorage.removeItem(STORED_CRYPTO_KEY); } catch { /* ignore */ }
  }, [cleanup]);

  const sendEncrypted = useCallback(async (cmd: InputCommand): Promise<boolean> => {
    const ws = wsRef.current;

    // Determine message type for transport routing
    let messageType: MessageType = 'input';
    if (cmd.type === 'file_start' || cmd.type === 'file_chunk' || cmd.type === 'file_abort') {
      messageType = 'file';
    } else if (cmd.type === 'ratchet_init' || cmd.type === 'pc_ratchet_init' || cmd.type === 'handshake_ack') {
      messageType = 'handshake';
    } else if (cmd.type === 'ble_auth' || cmd.type === 'ble_auth_ok' || cmd.type === 'clipboard') {
      messageType = 'control';
    }

    const sent = await sendEncryptedPayload(JSON.stringify(cmd), messageType);
    if (sent) {
      return true;
    }

    if (bleTransportReadyRef.current) {
      // BLE is active but send failed — just ensure WS is reconnecting.
      if ((!ws || ws.readyState !== TRANSPORT_READY_STATE.OPEN) && connInfoRef.current) {
        connectWs(connInfoRef.current);
      }
      return false;
    }

    if ((hadEncryptedSessionRef.current || bufferedInputActiveRef.current) && connectionStateRef.current !== 'preempted') {
      queuedCommandsRef.current.push(cmd);
      updateQueuedCommandCount();
      startOfflineGrace();
      if ((!ws || ws.readyState !== TRANSPORT_READY_STATE.OPEN) && connInfoRef.current) {
        connectWs(connInfoRef.current);
      }
      return false;
    }

    if (!sent && (!ws || ws.readyState !== TRANSPORT_READY_STATE.OPEN)) {
      setPeerConnected(false);
      setEncryptionState(false);
      setPendingStatus('Waiting for your PC');
      setConnectionState('connecting');
      connectionStateRef.current = 'connecting';
    }
    return false;
  }, [connectWs, sendEncryptedPayload, setEncryptionState, startOfflineGrace, updateQueuedCommandCount]);

  useEffect(() => {
    const reconnectIfNeeded = () => {
      if (!connInfoRef.current) return;
      if (intentionalCloseRef.current) return;
      if (connectionStateRef.current === 'preempted') return;

      const ws = wsRef.current;
      if (!ws || ws.readyState === TRANSPORT_READY_STATE.CLOSED || ws.readyState === TRANSPORT_READY_STATE.CLOSING) {
        reconnectAttemptRef.current = 0;
        connectWs(connInfoRef.current);
      }
    };

    const handleVisibility = () => {
      if (document.visibilityState !== 'visible') return;
      suspendedRef.current = false;
      reconnectIfNeeded();
    };

    const handlePageShow = () => {
      suspendedRef.current = false;
      reconnectIfNeeded();
    };

    const handleOnline = () => {
      suspendedRef.current = false;
      reconnectIfNeeded();
    };

    document.addEventListener('visibilitychange', handleVisibility);
    window.addEventListener('pageshow', handlePageShow);
    window.addEventListener('online', handleOnline);

    return () => {
      document.removeEventListener('visibilitychange', handleVisibility);
      window.removeEventListener('pageshow', handlePageShow);
      window.removeEventListener('online', handleOnline);
    };
  }, [connectWs]);

  useEffect(() => {
    const handlePageHide = () => {
      clearReconnectTimer();
      clearHandshakeTimer();
      clearConnectTimeout();
      clearOfflineGraceTimer();
      clearHeartbeatInterval();
      clearHeartbeatTimeout();
      suspendedRef.current = true;
      setPeerConnected(false);
      setEncryptionState(false);
      setPendingStatus(null);
      setConnectionState('disconnected');
      connectionStateRef.current = 'disconnected';
      if (!wsRef.current || wsRef.current.readyState !== TRANSPORT_READY_STATE.OPEN) return;
      closeSocket('pagehide');
    };

    window.addEventListener('pagehide', handlePageHide);
    return () => window.removeEventListener('pagehide', handlePageHide);
  }, [clearConnectTimeout, clearHandshakeTimer, clearHeartbeatInterval, clearHeartbeatTimeout, clearOfflineGraceTimer, clearReconnectTimer, closeSocket, setEncryptionState]);

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
    bufferedInputActive,
    queuedCommandCount,
    inputResetVersion,
    incomingTransferBatch,
  };
}
