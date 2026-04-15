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
  type: 'diff' | 'text_commit' | 'backspace' | 'clear';
  text?: string;
  count?: number;
}

// History entry
export interface HistoryEntry {
  id: string;
  text: string;
  timestamp: number;
}

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'preempted';

export type AppTab = 'input' | 'scan' | 'history';
