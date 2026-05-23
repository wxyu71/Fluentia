import React, { useEffect, useRef, useCallback, useState } from 'react';
import { computeDiff } from '../utils/diff';
import { ScanIcon, ClipboardIcon, BluetoothIcon } from './Icons';
import { FileTransfer } from './FileTransfer';
import { TransferStatusCard } from './TransferStatusCard';
import { BlePairingCard } from './BlePairingCard';
import type { FileTransferHandle } from './FileTransfer';
import type { InputCommand, HistoryEntry, TransferBatchProgress } from '../types';
import type { UseBlePairingResult } from '../hooks/useBlePairing';
import { applyRegexFilters } from '../utils/regex';

interface InputAreaProps {
  encryptionReady: boolean;
  bufferedInputActive: boolean;
  queuedCommandCount: number;
  regexFilterEnabled: boolean;
  regexFilterMarkdown: string;
  text: string;
  setText: (text: string) => void;
  onSendCommand: (cmd: InputCommand) => void;
  onAddHistory: (entry: HistoryEntry) => void;
  onOpenScanner: () => void;
  autoSaveHistory: boolean;
  fileTransferEnabled?: boolean;
  maxFileMB?: number;
  pendingStatus: string | null;
  onCancelPendingConnection: () => void;
  inputResetVersion: number;
  incomingTransferBatch?: TransferBatchProgress | null;
  blePairing?: UseBlePairingResult;
}

export const InputArea: React.FC<InputAreaProps> = ({
  encryptionReady,
  bufferedInputActive,
  queuedCommandCount,
  regexFilterEnabled,
  regexFilterMarkdown,
  text,
  setText,
  onSendCommand,
  onAddHistory,
  onOpenScanner,
  autoSaveHistory,
  fileTransferEnabled = false,
  maxFileMB = 0,
  pendingStatus,
  onCancelPendingConnection,
  inputResetVersion,
  incomingTransferBatch = null,
  blePairing,
}) => {
  const inputEnabled = encryptionReady || bufferedInputActive;
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
    const normalized = applyRegexFilters(paragraphText.replace(/\r/g, ''), regexFilterMarkdown, regexFilterEnabled);
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
  }, [addHistoryEntry, onSendCommand, regexFilterEnabled, regexFilterMarkdown, sendDiff, setText]);

  const processTextValue = useCallback((rawValue: string, source?: HTMLTextAreaElement | null) => {
    const normalized = applyRegexFilters(rawValue.replace(/\r/g, ''), regexFilterMarkdown, regexFilterEnabled);

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
  }, [addHistoryEntry, encryptionReady, onSendCommand, regexFilterEnabled, regexFilterMarkdown, sendDiff, setText]);

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
    onSendCommand({ type: 'clipboard', text: applyRegexFilters(text.trim(), regexFilterMarkdown, regexFilterEnabled) });
  }, [text, encryptionReady, onSendCommand, regexFilterEnabled, regexFilterMarkdown]);

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
            {bufferedInputActive
              ? `You can keep typing. ${queuedCommandCount} buffered command${queuedCommandCount === 1 ? '' : 's'} will replay after reconnect.`
              : 'You can go back if the phone stays stuck on key exchange.'}
          </div>
          {!bufferedInputActive && (
            <button
              className="glass-btn"
              style={{ fontSize: 12, padding: '8px 14px', marginTop: 12 }}
              onClick={onCancelPendingConnection}
            >
              Cancel and go back
            </button>
          )}
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

      {encryptionReady && blePairing && (
        <BlePairingCard
          isSupported={blePairing.isSupported}
          isAvailable={blePairing.isAvailable}
          isConnecting={blePairing.isConnecting}
          status={blePairing.status}
          error={blePairing.error}
          deviceName={blePairing.deviceName}
          verificationCode={blePairing.verificationCode}
          onRequestPairing={() => { void blePairing.requestPairing(); }}
          onDisconnect={() => { void blePairing.disconnect(); }}
        />
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
            {blePairing?.isSupported && (
              <div style={{
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                padding: '0 8px',
                fontSize: 12,
                color: blePairing.isTransportReady ? 'var(--accent)' : 'var(--text-secondary)',
              }}>
                <BluetoothIcon size={14} color={blePairing.isTransportReady ? 'var(--accent)' : 'var(--text-secondary)'} />
                {blePairing.isTransportReady ? 'BLE ready' : 'BLE optional'}
              </div>
            )}
            {fileTransferEnabled && (
              <FileTransfer
                ref={fileTransferRef}
                encryptionReady={encryptionReady}
                onSendCommand={onSendCommand}
                compact
                maxFileMB={maxFileMB}
                onBatchStateChange={setOutgoingTransferBatch}
              />
            )}
            <button
              onClick={handleCopyToClipboard}
              disabled={!text.trim() || !inputEnabled}
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
                opacity: text.trim() && inputEnabled ? 1 : 0.4,
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
          placeholder={inputEnabled ? 'Type or use voice input. Enter sends.' : 'Connect to a PC first.'}
          disabled={!inputEnabled}
          autoFocus={inputEnabled}
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
