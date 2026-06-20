import React from 'react';
import { EmptyIcon, ResendIcon, TrashIcon } from './Icons';
import type { HistoryEntry, InputCommand } from '../types';
import { REGEX_SKILL_TEMPLATE, parseRegexMarkdown } from '../utils/regex';
import { debugLog } from '../utils/debugLog';

interface HistoryProps {
  entries: HistoryEntry[];
  encryptionReady: boolean;
  regexFilterEnabled: boolean;
  regexFilterMarkdown: string;
  onResend: (cmd: InputCommand) => void;
  onClearHistory: () => void;
  autoSaveHistory: boolean;
  onToggleAutoSave: () => void;
  onRegexSettingsChange: (enabled: boolean, markdown: string) => void;
}

export const History: React.FC<HistoryProps> = ({
  entries, encryptionReady, onResend, onClearHistory,
  autoSaveHistory, onToggleAutoSave,
  regexFilterEnabled, regexFilterMarkdown, onRegexSettingsChange,
}) => {
  const [regexEnabled, setRegexEnabled] = React.useState(regexFilterEnabled);
  const [regexDraft, setRegexDraft] = React.useState(regexFilterMarkdown);
  const [regexStatus, setRegexStatus] = React.useState('');
  const [debugEnabled, setDebugEnabled] = React.useState(debugLog.enabled);

  React.useEffect(() => {
    setRegexEnabled(regexFilterEnabled);
  }, [regexFilterEnabled]);

  React.useEffect(() => {
    setRegexDraft(regexFilterMarkdown);
  }, [regexFilterMarkdown]);

  const handleResend = (entry: HistoryEntry) => {
    // Send the text as a diff (insert), then an enter to commit it
    onResend({ type: 'diff', text: entry.text, count: 0 });
    onResend({ type: 'enter' });
  };

  const formatTime = (ts: number) => {
    const d = new Date(ts);
    return d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
  };

  const handleCopyTemplate = async () => {
    try {
      await navigator.clipboard.writeText(REGEX_SKILL_TEMPLATE);
      setRegexStatus('Skill template copied.');
    } catch {
      setRegexStatus('Failed to copy template.');
    }
  };

  const handleSaveRegex = () => {
    try {
      const normalizedMarkdown = regexDraft.trim() ? parseRegexMarkdown(regexDraft).normalizedMarkdown : '';
      onRegexSettingsChange(regexEnabled, normalizedMarkdown);
      setRegexDraft(normalizedMarkdown);
      setRegexStatus(regexEnabled && normalizedMarkdown ? 'Regex rules saved.' : 'Regex filter disabled.');
    } catch (error) {
      setRegexStatus(error instanceof Error ? error.message : 'Regex rules are invalid.');
    }
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

      <div className="glass glass-sm" style={{ padding: 14, display: 'grid', gap: 10 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
          <div>
            <div style={{ fontSize: 14, fontWeight: 600 }}>Regex Filter</div>
            <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
              Remove filler words before text leaves the phone.
            </div>
          </div>
          <button
            className={`toggle-switch ${regexEnabled ? 'on' : ''}`}
            onClick={() => setRegexEnabled((value) => !value)}
          />
        </div>

        <textarea
          className="glass-input"
          value={regexDraft}
          onChange={(event) => setRegexDraft(event.target.value)}
          placeholder="Paste the AI-generated Markdown regex block here"
          style={{ minHeight: 120, fontSize: 13, lineHeight: 1.5 }}
        />

        <div style={{ display: 'flex', gap: 8, justifyContent: 'space-between', alignItems: 'center' }}>
          <div style={{ fontSize: 12, color: regexStatus.includes('invalid') || regexStatus.includes('Failed') ? 'var(--danger)' : 'var(--text-secondary)' }}>
            {regexStatus || 'Paste Markdown code blocks with one regex rule per line.'}
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button className="glass-btn" style={{ fontSize: 12, padding: '6px 12px' }} onClick={handleCopyTemplate}>
              Copy Skill Template
            </button>
            <button className="glass-btn accent" style={{ fontSize: 12, padding: '6px 12px' }} onClick={handleSaveRegex}>
              Save Rules
            </button>
          </div>
        </div>
      </div>

      {/* Debug logging toggle */}
      <div className="glass glass-sm" style={{ padding: 14, display: 'grid', gap: 10 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
          <div>
            <div style={{ fontSize: 14, fontWeight: 600 }}>Debug logging</div>
            <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
              Record input sync events for troubleshooting. Zero overhead when off.
            </div>
          </div>
          <button
            className={`toggle-switch ${debugEnabled ? 'on' : ''}`}
            onClick={() => {
              const next = !debugEnabled;
              debugLog.enabled = next;
              setDebugEnabled(next);
            }}
          />
        </div>
        {debugEnabled && (
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            <span style={{ fontSize: 11, color: 'var(--text-tertiary)', alignSelf: 'center', marginRight: 'auto' }}>
              {debugLog.size} entries
            </span>
            <button
              className="glass-btn"
              style={{ fontSize: 11, padding: '4px 10px' }}
              onClick={() => { debugLog.clear(); setDebugEnabled(debugLog.enabled); }}
            >
              Clear
            </button>
            <button
              className="glass-btn"
              style={{ fontSize: 11, padding: '4px 10px' }}
              onClick={() => debugLog.download()}
            >
              Download
            </button>
          </div>
        )}
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
