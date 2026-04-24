import React, { useEffect, useRef, useCallback, useState } from 'react';
import { computeDiff } from '../utils/diff';
import { ScanIcon, ClipboardIcon } from './Icons';
import { FileTransfer } from './FileTransfer';
import { TransferStatusCard } from './TransferStatusCard';
import type { FileTransferHandle } from './FileTransfer';
import type { InputCommand, HistoryEntry, TransferBatchProgress } from '../types';

interface InputAreaProps {
  encryptionReady: boolean;
  text: string;
  setText: (text: string) => void;
  onSendCommand: (cmd: InputCommand) => void;
  onAddHistory: (entry: HistoryEntry) => void;
  onOpenScanner: () => void;
  autoSaveHistory: boolean;
  fileTransferEnabled?: boolean;
  pendingStatus: string | null;
  onCancelPendingConnection: () => void;
  inputResetVersion: number;
  incomingTransferBatch?: TransferBatchProgress | null;
}

export const InputArea: React.FC<InputAreaProps> = ({
  encryptionReady,
  text,
  setText,
  onSendCommand,
  onAddHistory,
  onOpenScanner,
  autoSaveHistory,
  fileTransferEnabled = false,
  pendingStatus,
  onCancelPendingConnection,
  inputResetVersion,
  incomingTransferBatch = null,
}) => {
  const lastSentRef = useRef('');
  const textRef = useRef(text);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const isComposingRef = useRef(false);
  const fileTransferRef = useRef<FileTransferHandle>(null);
  const [resetNotice, setResetNotice] = useState(false);
  const [outgoingTransferBatch, setOutgoingTransferBatch] = useState<TransferBatchProgress | null>(null);

  useEffect(() => {
    textRef.current = text;
  }, [text]);

  const sendDiff = useCallback((newText: string) => {
    const diff = computeDiff(lastSentRef.current, newText);
    if (diff.backspace > 0 || diff.insert) {
      onSendCommand({ type: 'diff', count: diff.backspace, text: diff.insert || '' });
    }
    lastSentRef.current = newText;
  }, [onSendCommand]);

  const addHistoryEntry = useCallback((paragraphText: string) => {
    if (!paragraphText.trim() || !autoSaveHistory) return;
    onAddHistory({
      id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
      text: paragraphText.trim(),
      timestamp: Date.now(),
    });
  }, [autoSaveHistory, onAddHistory]);

  const commitParagraph = useCallback((paragraphText: string) => {
    const normalized = paragraphText.replace(/\r/g, '');
    if (normalized !== lastSentRef.current) {
      sendDiff(normalized);
    }

    onSendCommand({ type: 'enter' });
    addHistoryEntry(normalized);
    setText('');
    textRef.current = '';
    lastSentRef.current = '';

    if (textareaRef.current) {
      textareaRef.current.value = '';
    }
  }, [addHistoryEntry, onSendCommand, sendDiff, setText]);

  const processTextValue = useCallback((rawValue: string, source?: HTMLTextAreaElement | null) => {
    const normalized = rawValue.replace(/\r/g, '');

    if (normalized.includes('\n')) {
      const segments = normalized.split('\n');
      const completed = segments.slice(0, -1);

      for (const segment of completed) {
        if (segment !== lastSentRef.current) {
          sendDiff(segment);
        }
        onSendCommand({ type: 'enter' });
        addHistoryEntry(segment);
        lastSentRef.current = '';
      }

      const remainder = segments[segments.length - 1] ?? '';
      if (source) {
        source.value = remainder;
      }
      setText(remainder);
      textRef.current = remainder;
      if (remainder) {
        sendDiff(remainder);
      } else {
        lastSentRef.current = '';
      }
      return;
    }

    if (normalized === textRef.current) return;

    setText(normalized);
    textRef.current = normalized;
    if (encryptionReady) {
      sendDiff(normalized);
    }
  }, [addHistoryEntry, encryptionReady, onSendCommand, sendDiff, setText]);

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    processTextValue(e.currentTarget.value, e.currentTarget);
  }, [processTextValue]);

  const handleCompositionStart = useCallback(() => {
    isComposingRef.current = true;
  }, []);

  const handleCompositionEnd = useCallback((e: React.CompositionEvent<HTMLTextAreaElement>) => {
    isComposingRef.current = false;
    processTextValue(e.currentTarget.value, e.currentTarget);
  }, [processTextValue]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !isComposingRef.current) {
      e.preventDefault();
      commitParagraph(textRef.current);
    }
  }, [commitParagraph]);

  const handleCopyToClipboard = useCallback(() => {
    if (!text.trim() || !encryptionReady) return;
    onSendCommand({ type: 'clipboard', text: text.trim() });
  }, [text, encryptionReady, onSendCommand]);

  useEffect(() => {
    if (inputResetVersion === 0) return;

    setText('');
    textRef.current = '';
    lastSentRef.current = '';
    if (textareaRef.current) {
      textareaRef.current.value = '';
    }
    setResetNotice(true);

    const timer = window.setTimeout(() => {
      setResetNotice(false);
    }, 2200);

    return () => window.clearTimeout(timer);
  }, [inputResetVersion, setText]);

  return (
    <div className="input-page" style={{ padding: '0 20px' }}>
      <div style={{ flex: 1, minHeight: 40 }} />

      {!encryptionReady && (
        <div className="glass glass-xs" style={{
          padding: '16px 18px',
          marginBottom: 12,
          textAlign: 'left',
        }}>
          <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--text-primary)' }}>
            {pendingStatus || 'Waiting for encrypted connection'}
          </div>
          <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 4 }}>
            You can go back if the phone stays stuck on key exchange.
          </div>
          <button
            className="glass-btn"
            style={{ fontSize: 12, padding: '8px 14px', marginTop: 12 }}
            onClick={onCancelPendingConnection}
          >
            Cancel and go back
          </button>
        </div>
      )}

      {resetNotice && encryptionReady && (
        <div className="glass glass-xs" style={{
          padding: '10px 16px',
          marginBottom: 12,
          fontSize: 13,
          color: 'var(--warning)',
        }}>
          PC focus changed. Start a new line for the new app.
        </div>
      )}

      {(outgoingTransferBatch || incomingTransferBatch) && (
        <div className="transfer-stack">
          {outgoingTransferBatch && (
            <TransferStatusCard
              batch={outgoingTransferBatch}
              onPauseToggle={() => fileTransferRef.current?.togglePause()}
              onCancel={() => fileTransferRef.current?.cancel()}
            />
          )}
          {incomingTransferBatch && (
            <TransferStatusCard batch={incomingTransferBatch} />
          )}
        </div>
      )}

      {encryptionReady && (
        <div style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          marginBottom: 8,
          padding: '0 4px',
        }}>
          <button
            onClick={onOpenScanner}
            style={{
              background: 'none',
              border: 'none',
              color: 'var(--accent)',
              cursor: 'pointer',
              padding: '6px',
              display: 'flex',
              alignItems: 'center',
              gap: 6,
              fontSize: 13,
              fontWeight: 500,
            }}
          >
            <ScanIcon size={18} color="var(--accent)" />
            Scan
          </button>
          <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
            {fileTransferEnabled && (
              <FileTransfer
                ref={fileTransferRef}
                encryptionReady={encryptionReady}
                onSendCommand={onSendCommand}
                compact
                onBatchStateChange={setOutgoingTransferBatch}
              />
            )}
            <button
              onClick={handleCopyToClipboard}
              disabled={!text.trim()}
              style={{
                background: 'none',
                border: 'none',
                color: text.trim() ? 'var(--accent)' : 'var(--text-tertiary)',
                cursor: text.trim() ? 'pointer' : 'default',
                padding: '6px',
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                fontSize: 13,
                fontWeight: 500,
                opacity: text.trim() ? 1 : 0.4,
              }}
            >
              <ClipboardIcon size={16} />
              Copy to PC
            </button>
          </div>
        </div>
      )}

      <div style={{ position: 'relative' }}>
        <textarea
          ref={textareaRef}
          className="glass-input"
          value={text}
          onInput={handleInput}
          onKeyDown={handleKeyDown}
          onCompositionStart={handleCompositionStart}
          onCompositionEnd={handleCompositionEnd}
          placeholder={encryptionReady ? 'Type or use voice input. Enter sends.' : 'Connect to a PC first.'}
          disabled={!encryptionReady}
          autoFocus={encryptionReady}
          style={{
            minHeight: 200,
            maxHeight: '50vh',
            lineHeight: 1.6,
            fontSize: 17,
          }}
        />
        <div style={{
          position: 'absolute',
          bottom: 10,
          right: 14,
          fontSize: 11,
          color: 'var(--text-tertiary)',
        }}>
          {text.length > 0 ? `${text.length} chars · Enter sends` : ''}
        </div>
      </div>
    </div>
  );
};
