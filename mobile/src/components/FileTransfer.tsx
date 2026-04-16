import React, { useRef, useState, useCallback } from 'react';
import { PhotoIcon, FileIcon, UploadIcon } from './Icons';
import type { InputCommand } from '../types';

const CHUNK_SIZE = 32 * 1024; // 32 KB per chunk (safe for WebSocket frames after base64+encryption overhead)
const MAX_FILE_MB = 20;        // match server MAX_FILE_MB default

interface FileTransferProps {
  encryptionReady: boolean;
  onSendCommand: (cmd: InputCommand) => void;
}

function generateTransferId(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

export const FileTransfer: React.FC<FileTransferProps> = ({ encryptionReady, onSendCommand }) => {
  const [progress, setProgress] = useState<{ name: string; sent: number; total: number } | null>(null);
  const [status, setStatus] = useState<string>('');
  const [error, setError] = useState<string>('');
  const fileInputRef = useRef<HTMLInputElement>(null);
  const abortRef = useRef(false);

  const sendFile = useCallback(async (file: File) => {
    setError('');
    setStatus('');

    if (file.size > MAX_FILE_MB * 1024 * 1024) {
      setError(`File too large (max ${MAX_FILE_MB} MB)`);
      return;
    }

    abortRef.current = false;
    const transferId = generateTransferId();
    const totalChunks = Math.ceil(file.size / CHUNK_SIZE);

    // Send header — all fields are encrypted before leaving this device
    onSendCommand({
      type: 'file_start',
      transferId,
      fileName: file.name,
      fileSize: file.size,
      mimeType: file.type || 'application/octet-stream',
    });

    setProgress({ name: file.name, sent: 0, total: file.size });

    const arrayBuffer = await file.arrayBuffer();
    const bytes = new Uint8Array(arrayBuffer);

    for (let i = 0; i < totalChunks; i++) {
      if (abortRef.current) {
        onSendCommand({ type: 'file_abort', transferId });
        setProgress(null);
        setStatus('Transfer cancelled');
        return;
      }

      const start = i * CHUNK_SIZE;
      const end = Math.min(start + CHUNK_SIZE, file.size);
      const chunk = bytes.slice(start, end);

      // Encode chunk to base64 — stays encrypted in transit (via ratchet in sendEncrypted)
      const b64 = btoa(String.fromCharCode(...chunk));

      onSendCommand({
        type: 'file_chunk',
        transferId,
        chunkIndex: i,
        chunkData: b64,
        isLast: i === totalChunks - 1,
      });

      setProgress({ name: file.name, sent: end, total: file.size });

      // Yield to event loop every 4 chunks to keep UI responsive
      if (i % 4 === 3) await new Promise(r => setTimeout(r, 0));
    }

    setProgress(null);
    setStatus(`Sent: ${file.name}`);
  }, [onSendCommand]);

  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) sendFile(file);
    // Reset input so same file can be re-selected
    e.target.value = '';
  }, [sendFile]);

  const percent = progress
    ? Math.round((progress.sent / progress.total) * 100)
    : 0;

  return (
    <div style={{ marginTop: 8 }}>
      {/* Hidden file input */}
      <input
        ref={fileInputRef}
        type="file"
        accept="image/*,*/*"
        style={{ display: 'none' }}
        onChange={handleFileChange}
      />

      {/* Button row */}
      <div style={{ display: 'flex', gap: 8 }}>
        <button
          className="glass-btn"
          style={{ flex: 1, fontSize: 13, padding: '10px 8px', opacity: encryptionReady ? 1 : 0.45 }}
          disabled={!encryptionReady || !!progress}
          onClick={() => {
            if (fileInputRef.current) {
              fileInputRef.current.accept = 'image/*';
              fileInputRef.current.click();
            }
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
            <PhotoIcon size={15} color="var(--accent)" />
            Photo
          </span>
        </button>

        <button
          className="glass-btn"
          style={{ flex: 1, fontSize: 13, padding: '10px 8px', opacity: encryptionReady ? 1 : 0.45 }}
          disabled={!encryptionReady || !!progress}
          onClick={() => {
            if (fileInputRef.current) {
              fileInputRef.current.accept = '*/*';
              fileInputRef.current.click();
            }
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
            <FileIcon size={15} color="var(--accent)" />
            File
          </span>
        </button>
      </div>

      {/* Progress bar */}
      {progress && (
        <div style={{ marginTop: 10 }}>
          <div style={{
            display: 'flex', justifyContent: 'space-between',
            fontSize: 12, color: 'var(--text-secondary)', marginBottom: 4,
          }}>
            <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '70%' }}>
              <UploadIcon size={11} color="var(--accent)" /> {progress.name}
            </span>
            <span>{percent}%</span>
          </div>
          <div style={{
            height: 4, borderRadius: 2,
            background: 'rgba(255,255,255,0.1)',
            overflow: 'hidden',
          }}>
            <div style={{
              height: '100%',
              width: `${percent}%`,
              background: 'var(--accent)',
              borderRadius: 2,
              transition: 'width 0.1s',
            }} />
          </div>
          <button
            style={{ marginTop: 6, background: 'none', border: 'none', color: 'var(--danger)', fontSize: 12, cursor: 'pointer' }}
            onClick={() => { abortRef.current = true; }}
          >
            Cancel
          </button>
        </div>
      )}

      {status && !progress && (
        <div style={{ marginTop: 6, fontSize: 12, color: 'var(--text-secondary)' }}>{status}</div>
      )}
      {error && (
        <div style={{ marginTop: 6, fontSize: 12, color: 'var(--danger)' }}>{error}</div>
      )}
    </div>
  );
};
