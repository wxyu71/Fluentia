// Protocol version — must match server and Windows client
export const PROTOCOL_VERSION = '1.1.0';

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
  seq?: number;
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
  type: 'diff' | 'text_commit' | 'backspace' | 'clear' | 'ratchet_init' | 'clipboard'
      | 'file_start' | 'file_chunk' | 'file_abort';
  text?: string;
  count?: number;
  seed?: string;
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

// App settings persisted in localStorage
export interface AppSettings {
  autoSaveHistory: boolean;
}

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'preempted';

// Only 2 tabs: input (with integrated scan) and history
export type AppTab = 'input' | 'history';
