import React, { useState, useCallback } from 'react';
import { Header } from './components/Header';
import { InputArea } from './components/InputArea';
import { QRScanner } from './components/QRScanner';
import { History } from './components/History';
import { useWebSocket } from './hooks/useWebSocket';
import { useDeviceId } from './hooks/useDeviceId';
import type { AppTab, ConnectionInfo, HistoryEntry, InputCommand } from './types';

const HISTORY_KEY = 'fluentia_history';

function loadHistory(): HistoryEntry[] {
  try {
    const raw = localStorage.getItem(HISTORY_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

function saveHistory(entries: HistoryEntry[]) {
  localStorage.setItem(HISTORY_KEY, JSON.stringify(entries.slice(-100)));
}

export const App: React.FC = () => {
  const deviceId = useDeviceId();
  const {
    connectionState,
    peerConnected,
    encryptionReady,
    connect,
    disconnect,
    sendEncrypted,
    lastError,
  } = useWebSocket(deviceId);

  const [activeTab, setActiveTab] = useState<AppTab>('scan');
  const [history, setHistory] = useState<HistoryEntry[]>(loadHistory);

  const handleScan = useCallback((info: ConnectionInfo) => {
    connect(info);
    setActiveTab('input');
  }, [connect]);

  const handleSendCommand = useCallback((cmd: InputCommand) => {
    sendEncrypted(cmd);
  }, [sendEncrypted]);

  const handleAddHistory = useCallback((entry: HistoryEntry) => {
    setHistory((prev) => {
      const next = [...prev, entry];
      saveHistory(next);
      return next;
    });
  }, []);

  const handleClearHistory = useCallback(() => {
    setHistory([]);
    localStorage.removeItem(HISTORY_KEY);
  }, []);

  const handleResend = useCallback((cmd: InputCommand) => {
    sendEncrypted(cmd);
  }, [sendEncrypted]);

  return (
    <div style={{
      height: '100%',
      display: 'flex',
      flexDirection: 'column',
      position: 'relative',
      zIndex: 1,
    }}>
      {/* Animated background */}
      <div className="bg-scene">
        <div className="bg-blob bg-blob-1" />
        <div className="bg-blob bg-blob-2" />
        <div className="bg-blob bg-blob-3" />
        <div className="bg-blob bg-blob-4" />
      </div>

      {/* Header */}
      <Header
        connectionState={connectionState}
        peerConnected={peerConnected}
        encryptionReady={encryptionReady}
      />

      {/* Error / Preemption banner */}
      {(connectionState === 'preempted' || lastError) && (
        <div style={{
          margin: '0 20px 12px',
          padding: '12px 16px',
          background: 'rgba(239, 68, 68, 0.15)',
          border: '1px solid rgba(239, 68, 68, 0.3)',
          borderRadius: 16,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 12,
        }}>
          <div>
            <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--danger)' }}>
              {connectionState === 'preempted' ? '⚠️ Device Preempted' : '⚠️ Error'}
            </div>
            <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
              {lastError || 'Another device took control of this session'}
            </div>
          </div>
          <button
            className="glass-btn"
            style={{ fontSize: 12, padding: '6px 14px', flexShrink: 0 }}
            onClick={() => { disconnect(); setActiveTab('scan'); }}
          >
            Reconnect
          </button>
        </div>
      )}

      {/* Main Content */}
      <div style={{ flex: 1, overflow: 'hidden' }}>
        {activeTab === 'input' && (
          <InputArea
            encryptionReady={encryptionReady}
            onSendCommand={handleSendCommand}
            onAddHistory={handleAddHistory}
          />
        )}
        {activeTab === 'scan' && (
          <QRScanner onScan={handleScan} />
        )}
        {activeTab === 'history' && (
          <History
            entries={history}
            encryptionReady={encryptionReady}
            onResend={handleResend}
            onClearHistory={handleClearHistory}
          />
        )}
      </div>

      {/* Tab Bar */}
      <div style={{ padding: '12px 20px 20px', paddingBottom: 'max(20px, env(safe-area-inset-bottom))' }}>
        <div className="tab-bar">
          <button
            className={`tab-item ${activeTab === 'input' ? 'active' : ''}`}
            onClick={() => setActiveTab('input')}
          >
            <span className="tab-icon">⌨️</span>
            Input
          </button>
          <button
            className={`tab-item ${activeTab === 'scan' ? 'active' : ''}`}
            onClick={() => setActiveTab('scan')}
          >
            <span className="tab-icon">📷</span>
            Scan
          </button>
          <button
            className={`tab-item ${activeTab === 'history' ? 'active' : ''}`}
            onClick={() => setActiveTab('history')}
          >
            <span className="tab-icon">📋</span>
            History
          </button>
        </div>
      </div>
    </div>
  );
};
