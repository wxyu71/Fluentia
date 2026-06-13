import React, { useState, useCallback, useRef, useEffect, useLayoutEffect } from 'react';
import { Header } from './components/Header';
import { InputArea } from './components/InputArea';
import { QRScanner } from './components/QRScanner';
import { History } from './components/History';
import { KeyboardIcon, HistoryIcon, WarningIcon } from './components/Icons';
import { useBlePairing } from './hooks/useBlePairing';
import { useWebSocket } from './hooks/useWebSocket';
import { useDeviceId } from './hooks/useDeviceId';
import type { AppTab, ConnectionInfo, HistoryEntry, InputCommand, AppSettings } from './types';

const HISTORY_KEY = 'fluentia_history';
const SETTINGS_KEY = 'fluentia_settings';
const CONN_KEY = 'fluentia_conn';
const APP_VERSION = __APP_VERSION__;

const appContainerStyle: React.CSSProperties = {
  height: '100%',
  display: 'flex',
  flexDirection: 'column',
  position: 'relative',
  zIndex: 1,
};

const tabContentStyle: React.CSSProperties = {
  flex: 1,
  overflow: 'hidden',
};

const tabBarWrapperStyle: React.CSSProperties = {
  padding: '12px 20px 20px',
  paddingBottom: 'max(20px, env(safe-area-inset-bottom))',
};

const errorBannerStyle: React.CSSProperties = {
  margin: '0 20px 12px',
  padding: '12px 16px',
  background: 'rgba(255, 69, 58, 0.12)',
  border: '0.5px solid rgba(255, 69, 58, 0.25)',
  borderRadius: 14,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 12,
};

const errorContentStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: 8,
};

function resolveThemeColor(): string {
  const cssColor = getComputedStyle(document.documentElement).getPropertyValue('--bg-primary').trim();
  if (cssColor) {
    return cssColor;
  }

  return window.matchMedia('(prefers-color-scheme: dark)').matches ? '#000000' : '#FFFFFF';
}

function syncThemeColor() {
  const color = resolveThemeColor();
  let themeMeta = document.querySelector('meta[name="theme-color"]') as HTMLMetaElement | null;

  if (!themeMeta) {
    themeMeta = document.createElement('meta');
    themeMeta.name = 'theme-color';
    document.head.appendChild(themeMeta);
  }

  themeMeta.content = color;
  document.documentElement.style.backgroundColor = color;
  document.body.style.backgroundColor = color;
}

