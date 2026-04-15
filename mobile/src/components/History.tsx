import React from 'react';
import type { HistoryEntry, InputCommand } from '../types';

interface HistoryProps {
  entries: HistoryEntry[];
  encryptionReady: boolean;
  onResend: (cmd: InputCommand) => void;
  onClearHistory: () => void;
}

export const History: React.FC<HistoryProps> = ({ entries, encryptionReady, onResend, onClearHistory }) => {
  const handleResend = (entry: HistoryEntry) => {
    onResend({ type: 'text_commit', text: entry.text });
  };

  const formatTime = (ts: number) => {
    const d = new Date(ts);
    return d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
  };

  return (
    <div className="fade-in" style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: '0 20px' }}>
      <div style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
      }}>
        <h3 style={{ fontSize: 16, fontWeight: 600 }}>Input History</h3>
        {entries.length > 0 && (
          <button
            className="glass-btn"
            style={{ fontSize: 12, padding: '6px 12px' }}
            onClick={onClearHistory}
          >
            Clear All
          </button>
        )}
      </div>

      <div className="scroll-area" style={{ flex: 1, maxHeight: 'calc(100vh - 260px)' }}>
        {entries.length === 0 ? (
          <div className="glass" style={{
            padding: 32,
            textAlign: 'center',
            color: 'var(--text-tertiary)',
          }}>
            <div style={{ fontSize: 40, marginBottom: 12 }}>📋</div>
            <p style={{ fontSize: 14 }}>No history yet</p>
            <p style={{ fontSize: 12, marginTop: 4 }}>Your sent texts will appear here</p>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {[...entries].reverse().map((entry) => (
              <div key={entry.id} className="glass glass-sm" style={{
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
                    style={{ fontSize: 11, padding: '4px 12px' }}
                    disabled={!encryptionReady}
                    onClick={() => handleResend(entry)}
                  >
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
