import React, { useRef, useState, useCallback, forwardRef, useImperativeHandle } from 'react';
import { AttachIcon } from './Icons';
import type { InputCommand } from '../types';

const CHUNK_SIZE = 32 * 1024;
const MAX_FILE_MB = 20;

interface FileTransferProps {
  encryptionReady: boolean;
  onSendCommand: (cmd: InputCommand) => void;
  compact?: boolean;
}

export interface FileTransferHandle {
  open: () => void;
}

function generateTransferId(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

export const FileTransfer = forwardRef<FileTransferHandle, FileTransferProps>(
  ({ encryptionReady, onSendCommand, compact = false }, ref) => {
  const [progress, setProgress] = useState<{ name: string; sent: number; total: number } | null>(null);
  const [status, setStatus] = useState<string>('');
  const [error, setError] = useState<string>('');
  const fileInputRef = useRef<HTMLInputElement>(null);
  const abortRef = useRef(false);

  useImperativeHandle(ref, () => ({
    open: () => {
      if (fileInputRef.current) {
        fileInputRef.current.accept = 'image/*,*/*';
        fileInputRef.current.click();
      }
    },
  }));

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
    onSendCommand({ type: 'file_start', transferId, fileName: file.name, fileSize: file.size, mimeType: file.type || 'application/octet-stream' });
    setProgress({ name: file.name, sent: 0, total: file.size });
    const arrayBuffer = await file.arrayBuffer();
    const bytes = new Uint8Array(arrayBuffer);
    for (let i = 0; i < totalChunks; i++) {
      if (abortRef.current) {
        onSendCommand({ type: 'file_abort', transferId });
        setProgress(null);
        setStatus('Cancelled');
        return;
      }
      const start = i * CHUNK_SIZE;
      const end = Math.min(start + CHUNK_SIZE, file.size);
      const chunk = bytes.slice(start, end);
      const b64 = btoa(String.fromCharCode(...chunk));
      onSendCommand({ type: 'file_chunk', transferId, chunkIndex: i, chunkData: b64, isLast: i === totalChunks - 1 });
      setProgress({ name: file.name, sent: end, total: file.size });
      if (i % 4 === 3) await new Promise(r => setTimeout(r, 0));
    }
    setProgress(null);
    setStatus(`Sent: ${file.name}`);
  }, [onSendCommand]);

  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) sendFile(file);
    e.target.value = '';
  }, [sendFile]);

  const percent = progress ? Math.round((progress.sent / progress.total) * 100) : 0;

  if (compact) {
    return (
      <div style={{ position: 'relative' }}>
        <input ref={fileInputRef} type="file" accept="image/*,*/*" style={{ display: 'none' }} onChange={handleFileChange} />
        <button
          onClick={() => fileInputRef.current?.click()}
          disabled={!encryptionReady || !!progress}
          title="Send photo or file"
          style={{
            background: 'none', border: 'none',
            color: encryptionReady && !progress ? 'var(--accent)' : 'var(--text-tertiary)',
            cursor: encryptionReady && !progress ? 'pointer' : 'default',
            padding: '6px', display: 'flex', alignItems: 'center', gap: 6,
            fontSize: 13, fontWeight: 500, opacity: encryptionReady ? 1 : 0.4,
          }}
        >
          <AttachIcon size={16} color="var(--accent)" />
          {progress ? `${percent}%` : 'Attach'}
        </button>
        {error && (
          <div style={{ position: 'absolute', bottom: '100%', right: 0, background: 'var(--danger)', color: '#fff', fontSize: 11, borderRadius: 6, padding: '4px 8px', whiteSpace: 'nowrap', zIndex: 100 }}>
            {error}
          </div>
        )}
      </div>
    );
  }

  return (
    <div style={{ marginTop: 8 }}>
      <input ref={fileInputRef} type="file" accept="image/*,*/*" style={{ display: 'none' }} onChange={handleFileChange} />
      {status && !progress && <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>{status}</div>}
      {error && <div style={{ fontSize: 12, color: 'var(--danger)' }}>{error}</div>}
    </div>
  );
});