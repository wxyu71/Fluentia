import React, { useState, useCallback, useRef, useEffect, useLayoutEffect } from 'react';
import { Header } from './components/Header';
import { InputArea } from './components/InputArea';
import { QRScanner } from './components/QRScanner';
import { History } from './components/History';
import { KeyboardIcon, HistoryIcon, WarningIcon } from './components/Icons';
import { useWebSocket } from './hooks/useWebSocket';
import { useDeviceId } from './hooks/useDeviceId';
import type { AppTab, ConnectionInfo, HistoryEntry, InputCommand, AppSettings } from './types';

const HISTORY_KEY = 'fluentia_history';
const SETTINGS_KEY = 'fluentia_settings';
const CONN_KEY = 'fluentia_conn';

function loadHistory(): HistoryEntry[] {
  try {
    if (!loadSettings().autoSaveHistory) {
      return [];
    }
    const raw = localStorage.getItem(HISTORY_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch { return []; }
}

function saveHistory(entries: HistoryEntry[]) {
  localStorage.setItem(HISTORY_KEY, JSON.stringify(entries.slice(-100)));
}

function loadSettings(): AppSettings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? JSON.parse(raw) : { autoSaveHistory: false };
  } catch { return { autoSaveHistory: false }; }
}

function saveSettings(s: AppSettings) {
  localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
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
    pendingStatus,
    inputResetVersion,
  } = useWebSocket(deviceId);

  const [activeTab, setActiveTab] = useState<AppTab>('input');
  const [history, setHistory] = useState<HistoryEntry[]>(loadHistory);
  const [settings, setSettings] = useState<AppSettings>(loadSettings);
  const [inputText, setInputText] = useState('');
  const [scannerOverlay, setScannerOverlay] = useState(false);
  const [fileTransferEnabled, setFileTransferEnabled] = useState(false);

  // Swipe state
  const touchStartRef = useRef<{ x: number; y: number; t: number } | null>(null);
  const swipeContainerRef = useRef<HTMLDivElement>(null);
  const [swipeOffset, setSwipeOffset] = useState(0);
  const [isSwiping, setIsSwiping] = useState(false);

  const isConnected = connectionState === 'connected' && peerConnected;

  useEffect(() => {
    if (inputResetVersion === 0) return;
    setInputText('');
  }, [inputResetVersion]);

  // Fix mobile keyboard occlusion: track visualViewport height
  useLayoutEffect(() => {
    const vv = window.visualViewport;
    if (!vv) return;
    const update = () => {
      document.documentElement.style.setProperty('--app-height', `${vv.height}px`);
    };
    update();
    vv.addEventListener('resize', update);
    vv.addEventListener('scroll', update);
    return () => {
      vv.removeEventListener('resize', update);
      vv.removeEventListener('scroll', update);
    };
  }, []);

  const handleScan = useCallback((info: ConnectionInfo) => {
    // Persist connection info for auto-reconnect
    localStorage.setItem(CONN_KEY, JSON.stringify(info));
    connect(info);
    setActiveTab('input');
    setScannerOverlay(false);
    // Fetch server config (file transfer toggle, etc.)
    const httpBase = info.s.replace(/\/ws.*$/, '').replace('wss://', 'https://').replace('ws://', 'http://');
    fetch(`${httpBase}/api/config`).then(r => r.json()).then(cfg => {
      if (typeof cfg.fileTransfer === 'boolean') setFileTransferEnabled(cfg.fileTransfer);
    }).catch(() => {});
  }, [connect]);

  // Auto-reconnect from localStorage on mount (survives page refresh)
  useEffect(() => {
    try {
      const raw = localStorage.getItem(CONN_KEY);
      if (raw) {
        const info = JSON.parse(raw) as ConnectionInfo;
        if (info.s && info.t && info.k) {
          connect(info);
        }
      }
    } catch { /* ignore */ }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Auto-reconnect on visibility change (15-minute tolerance)
  useEffect(() => {
    const handleVisibility = () => {
      if (document.visibilityState === 'visible' && connectionState === 'disconnected') {
        try {
          const raw = localStorage.getItem(CONN_KEY);
          if (raw) {
            const info = JSON.parse(raw) as ConnectionInfo;
            if (info.s && info.t && info.k) {
              connect(info);
            }
          }
        } catch { /* ignore */ }
      }
    };
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  }, [connectionState, connect]);

  const handleSendCommand = useCallback((cmd: InputCommand) => {
    sendEncrypted(cmd);
  }, [sendEncrypted]);

  const handleAddHistory = useCallback((entry: HistoryEntry) => {
    if (!settings.autoSaveHistory) {
      return;
    }

    setHistory((prev) => {
      const next = [...prev, entry];
      saveHistory(next);
      return next;
    });
  }, [settings.autoSaveHistory]);

  const handleClearHistory = useCallback(() => {
    setHistory([]);
    localStorage.removeItem(HISTORY_KEY);
  }, []);

  const handleResend = useCallback((cmd: InputCommand) => {
    sendEncrypted(cmd);
  }, [sendEncrypted]);

  const handleToggleAutoSave = useCallback(() => {
    setSettings(prev => {
      const next = { ...prev, autoSaveHistory: !prev.autoSaveHistory };
      if (!next.autoSaveHistory) {
        localStorage.removeItem(HISTORY_KEY);
        setHistory([]);
      }
      saveSettings(next);
      return next;
    });
  }, []);

  const handleCancelPendingConnection = useCallback(() => {
    disconnect();
    localStorage.removeItem(CONN_KEY);
    setInputText('');
    setScannerOverlay(false);
  }, [disconnect]);

  // Swipe gesture handlers
  const handleTouchStart = useCallback((e: React.TouchEvent) => {
    const touch = e.touches[0];
    touchStartRef.current = { x: touch.clientX, y: touch.clientY, t: Date.now() };
  }, []);

  const handleTouchMove = useCallback((e: React.TouchEvent) => {
    if (!touchStartRef.current) return;
    const touch = e.touches[0];
    const dx = touch.clientX - touchStartRef.current.x;
    const dy = touch.clientY - touchStartRef.current.y;
    // Only swipe if more horizontal than vertical
    if (Math.abs(dx) > Math.abs(dy) && Math.abs(dx) > 10) {
      setIsSwiping(true);
      setSwipeOffset(dx);
    }
  }, []);

  const handleTouchEnd = useCallback(() => {
    if (!touchStartRef.current || !isSwiping) {
      touchStartRef.current = null;
      return;
    }
    const threshold = 80;
    if (swipeOffset > threshold && activeTab === 'history') {
      setActiveTab('input');
    } else if (swipeOffset < -threshold && activeTab === 'input') {
      setActiveTab('history');
    }
    setSwipeOffset(0);
    setIsSwiping(false);
    touchStartRef.current = null;
  }, [swipeOffset, activeTab, isSwiping]);

  const tabIndex = activeTab === 'input' ? 0 : 1;
  const translateX = isSwiping
    ? `calc(-${tabIndex * 50}% + ${swipeOffset}px)`
    : `-${tabIndex * 50}%`;

  // Show full-page scanner when not connected (input tab)
  const showFullScanner = connectionState === 'disconnected' && !encryptionReady && activeTab === 'input';

  return (
    <div
      style={{
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
        position: 'relative',
        zIndex: 1,
      }}
      onTouchStart={handleTouchStart}
      onTouchMove={handleTouchMove}
      onTouchEnd={handleTouchEnd}
    >
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
        pendingStatus={pendingStatus}
      />

      {/* Error banner */}
      {(connectionState === 'preempted' || lastError) && (
        <div style={{
          margin: '0 20px 12px',
          padding: '12px 16px',
          background: 'rgba(255, 69, 58, 0.12)',
          border: '0.5px solid rgba(255, 69, 58, 0.25)',
          borderRadius: 14,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 12,
        }}>
          <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
            <WarningIcon size={16} color="var(--danger)" />
            <div>
              <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--danger)' }}>
                {connectionState === 'preempted' ? 'Device Preempted' : 'Error'}
              </div>
              <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
                {lastError || 'Another device took control'}
              </div>
            </div>
          </div>
          <button
            className="glass-btn"
            style={{ fontSize: 12, padding: '6px 14px', flexShrink: 0 }}
            onClick={() => { disconnect(); localStorage.removeItem(CONN_KEY); }}
          >
            Reconnect
          </button>
        </div>
      )}

      {/* Main Content — swipeable pages */}
      <div style={{ flex: 1, overflow: 'hidden' }}>
        {showFullScanner ? (
          <QRScanner onScan={handleScan} />
        ) : (
          <div
            ref={swipeContainerRef}
            className={`swipe-container ${isSwiping ? 'no-transition' : ''}`}
            style={{ transform: `translateX(${translateX})` }}
          >
            <div className="swipe-page">
              <InputArea
                encryptionReady={encryptionReady}
                text={inputText}
                setText={setInputText}
                onSendCommand={handleSendCommand}
                onAddHistory={handleAddHistory}
                onOpenScanner={() => setScannerOverlay(true)}
                autoSaveHistory={settings.autoSaveHistory}
                fileTransferEnabled={fileTransferEnabled}
                pendingStatus={pendingStatus}
                onCancelPendingConnection={handleCancelPendingConnection}
                inputResetVersion={inputResetVersion}
              />
            </div>
            <div className="swipe-page">
              <History
                entries={history}
                encryptionReady={encryptionReady}
                onResend={handleResend}
                onClearHistory={handleClearHistory}
                autoSaveHistory={settings.autoSaveHistory}
                onToggleAutoSave={handleToggleAutoSave}
              />
            </div>
          </div>
        )}
      </div>

      {/* Scanner overlay (modal) — only when connected */}
      {scannerOverlay && (
        <QRScanner onScan={handleScan} overlay onClose={() => setScannerOverlay(false)} />
      )}

      {/* Tab Bar — only show when connected */}
      {!showFullScanner && (
        <div style={{ padding: '12px 20px 20px', paddingBottom: 'max(20px, env(safe-area-inset-bottom))' }}>
          <div className="tab-bar">
            <button
              className={`tab-item ${activeTab === 'input' ? 'active' : ''}`}
              onClick={() => setActiveTab('input')}
            >
              <KeyboardIcon size={18} />
              Input
            </button>
            <button
              className={`tab-item ${activeTab === 'history' ? 'active' : ''}`}
              onClick={() => setActiveTab('history')}
            >
              <HistoryIcon size={18} />
              History
            </button>
          </div>
        </div>
      )}
    </div>
  );
};
