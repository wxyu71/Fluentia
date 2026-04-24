import React, { useRef, useState, useCallback, forwardRef, useImperativeHandle } from 'react';
import { AttachIcon } from './Icons';
import type { InputCommand, TransferBatchProgress } from '../types';

const CHUNK_SIZE = 16 * 1024;
const MAX_FILE_MB = 20;

interface FileTransferProps {
  encryptionReady: boolean;
  onSendCommand: (cmd: InputCommand) => void;
  compact?: boolean;
  onBatchStateChange?: (batch: TransferBatchProgress | null) => void;
}

export interface FileTransferHandle {
  open: () => void;
  togglePause: () => void;
  cancel: () => void;
}

function generateTransferId(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

export const FileTransfer = forwardRef<FileTransferHandle, FileTransferProps>(
  ({ encryptionReady, onSendCommand, compact = false, onBatchStateChange }, ref) => {
  const [batch, setBatch] = useState<TransferBatchProgress | null>(null);
  const [error, setError] = useState<string>('');
  const fileInputRef = useRef<HTMLInputElement>(null);
  const abortRef = useRef(false);
  const pausedRef = useRef(false);
  const resumeRef = useRef<(() => void) | null>(null);
  const clearTimerRef = useRef<number | null>(null);

  const updateBatch = useCallback((updater: TransferBatchProgress | null | ((prev: TransferBatchProgress | null) => TransferBatchProgress | null)) => {
    setBatch((prev) => {
      const next = typeof updater === 'function'
        ? (updater as (value: TransferBatchProgress | null) => TransferBatchProgress | null)(prev)
        : updater;
      onBatchStateChange?.(next);
      return next;
    });
  }, [onBatchStateChange]);

  const clearHideTimer = useCallback(() => {
    if (clearTimerRef.current !== null) {
      window.clearTimeout(clearTimerRef.current);
      clearTimerRef.current = null;
    }
  }, []);

  const scheduleBatchClear = useCallback(() => {
    clearHideTimer();
    clearTimerRef.current = window.setTimeout(() => {
      updateBatch(null);
    }, 2400);
  }, [clearHideTimer, updateBatch]);

  const resumeIfNeeded = useCallback(() => {
    pausedRef.current = false;
    const resume = resumeRef.current;
    resumeRef.current = null;
    resume?.();
  }, []);

  useImperativeHandle(ref, () => ({
    open: () => {
      if (fileInputRef.current) {
        fileInputRef.current.accept = 'image/*,*/*';
        fileInputRef.current.click();
      }
    },
    togglePause: () => {
      updateBatch((current) => {
        if (!current || (current.status !== 'active' && current.status !== 'paused')) {
          return current;
        }

        const nextStatus = current.status === 'paused' ? 'active' : 'paused';
        pausedRef.current = nextStatus === 'paused';
        if (!pausedRef.current) {
          resumeIfNeeded();
        }

        return {
          ...current,
          status: nextStatus,
          updatedAt: Date.now(),
        };
      });
    },
    cancel: () => {
      abortRef.current = true;
      resumeIfNeeded();
      updateBatch((current) => current ? {
        ...current,
        status: 'cancelled',
        updatedAt: Date.now(),
        files: current.files.map((file) =>
          file.status === 'completed'
            ? file
            : { ...file, status: 'cancelled', updatedAt: Date.now() }),
      } : current);
      scheduleBatchClear();
    },
  }));

  const waitWhilePaused = useCallback(async () => {
    while (pausedRef.current && !abortRef.current) {
      await new Promise<void>((resolve) => {
        resumeRef.current = resolve;
      });
    }
  }, []);

  const sendFiles = useCallback(async (files: File[]) => {
    setError('');
    clearHideTimer();

    const oversize = files.find((file) => file.size > MAX_FILE_MB * 1024 * 1024);
    if (oversize) {
      setError(`File too large (max ${MAX_FILE_MB} MB)`);
      updateBatch(null);
      return;
    }

    abortRef.current = false;
    pausedRef.current = false;

    const now = Date.now();
    const transferFiles = files.map((file) => ({
      id: generateTransferId(),
      name: file.name,
      transferredBytes: 0,
      totalBytes: file.size,
      status: 'queued' as const,
      startedAt: now,
      updatedAt: now,
    }));

    updateBatch({
      id: generateTransferId(),
      direction: 'upload',
      status: 'active',
      files: transferFiles,
      startedAt: now,
      updatedAt: now,
    });

    try {
      for (const [index, file] of files.entries()) {
        const transferId = transferFiles[index].id;
        const fileStartedAt = Date.now();

        updateBatch((current) => current ? {
          ...current,
          status: pausedRef.current ? 'paused' : 'active',
          updatedAt: Date.now(),
          files: current.files.map((item) =>
            item.id === transferId
              ? { ...item, status: 'active', startedAt: fileStartedAt, updatedAt: fileStartedAt }
              : item),
        } : current);

        onSendCommand({
          type: 'file_start',
          transferId,
          fileName: file.name,
          fileSize: file.size,
          mimeType: file.type || 'application/octet-stream',
        });

        const totalChunks = Math.ceil(file.size / CHUNK_SIZE);
        const arrayBuffer = await file.arrayBuffer();
        const bytes = new Uint8Array(arrayBuffer);

        if (file.size === 0) {
          onSendCommand({ type: 'file_chunk', transferId, chunkIndex: 0, chunkData: '', isLast: true });
          updateBatch((current) => current ? {
            ...current,
            updatedAt: Date.now(),
            files: current.files.map((item) =>
              item.id === transferId
                ? { ...item, status: 'completed', transferredBytes: 0, updatedAt: Date.now() }
                : item),
          } : current);
          continue;
        }

        for (let chunkIndex = 0; chunkIndex < totalChunks; chunkIndex += 1) {
          if (abortRef.current) {
            onSendCommand({ type: 'file_abort', transferId });
            updateBatch((current) => current ? {
              ...current,
              status: 'cancelled',
              updatedAt: Date.now(),
              files: current.files.map((item) =>
                item.id === transferId
                  ? { ...item, status: 'cancelled', updatedAt: Date.now() }
                  : item),
            } : current);
            scheduleBatchClear();
            return;
          }

          await waitWhilePaused();

          const start = chunkIndex * CHUNK_SIZE;
          const end = Math.min(start + CHUNK_SIZE, file.size);
          const chunk = bytes.slice(start, end);
          const b64 = btoa(String.fromCharCode(...chunk));
          onSendCommand({ type: 'file_chunk', transferId, chunkIndex, chunkData: b64, isLast: chunkIndex === totalChunks - 1 });

          updateBatch((current) => current ? {
            ...current,
            status: pausedRef.current ? 'paused' : 'active',
            updatedAt: Date.now(),
            files: current.files.map((item) =>
              item.id === transferId
                ? {
                    ...item,
                    transferredBytes: end,
                    status: chunkIndex === totalChunks - 1 ? 'completed' : (pausedRef.current ? 'paused' : 'active'),
                    updatedAt: Date.now(),
                  }
                : item),
          } : current);

          if (chunkIndex % 2 === 1) {
            await new Promise((resolve) => setTimeout(resolve, 10));
          }
        }
      }

      updateBatch((current) => current ? {
        ...current,
        status: 'completed',
        updatedAt: Date.now(),
        files: current.files.map((item) => ({
          ...item,
          status: 'completed',
          transferredBytes: item.totalBytes,
          updatedAt: Date.now(),
        })),
      } : current);
      scheduleBatchClear();
    } catch {
      setError('Transfer failed');
      updateBatch((current) => current ? {
        ...current,
        status: 'failed',
        updatedAt: Date.now(),
        files: current.files.map((item) =>
          item.status === 'completed'
            ? item
            : { ...item, status: 'failed', updatedAt: Date.now() }),
      } : current);
      scheduleBatchClear();
    }
  }, [clearHideTimer, onSendCommand, scheduleBatchClear, updateBatch, waitWhilePaused]);

  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(e.target.files ?? []);
    if (files.length > 0) {
      void sendFiles(files);
    }
    e.target.value = '';
  }, [sendFiles]);

  const overallPercent = batch
    ? (() => {
        const total = batch.files.reduce((sum, file) => sum + file.totalBytes, 0);
        const transferred = batch.files.reduce((sum, file) => sum + file.transferredBytes, 0);
        return total > 0 ? Math.round((transferred / total) * 100) : 0;
      })()
    : 0;

  const transferBusy = !!batch && (batch.status === 'active' || batch.status === 'paused');

  if (compact) {
    return (
      <div style={{ position: 'relative' }}>
        <input ref={fileInputRef} type="file" accept="image/*,*/*" multiple style={{ display: 'none' }} onChange={handleFileChange} />
        <button
          onClick={() => fileInputRef.current?.click()}
          disabled={!encryptionReady || transferBusy}
          title="Send photo or file"
          style={{
            background: 'none', border: 'none',
            color: encryptionReady && !transferBusy ? 'var(--accent)' : 'var(--text-tertiary)',
            cursor: encryptionReady && !transferBusy ? 'pointer' : 'default',
            padding: '6px', display: 'flex', alignItems: 'center', gap: 6,
            fontSize: 13, fontWeight: 500, opacity: encryptionReady ? 1 : 0.4,
          }}
        >
          <AttachIcon size={16} color="var(--accent)" />
          {transferBusy ? `${overallPercent}%` : 'Attach'}
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
      <input ref={fileInputRef} type="file" accept="image/*,*/*" multiple style={{ display: 'none' }} onChange={handleFileChange} />
      {error && <div style={{ fontSize: 12, color: 'var(--danger)' }}>{error}</div>}
    </div>
  );
});