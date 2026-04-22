import React from 'react';
import { EmptyIcon, ResendIcon, TrashIcon } from './Icons';
import type { HistoryEntry, InputCommand } from '../types';

interface HistoryProps {
  entries: HistoryEntry[];
  encryptionReady: boolean;
  onResend: (cmd: InputCommand) => void;
  onClearHistory: () => void;
  autoSaveHistory: boolean;
  onToggleAutoSave: () => void;
}

export const History: React.FC<HistoryProps> = ({
  entries, encryptionReady, onResend, onClearHistory,
  autoSaveHistory, onToggleAutoSave,
}) => {
  const handleResend = (entry: HistoryEntry) => {
    // Send the text as a diff (insert), then an enter to commit it
    onResend({ type: 'diff', text: entry.text, count: 0 });
    onResend({ type: 'enter' });
  };

  const formatTime = (ts: number) => {
    const d = new Date(ts);
    return d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: '0 20px', height: '100%' }}>
      {/* Header with settings */}
      <div style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
      }}>
        <h3 style={{ fontSize: 16, fontWeight: 600 }}>History</h3>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          {/* Auto-save toggle */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12, color: 'var(--text-secondary)' }}>
            <span>Auto-save</span>
            <button
              className={`toggle-switch ${autoSaveHistory ? 'on' : ''}`}
              onClick={onToggleAutoSave}
              style={{ transform: 'scale(0.7)', transformOrigin: 'right center' }}
            />
          </div>
          {entries.length > 0 && (
            <button
              className="glass-btn"
              style={{ fontSize: 12, padding: '6px 12px', display: 'flex', alignItems: 'center', gap: 4 }}
              onClick={onClearHistory}
            >
              <TrashIcon size={12} />
              Clear
            </button>
          )}
        </div>
      </div>

      <div className="scroll-area" style={{ flex: 1, maxHeight: 'calc(100vh - 260px)' }}>
        {entries.length === 0 ? (
          <div className="glass" style={{
            padding: 32,
            textAlign: 'center',
            color: 'var(--text-tertiary)',
          }}>
            <div style={{ marginBottom: 12, opacity: 0.5 }}>
              <EmptyIcon size={48} />
            </div>
            <p style={{ fontSize: 14 }}>{autoSaveHistory ? 'No history yet' : 'History is off'}</p>
            <p style={{ fontSize: 12, marginTop: 4 }}>
              {autoSaveHistory ? 'Your sent texts will appear here' : 'Turn on auto-save if you want to keep sent text snippets'}
            </p>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {[...entries].reverse().map((entry) => (
              <div key={entry.id} className="glass glass-sm history-item" style={{
                padding: 14,
                display: 'flex',
                flexDirection: 'column',
                gap: 8,
              }}>
                <p style={{
                  fontSize: 14,
                  lineHeight: 1.5,
                  color: 'var(--text-primary)',
                  wordBreak: 'break-all',
                  whiteSpace: 'pre-wrap',
                  maxHeight: 120,
                  overflow: 'hidden',
                }}>
                  {entry.text}
                </p>
                <div style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                }}>
                  <span style={{ fontSize: 11, color: 'var(--text-tertiary)' }}>
                    {formatTime(entry.timestamp)} · {entry.text.length} chars
                  </span>
                  <button
                    className="glass-btn"
                    style={{ fontSize: 11, padding: '4px 12px', display: 'flex', alignItems: 'center', gap: 4 }}
                    disabled={!encryptionReady}
                    onClick={() => handleResend(entry)}
                  >
                    <ResendIcon size={12} />
                    Resend
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
};
