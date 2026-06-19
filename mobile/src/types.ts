// Protocol version — must match server and Windows client
export const PROTOCOL_VERSION = '1.7.9';

// Protocol message types matching the Go server
export interface WsMessage {
  type: string;
  token?: string;
  deviceId?: string;
  role?: string;
  publicKey?: string;
  payload?: string;
  nonce?: string;
  error?: string;
  version?: string;
  min_version?: string;
  expiresAt?: string;
  seq?: number;
  // Device code auth fields
  deviceCode?: string;
  verifyId?: string;
  userAgent?: string;
  approved?: boolean;
}

// Parsed QR code data from PC client
export interface ConnectionInfo {
  s: string;  // server WebSocket URL
  t: string;  // room token
  k: string;  // PC's public key (base64)
}

// Text diff result
export interface TextDiff {
  backspace: number;
  insert: string;
}

// Encrypted inner message (after decryption)
export interface InputCommand {
  type: 'diff' | 'enter' | 'backspace' | 'clear' | 'ratchet_init' | 'pc_ratchet_init'
  | 'handshake_ack' | 'clipboard' | 'file_start' | 'file_chunk' | 'file_abort' | 'regex_config' | 'ble_auth' | 'ble_auth_ok';
  text?: string;
  count?: number;
  seed?: string;
  publicKey?: string;
  // file transfer fields
  fileName?: string;
  fileSize?: number;     // total bytes
  mimeType?: string;
  chunkIndex?: number;
  chunkData?: string;    // base64-encoded binary chunk
  isLast?: boolean;
  transferId?: string;   // random ID to correlate chunks to a transfer
}

// History entry
export interface HistoryEntry {
  id: string;
  text: string;
  timestamp: number;
}

export type TransferDirection = 'upload' | 'download';

export type TransferStatus = 'queued' | 'active' | 'paused' | 'completed' | 'failed' | 'cancelled';

export interface TransferFileProgress {
  id: string;
  name: string;
  transferredBytes: number;
  totalBytes: number;
  status: TransferStatus;
  startedAt: number;
  updatedAt: number;
}

export interface TransferBatchProgress {
  id: string;
  direction: TransferDirection;
  status: TransferStatus;
  files: TransferFileProgress[];
  startedAt: number;
  updatedAt: number;
}

// App settings persisted in localStorage
export interface AppSettings {
  autoSaveHistory: boolean;
  regexFilterEnabled: boolean;
  regexFilterMarkdown: string;
}

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'preempted';

// Only 2 tabs: input (with integrated scan) and history
export type AppTab = 'input' | 'history';