function loadHistory(): HistoryEntry[] {
  try {
    if (!loadSettings().autoSaveHistory) {
      return [];
    }
    const raw = localStorage.getItem(HISTORY_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch { /* Corrupted JSON in localStorage — fall back to empty history */ return []; }
}

function saveHistory(entries: HistoryEntry[]) {
  localStorage.setItem(HISTORY_KEY, JSON.stringify(entries.slice(-100)));
}

function loadSettings(): AppSettings {
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? JSON.parse(raw) : { autoSaveHistory: false, regexFilterEnabled: false, regexFilterMarkdown: '' };
  } catch { /* Corrupted settings JSON — fall back to defaults */ return { autoSaveHistory: false, regexFilterEnabled: false, regexFilterMarkdown: '' }; }
}

function saveSettings(s: AppSettings) {
  localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
}

function loadStoredConnection(): ConnectionInfo | null {
  try {
    const raw = localStorage.getItem(CONN_KEY);
    if (!raw) {
      return null;
    }

    const info = JSON.parse(raw) as ConnectionInfo;
    return info.s && info.t && info.k ? info : null;
  } catch {
    // Invalid or missing stored connection data.
    return null;
  }
}

export const App: React.FC = () => {
  const deviceId = useDeviceId();
  const [activeTab, setActiveTab] = useState<AppTab>('input');
  const [history, setHistory] = useState<HistoryEntry[]>(loadHistory);
  const [settings, setSettings] = useState<AppSettings>(loadSettings);
  const [inputText, setInputText] = useState('');
  const [scannerOverlay, setScannerOverlay] = useState(false);
  const [fileTransferEnabled, setFileTransferEnabled] = useState(false);
  const [maxFileMB, setMaxFileMB] = useState(0);
  const [bleAuthorizedPublicKey, setBleAuthorizedPublicKey] = useState<string | null>(null);
  const connectRef = useRef<(info: ConnectionInfo) => void>(() => undefined);
  const fetchConfigRef = useRef<(info: ConnectionInfo) => void>(() => undefined);
  const authorizeBleRef = useRef<(publicKey: string) => void>(() => undefined);

  useEffect(() => {
    document.title = `Fluentia v${APP_VERSION}`;
  }, []);

  const fetchServerConfig = useCallback((info: ConnectionInfo) => {
    const httpBase = info.s.replace(/\/ws.*$/, '').replace('wss://', 'https://').replace('ws://', 'http://');
    fetch(`${httpBase}/api/config`)
      .then((response) => response.json())
      .then((config) => {
        setFileTransferEnabled(typeof config.fileTransfer === 'boolean' ? config.fileTransfer : false);
        setMaxFileMB(typeof config.maxFileMB === 'number' ? config.maxFileMB : 0);
      })
      .catch(() => {
        setFileTransferEnabled(false);
        setMaxFileMB(0);
      });
  }, []);

  const handleBleConnectionInfo = useCallback((info: ConnectionInfo) => {
    localStorage.setItem(CONN_KEY, JSON.stringify(info));
    connectRef.current(info);
    fetchConfigRef.current(info);
    setActiveTab('input');
    setScannerOverlay(false);
  }, []);

  const blePairing = useBlePairing(handleBleConnectionInfo, deviceId, (publicKey) => authorizeBleRef.current(publicKey), bleAuthorizedPublicKey);
  const {
    connectionState,
    peerConnected,
    encryptionReady,
    connect,
    disconnect,
    sendEncrypted,
    lastError,
    pendingStatus,
    bufferedInputActive,
    queuedCommandCount,
    inputResetVersion,
    incomingTransferBatch,
  } = useWebSocket(deviceId, blePairing.sendEncryptedMessage, (cmd) => {
    if (cmd.type === 'ble_auth_ok' && cmd.publicKey) {
      setBleAuthorizedPublicKey(cmd.publicKey);
    }
  }, blePairing.isTransportReady);

  useEffect(() => {
    connectRef.current = connect;
    fetchConfigRef.current = fetchServerConfig;
    authorizeBleRef.current = (publicKey: string) => {
      setBleAuthorizedPublicKey(null);
      sendEncrypted({ type: 'ble_auth', publicKey });
    };
  }, [connect, fetchServerConfig, sendEncrypted]);

  const bleOnly = connectionState === 'connecting' && encryptionReady && blePairing.isTransportReady;

  // Swipe state
  const touchStartRef = useRef<{ x: number; y: number; t: number } | null>(null);
  const swipeContainerRef = useRef<HTMLDivElement>(null);
  const [swipeOffset, setSwipeOffset] = useState(0);
  const [isSwiping, setIsSwiping] = useState(false);

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

  useEffect(() => {
    const colorScheme = window.matchMedia('(prefers-color-scheme: dark)');
    const updateTheme = () => {
      window.requestAnimationFrame(syncThemeColor);
      window.setTimeout(syncThemeColor, 120);
    };
    const handleVisibility = () => {
      if (document.visibilityState === 'visible') {
        updateTheme();
      }
    };

    updateTheme();

    if (typeof colorScheme.addEventListener === 'function') {
      colorScheme.addEventListener('change', updateTheme);
    } else {
      colorScheme.addListener(updateTheme);
    }

    window.addEventListener('focus', updateTheme);
    window.addEventListener('pageshow', updateTheme);
    document.addEventListener('visibilitychange', handleVisibility);

    return () => {
      if (typeof colorScheme.removeEventListener === 'function') {
        colorScheme.removeEventListener('change', updateTheme);
      } else {
        colorScheme.removeListener(updateTheme);
      }

      window.removeEventListener('focus', updateTheme);
      window.removeEventListener('pageshow', updateTheme);
      document.removeEventListener('visibilitychange', handleVisibility);
    };
  }, []);

  const handleScan = useCallback((info: ConnectionInfo) => {
    // Persist connection info for auto-reconnect
    localStorage.setItem(CONN_KEY, JSON.stringify(info));
    connect(info);
    setActiveTab('input');
    setScannerOverlay(false);
    fetchServerConfig(info);
  }, [connect, fetchServerConfig]);

  const retryStoredConnection = useCallback(() => {
    const info = loadStoredConnection();
    if (!info) {
      return;
    }

    connect(info);
    fetchServerConfig(info);
    setActiveTab('input');
  }, [connect, fetchServerConfig]);

  // Auto-reconnect from localStorage on mount (survives page refresh)
  useEffect(() => {
    try {
      const raw = localStorage.getItem(CONN_KEY);
      if (raw) {
        const info = JSON.parse(raw) as ConnectionInfo;
        if (info.s && info.t && info.k) {
          connect(info);
          fetchServerConfig(info);
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
              fetchServerConfig(info);
            }
          }
        } catch { /* ignore */ }
      }
    };
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  }, [connectionState, connect, fetchServerConfig]);

  useEffect(() => {
    if (connectionState !== 'connected') return;

    try {
      const raw = localStorage.getItem(CONN_KEY);
      if (!raw) return;

      const info = JSON.parse(raw) as ConnectionInfo;
      if (info.s && info.t && info.k) {
        fetchServerConfig(info);
      }
    } catch {
      setFileTransferEnabled(false);
      setMaxFileMB(0);
    }
  }, [connectionState, fetchServerConfig]);

  useEffect(() => {
    if (!encryptionReady || !settings.regexFilterEnabled || !settings.regexFilterMarkdown.trim()) {
      return;
    }

    sendEncrypted({
      type: 'regex_config',
      text: settings.regexFilterMarkdown,
    });
  }, [encryptionReady, sendEncrypted, settings.regexFilterEnabled, settings.regexFilterMarkdown]);

  const handleSendCommand = useCallback(async (cmd: InputCommand): Promise<boolean> => {
    return sendEncrypted(cmd);
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

  const handleRegexSettingsChange = useCallback((regexFilterEnabled: boolean, regexFilterMarkdown: string) => {
    setSettings((prev) => {
      const next = {
        ...prev,
        regexFilterEnabled,
        regexFilterMarkdown,
      };
      saveSettings(next);
      return next;
    });

    if (encryptionReady && regexFilterEnabled && regexFilterMarkdown.trim()) {
      sendEncrypted({ type: 'regex_config', text: regexFilterMarkdown });
    }
  }, [encryptionReady, sendEncrypted]);

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
      style={appContainerStyle}
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
        bleTransportReady={blePairing.isTransportReady}
        wsDisconnected={bleOnly}
      />

      {/* Error banner */}
      {(connectionState === 'preempted' || lastError) && (
        <div style={errorBannerStyle}>
          <div style={errorContentStyle}>
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
            onClick={() => {
              if (loadStoredConnection()) {
                retryStoredConnection();
              } else {
                disconnect();
                localStorage.removeItem(CONN_KEY);
              }
            }}
          >
            Try again
          </button>
        </div>
      )}

      {/* Main Content — swipeable pages */}
      <div style={tabContentStyle}>
        {showFullScanner ? (
          <QRScanner onScan={handleScan} deviceId={deviceId} />
        ) : (
          <div
            ref={swipeContainerRef}
            className={`swipe-container ${isSwiping ? 'no-transition' : ''}`}
            style={{ transform: `translateX(${translateX})` }}
          >
            <div className="swipe-page">
              <InputArea
                encryptionReady={encryptionReady}
                bufferedInputActive={bufferedInputActive}
                queuedCommandCount={queuedCommandCount}
                regexFilterEnabled={settings.regexFilterEnabled}
                regexFilterMarkdown={settings.regexFilterMarkdown}
                text={inputText}
                setText={setInputText}
                onSendCommand={handleSendCommand}
                onAddHistory={handleAddHistory}
                onOpenScanner={() => setScannerOverlay(true)}
                autoSaveHistory={settings.autoSaveHistory}
                fileTransferEnabled={fileTransferEnabled}
                maxFileMB={maxFileMB}
                pendingStatus={pendingStatus}
                onCancelPendingConnection={handleCancelPendingConnection}
                inputResetVersion={inputResetVersion}
                incomingTransferBatch={incomingTransferBatch}
                blePairing={blePairing}
                bleOnly={bleOnly}
              />
            </div>
            <div className="swipe-page">
              <History
                entries={history}
                encryptionReady={encryptionReady}
                regexFilterEnabled={settings.regexFilterEnabled}
                regexFilterMarkdown={settings.regexFilterMarkdown}
                onResend={handleResend}
                onClearHistory={handleClearHistory}
                autoSaveHistory={settings.autoSaveHistory}
                onToggleAutoSave={handleToggleAutoSave}
                onRegexSettingsChange={handleRegexSettingsChange}
              />
            </div>
          </div>
        )}
      </div>

      {/* Scanner overlay (modal) — only when connected */}
      {scannerOverlay && (
        <QRScanner
          onScan={handleScan}
          deviceId={deviceId}
          overlay
          onClose={() => setScannerOverlay(false)}
        />
      )}

      {/* Tab Bar — only show when connected */}
      {!showFullScanner && (
        <div style={tabBarWrapperStyle}>
          <div className="tab-bar" role="tablist">
            <button
              className={`tab-item ${activeTab === 'input' ? 'active' : ''}`}
              role="tab"
              aria-selected={activeTab === 'input'}
              onClick={() => setActiveTab('input')}
            >
              <KeyboardIcon size={18} />
              Input
            </button>
            <button
              className={`tab-item ${activeTab === 'history' ? 'active' : ''}`}
              role="tab"
              aria-selected={activeTab === 'history'}
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
