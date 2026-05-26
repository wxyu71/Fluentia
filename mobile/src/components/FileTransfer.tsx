import React, { useRef, useState, useCallback, useEffect, forwardRef, useImperativeHandle } from 'react';
import { AttachIcon } from './Icons';
import type { InputCommand, TransferBatchProgress } from '../types';

const CHUNK_SIZE = 64 * 1024;
const CHUNK_CONCURRENCY = 3;
const WORKER_POOL_SIZE = 2;

interface ChunkEncodeResponse {
  id: number;
  base64: string;
}

interface FileTransferProps {
  encryptionReady: boolean;
  onSendCommand: (cmd: InputCommand) => Promise<boolean>;
  compact?: boolean;
  maxFileMB?: number;
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
  ({ encryptionReady, onSendCommand, compact = false, maxFileMB = 0, onBatchStateChange }, ref) => {
  const [batch, setBatch] = useState<TransferBatchProgress | null>(null);
  const [error, setError] = useState<string>('');
  const fileInputRef = useRef<HTMLInputElement>(null);
  const abortRef = useRef(false);
  const pausedRef = useRef(false);
  const resumeRef = useRef<(() => void) | null>(null);
  const clearTimerRef = useRef<number | null>(null);
  const workersRef = useRef<Worker[]>([]);
  const pendingEncodesRef = useRef(new Map<number, (value: string) => void>());
  const nextWorkerRef = useRef(0);
  const nextEncodeIdRef = useRef(0);

  useEffect(() => {
    const workers = Array.from({ length: WORKER_POOL_SIZE }, () => new Worker(new URL('../workers/fileChunk.worker.ts', import.meta.url), { type: 'module' }));
    workers.forEach((worker) => {
      worker.onmessage = (event: MessageEvent<ChunkEncodeResponse>) => {
        const resolve = pendingEncodesRef.current.get(event.data.id);
        if (!resolve) {
          return;
        }

        pendingEncodesRef.current.delete(event.data.id);
        resolve(event.data.base64);
      };
    });

    workersRef.current = workers;

    const pending = pendingEncodesRef.current;
    return () => {
      pending.clear();
      workers.forEach((worker) => worker.terminate());
      workersRef.current = [];
    };
  }, []);

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
        fileInputRef.current.accept = '*/*';
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

  const encodeChunk = useCallback((chunk: Uint8Array) => {
    const workers = workersRef.current;
    if (workers.length === 0) {
      return Promise.resolve(btoa(String.fromCharCode(...chunk)));
    }

    const worker = workers[nextWorkerRef.current % workers.length];
    nextWorkerRef.current += 1;
    const id = nextEncodeIdRef.current++;

    return new Promise<string>((resolve) => {
      pendingEncodesRef.current.set(id, resolve);
      worker.postMessage({ id, bytes: chunk.buffer.slice(chunk.byteOffset, chunk.byteOffset + chunk.byteLength) }, [chunk.buffer.slice(chunk.byteOffset, chunk.byteOffset + chunk.byteLength)]);
    });
  }, []);

  const sendFiles = useCallback(async (files: File[]) => {
    setError('');
    clearHideTimer();

    const effectiveMaxFileMB = maxFileMB > 0 ? maxFileMB : Number.POSITIVE_INFINITY;
    const oversize = files.find((file) => file.size > effectiveMaxFileMB * 1024 * 1024);
    if (oversize) {
      setError(maxFileMB > 0 ? `File too large (max ${maxFileMB} MB)` : 'File too large');
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
        const chunkSize = CHUNK_SIZE;

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

        const totalChunks = Math.ceil(file.size / chunkSize);

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

        let completedChunks = 0;
        let nextChunkIndex = 0;

        const sendChunk = async () => {
          while (nextChunkIndex < totalChunks) {
            const chunkIndex = nextChunkIndex;
            nextChunkIndex += 1;

            if (abortRef.current) {
              return;
            }

            await waitWhilePaused();

            const start = chunkIndex * chunkSize;
            const end = Math.min(start + chunkSize, file.size);
            const chunkBuffer = await file.slice(start, end).arrayBuffer();
            const chunk = new Uint8Array(chunkBuffer);
            const b64 = await encodeChunk(chunk);

            if (abortRef.current) {
              return;
            }

            const MAX_CHUNK_RETRIES = 3;
            let chunkSent = false;
            for (let retry = 0; retry < MAX_CHUNK_RETRIES && !chunkSent; retry++) {
              if (abortRef.current) return;
              chunkSent = await onSendCommand({ type: 'file_chunk', transferId, chunkIndex, chunkData: b64, isLast: chunkIndex === totalChunks - 1 });
              if (!chunkSent) {
                // Transport temporarily down — wait before retry
                await new Promise<void>((resolve) => setTimeout(resolve, 1000));
              }
            }
            if (!chunkSent) {
              setError('Transfer failed: connection lost');
              abortRef.current = true;
              return;
            }
            completedChunks += 1;

            updateBatch((current) => current ? {
              ...current,
              status: pausedRef.current ? 'paused' : 'active',
              updatedAt: Date.now(),
              files: current.files.map((item) =>
                item.id === transferId
                  ? {
                      ...item,
                      transferredBytes: Math.min(file.size, item.transferredBytes + chunk.byteLength),
                      status: completedChunks === totalChunks ? 'completed' : (pausedRef.current ? 'paused' : 'active'),
                      updatedAt: Date.now(),
                    }
                  : item),
            } : current);
          }
        };

        await Promise.all(Array.from({ length: Math.min(CHUNK_CONCURRENCY, totalChunks) }, () => sendChunk()));

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
  }, [clearHideTimer, encodeChunk, maxFileMB, onSendCommand, scheduleBatchClear, updateBatch, waitWhilePaused]);

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
        <input ref={fileInputRef} type="file" accept="*/*" multiple style={{ display: 'none' }} onChange={handleFileChange} />
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
      <input ref={fileInputRef} type="file" accept="*/*" multiple style={{ display: 'none' }} onChange={handleFileChange} />
      {error && <div style={{ fontSize: 12, color: 'var(--danger)' }}>{error}</div>}
    </div>
  );
});