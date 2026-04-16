import React, { useRef, useCallback, useImperativeHandle, forwardRef } from 'react';
import { computeDiff } from '../utils/diff';
import { ScanIcon, ClearIcon, ClipboardIcon } from './Icons';
import type { InputCommand, HistoryEntry } from '../types';

interface InputAreaProps {
  encryptionReady: boolean;
  text: string;
  setText: (text: string) => void;
  onSendCommand: (cmd: InputCommand) => void;
  onAddHistory: (entry: HistoryEntry) => void;
  onOpenScanner: () => void;
  autoSaveHistory: boolean;
}

export interface InputAreaHandle {
  resetDiffState: () => void;
}

export const InputArea = forwardRef<InputAreaHandle, InputAreaProps>(({
  encryptionReady,
  text,
  setText,
  onSendCommand,
  onAddHistory,
  onOpenScanner,
  autoSaveHistory,
}, ref) => {
  const lastSentRef = useRef('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const charCountRef = useRef(0);

  // Expose resetDiffState to parent (used when PC focus changes)
  useImperativeHandle(ref, () => ({
    resetDiffState: () => {
      lastSentRef.current = text;
    },
  }), [text]);

  const sendDiff = useCallback((newText: string) => {
    const diff = computeDiff(lastSentRef.current, newText);
    if (diff.backspace > 0 || diff.insert) {
      onSendCommand({ type: 'diff', count: diff.backspace, text: diff.insert || '' });
    }
    lastSentRef.current = newText;
    charCountRef.current += (diff.insert?.length || 0);
  }, [onSendCommand]);

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    const newText = e.currentTarget.value;
    setText(newText);
    if (encryptionReady) {
      sendDiff(newText);
    }
  }, [encryptionReady, sendDiff, setText]);

  const handleCompositionEnd = useCallback((e: React.CompositionEvent<HTMLTextAreaElement>) => {
    const newText = e.currentTarget.value;
    setText(newText);
    if (encryptionReady) {
      sendDiff(newText);
    }
  }, [encryptionReady, sendDiff, setText]);

  const handleClear = useCallback(() => {
    if (text.trim() && autoSaveHistory) {
      onAddHistory({
        id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
        text: text.trim(),
        timestamp: Date.now(),
      });
    }
    setText('');
    lastSentRef.current = '';
    if (encryptionReady) {
      onSendCommand({ type: 'clear' });
    }
    textareaRef.current?.focus();
  }, [text, autoSaveHistory, onAddHistory, encryptionReady, onSendCommand, setText]);

  const handleCopyToClipboard = useCallback(() => {
    if (!text.trim() || !encryptionReady) return;
    onSendCommand({ type: 'clipboard', text: text.trim() });
  }, [text, encryptionReady, onSendCommand]);

  return (
    <div className="input-page" style={{ padding: '0 20px' }}>
      {/* Spacer — pushes content to bottom for thumb reach */}
      <div style={{ flex: 1, minHeight: 40 }} />

      {/* Encryption hint */}
      {!encryptionReady && (
        <div className="glass glass-xs" style={{
          padding: '10px 16px',
          textAlign: 'center',
          color: 'var(--warning)',
          fontSize: 13,
          marginBottom: 12,
        }}>
          Waiting for encrypted connection...
        </div>
      )}

      {/* Toolbar: scan icon + clipboard */}
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
      )}

      {/* Main textarea — large, positioned for thumb reach */}
      <div style={{ position: 'relative' }}>
        <textarea
          ref={textareaRef}
          className="glass-input"
          value={text}
          onInput={handleInput}
          onCompositionEnd={handleCompositionEnd}
          placeholder={encryptionReady ? 'Start typing or use voice input...' : 'Connect to PC first...'}
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
          {text.length} chars
        </div>
      </div>

      {/* Clear button */}
      <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
        <button
          className="glass-btn full-width"
          disabled={!text}
          onClick={handleClear}
          style={{ gap: 6 }}
        >
          <ClearIcon size={14} />
          Clear{autoSaveHistory ? ' & Save' : ''}
        </button>
      </div>
    </div>
  );
});
