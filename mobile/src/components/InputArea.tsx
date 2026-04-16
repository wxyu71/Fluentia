import React, { useRef, useCallback, useImperativeHandle, forwardRef } from 'react';
import { computeDiff } from '../utils/diff';
import { ScanIcon, ClearIcon, ClipboardIcon } from './Icons';
import { FileTransfer } from './FileTransfer';
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
  /** Called when PC focus changes AWAY from target window — pause sync, keep lastSentRef intact */
  pauseSync: () => void;
  /** Called when PC focus returns to a text window — resume sync and immediately flush pending diff */
  resumeSync: () => void;
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
  const syncPausedRef = useRef(false);   // true while PC focus is on another window
  const isComposingRef = useRef(false);  // true during IME composition — skip diff during compose

  // sendDiff must be declared BEFORE useImperativeHandle so the handle closure can reference it
  const sendDiff = useCallback((newText: string) => {
    if (syncPausedRef.current) return; // don't send while PC focus is elsewhere
    const diff = computeDiff(lastSentRef.current, newText);
    if (diff.backspace > 0 || diff.insert) {
      onSendCommand({ type: 'diff', count: diff.backspace, text: diff.insert || '' });
    }
    lastSentRef.current = newText;
    charCountRef.current += (diff.insert?.length || 0);
  }, [onSendCommand]);

  // Expose pause/resume to parent
  useImperativeHandle(ref, () => ({
    pauseSync: () => {
      // Pause: keep lastSentRef pointing to last confirmed PC state (do NOT reset it).
      syncPausedRef.current = true;
    },
    resumeSync: () => {
      syncPausedRef.current = false;
      // Immediately flush any text changes that accumulated while paused.
      if (encryptionReady && !isComposingRef.current) {
        const currentText = textareaRef.current?.value ?? '';
        sendDiff(currentText);
      }
    },
  }), [encryptionReady, sendDiff]);

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    const newText = e.currentTarget.value;
    setText(newText);
    // Skip diff during IME composition to avoid sending partial/uncommitted characters.
    if (encryptionReady && !isComposingRef.current) {
      sendDiff(newText);
    }
  }, [encryptionReady, sendDiff, setText]);

  const handleCompositionStart = useCallback(() => {
    isComposingRef.current = true;
  }, []);

  const handleCompositionEnd = useCallback((e: React.CompositionEvent<HTMLTextAreaElement>) => {
    isComposingRef.current = false;
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
          onCompositionStart={handleCompositionStart}
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

      {/* Clear button + file transfer */}
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

      {/* File / photo transfer */}
      <FileTransfer encryptionReady={encryptionReady} onSendCommand={onSendCommand} />
    </div>
  );
});
