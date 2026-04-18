import React, { useRef, useCallback } from 'react';
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
  fileTransferEnabled?: boolean;
}

/**
 * Paragraph-model InputArea.
 *
 * Mobile textarea holds only the CURRENT paragraph (no newlines).
 * Keystrokes are diffed against the last-sent state and sent to PC in real time.
 * When the user presses Enter (or voice input inserts a newline):
 *   1. Current paragraph is committed → diff + 'enter' command sent to PC.
 *   2. Optionally saved to history.
 *   3. Textarea clears and diff state resets for the next paragraph.
 */
export const InputArea: React.FC<InputAreaProps> = ({
  encryptionReady,
  text,
  setText,
  onSendCommand,
  onAddHistory,
  onOpenScanner,
  autoSaveHistory,
  fileTransferEnabled = false,
}) => {
  const lastSentRef = useRef('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const isComposingRef = useRef(false);
  const fileTransferRef = useRef<{ open: () => void }>(null);

  /** Send diff between lastSentRef and newText. */
  const sendDiff = useCallback((newText: string) => {
    const diff = computeDiff(lastSentRef.current, newText);
    if (diff.backspace > 0 || diff.insert) {
      onSendCommand({ type: 'diff', count: diff.backspace, text: diff.insert || '' });
    }
    lastSentRef.current = newText;
  }, [onSendCommand]);

  /** Commit a paragraph: flush diff, send Enter, save history, reset. */
  const commitParagraph = useCallback((paragraphText: string) => {
    if (paragraphText !== lastSentRef.current) {
      sendDiff(paragraphText);
    }
    onSendCommand({ type: 'enter' });
    if (paragraphText.trim() && autoSaveHistory) {
      onAddHistory({
        id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
        text: paragraphText.trim(),
        timestamp: Date.now(),
      });
    }
    setText('');
    lastSentRef.current = '';
  }, [sendDiff, onSendCommand, autoSaveHistory, onAddHistory, setText]);

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    const raw = e.currentTarget.value;

    // Voice input or keyboard may insert newlines → each newline = paragraph commit.
    if (raw.includes('\n')) {
      const parts = raw.split('\n');
      for (let i = 0; i < parts.length - 1; i++) {
        if (i === 0) {
          commitParagraph(parts[i]);
        } else {
          if (parts[i]) sendDiff(parts[i]);
          onSendCommand({ type: 'enter' });
          if (parts[i].trim() && autoSaveHistory) {
            onAddHistory({
              id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
              text: parts[i].trim(),
              timestamp: Date.now(),
            });
          }
          lastSentRef.current = '';
        }
      }
      const remainder = parts[parts.length - 1];
      setText(remainder);
      if (remainder) sendDiff(remainder);
      return;
    }

    setText(raw);
    if (encryptionReady && !isComposingRef.current) {
      sendDiff(raw);
    }
  }, [encryptionReady, sendDiff, setText, commitParagraph, onSendCommand, autoSaveHistory, onAddHistory]);

  const handleCompositionStart = useCallback(() => {
    isComposingRef.current = true;
  }, []);

  const handleCompositionEnd = useCallback((e: React.CompositionEvent<HTMLTextAreaElement>) => {
    isComposingRef.current = false;
    const raw = e.currentTarget.value;
    if (raw.includes('\n')) {
      handleInput({ currentTarget: { value: raw } } as React.FormEvent<HTMLTextAreaElement>);
      return;
    }
    setText(raw);
    if (encryptionReady) {
      sendDiff(raw);
    }
  }, [encryptionReady, sendDiff, setText, handleInput]);

  /** Enter key → commit paragraph (keyboard users). */
  const handleKeyDown = useCallback((e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !isComposingRef.current) {
      e.preventDefault();
      commitParagraph(text);
    }
  }, [text, commitParagraph]);

  const handleClear = useCallback(() => {
    if (text.trim() && autoSaveHistory) {
      onAddHistory({
        id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
        text: text.trim(),
        timestamp: Date.now(),
      });
    }
    if (encryptionReady && lastSentRef.current.length > 0) {
      onSendCommand({ type: 'diff', count: lastSentRef.current.length, text: '' });
    }
    setText('');
    lastSentRef.current = '';
    textareaRef.current?.focus();
  }, [text, autoSaveHistory, onAddHistory, encryptionReady, onSendCommand, setText]);

  const handleCopyToClipboard = useCallback(() => {
    if (!text.trim() || !encryptionReady) return;
    onSendCommand({ type: 'clipboard', text: text.trim() });
  }, [text, encryptionReady, onSendCommand]);

  return (
    <div className="input-page" style={{ padding: '0 20px' }}>
      <div style={{ flex: 1, minHeight: 40 }} />

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
          placeholder={encryptionReady ? 'Type or use voice input… Enter ↵ sends.' : 'Connect to PC first...'}
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
          {text.length > 0 ? `${text.length} chars · Enter ↵ sends` : ''}
        </div>
      </div>

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
};
