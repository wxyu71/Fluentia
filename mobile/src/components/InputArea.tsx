import React, { useState, useRef, useCallback } from 'react';
import { computeDiff } from '../utils/diff';
import type { InputCommand, HistoryEntry } from '../types';

interface InputAreaProps {
  encryptionReady: boolean;
  onSendCommand: (cmd: InputCommand) => void;
  onAddHistory: (entry: HistoryEntry) => void;
}

export const InputArea: React.FC<InputAreaProps> = ({ encryptionReady, onSendCommand, onAddHistory }) => {
  const [text, setText] = useState('');
  const [isComposing, setIsComposing] = useState(false);
  const [charCount, setCharCount] = useState(0);
  const lastSentRef = useRef('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const sendDiff = useCallback((newText: string) => {
    const diff = computeDiff(lastSentRef.current, newText);
    // Send as a single atomic 'diff' command so the PC can apply
    // backspace + insert in one SendInput call — prevents race conditions.
    if (diff.backspace > 0 || diff.insert) {
      onSendCommand({ type: 'diff', count: diff.backspace, text: diff.insert || '' });
    }
    lastSentRef.current = newText;
    setCharCount((prev) => prev + (diff.insert?.length || 0));
  }, [onSendCommand]);

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    const newText = e.currentTarget.value;
    setText(newText);
    // Always send diffs — including during composition (voice input / IME).
    // This enables real-time streaming of every character change.
    if (encryptionReady) {
      sendDiff(newText);
    }
  }, [encryptionReady, sendDiff]);

  const handleCompositionStart = useCallback(() => {
    setIsComposing(true);
  }, []);

  const handleCompositionEnd = useCallback((e: React.CompositionEvent<HTMLTextAreaElement>) => {
    setIsComposing(false);
    const newText = e.currentTarget.value;
    setText(newText);
    if (encryptionReady) {
      sendDiff(newText);
    }
  }, [encryptionReady, sendDiff]);

  const handleClear = useCallback(() => {
    if (text.trim()) {
      onAddHistory({
        id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
        text: text.trim(),
        timestamp: Date.now(),
      });
    }
    setText('');
    lastSentRef.current = '';
    textareaRef.current?.focus();
  }, [text, onAddHistory]);

  const handleNewLine = useCallback(() => {
    if (encryptionReady) {
      onSendCommand({ type: 'text_commit', text: '\n' });
      const newText = text + '\n';
      setText(newText);
      lastSentRef.current = newText;
    }
  }, [text, encryptionReady, onSendCommand]);

  return (
    <div className="fade-in" style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: '0 20px' }}>
      {/* Status hint */}
      {!encryptionReady && (
        <div className="glass glass-xs" style={{
          padding: '10px 16px',
          textAlign: 'center',
          color: 'var(--warning)',
          fontSize: 13,
        }}>
          ⏳ Waiting for encrypted connection...
        </div>
      )}

      {/* Main textarea */}
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
          autoFocus
          style={{
            minHeight: 160,
            maxHeight: '40vh',
            lineHeight: 1.6,
            fontSize: 17,
          }}
        />
        {/* Character counter */}
        <div style={{
          position: 'absolute',
          bottom: 10,
          right: 14,
          fontSize: 11,
          color: 'var(--text-tertiary)',
        }}>
          {text.length} chars · {charCount} sent
        </div>
      </div>

      {/* Action buttons */}
      <div style={{ display: 'flex', gap: 8 }}>
        <button
          className="glass-btn accent full-width"
          disabled={!encryptionReady}
          onClick={handleNewLine}
        >
          ↵ Enter
        </button>
        <button
          className="glass-btn full-width"
          disabled={!text}
          onClick={handleClear}
        >
          ✕ Clear & Save
        </button>
      </div>

      {/* Composing indicator */}
      {isComposing && (
        <div style={{
          fontSize: 12,
          color: 'var(--accent)',
          textAlign: 'center',
          animation: 'pulse 1s ease-in-out infinite',
        }}>
          ● Composing...
        </div>
      )}
    </div>
  );
};
