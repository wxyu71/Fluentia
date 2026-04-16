import React, { useEffect, useRef, useState } from 'react';
import { Html5Qrcode } from 'html5-qrcode';
import { ScanIcon, CloseIcon, KeyboardIcon } from './Icons';
import type { ConnectionInfo } from '../types';

interface QRScannerProps {
  onScan: (info: ConnectionInfo) => void;
  overlay?: boolean;    // true = modal overlay, false = full page
  onClose?: () => void; // required when overlay=true
}

export const QRScanner: React.FC<QRScannerProps> = ({ onScan, overlay = false, onClose }) => {
  const scannerRef = useRef<Html5Qrcode | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [manualMode, setManualMode] = useState(false);

  const startScanner = async () => {
    if (scannerRef.current || !containerRef.current) return;
    setError(null);

    try {
      const scanner = new Html5Qrcode('qr-reader');
      scannerRef.current = scanner;
      setScanning(true);

      await scanner.start(
        { facingMode: 'environment' },
        {
          fps: 10,
          qrbox: { width: 220, height: 220 },
          aspectRatio: 1,
        },
        (decodedText) => {
          try {
            let data: ConnectionInfo;
            if (decodedText.startsWith('F1|')) {
              // Compact format: F1|token|key or F1|token|key|serverUrl
              const parts = decodedText.split('|');
              const wsUrl = parts.length >= 5 ? parts[3]
                : window.location.origin.replace(/^http/, 'ws') + '/ws';
              data = { s: wsUrl, t: parts[1], k: parts[2] };
            } else {
              // Legacy JSON format
              data = JSON.parse(decodedText) as ConnectionInfo;
            }
            if (data.s && data.t && data.k) {
              scanner.stop().catch(() => {});
              scannerRef.current = null;
              setScanning(false);
              onScan(data);
            } else {
              setError('Invalid QR code format');
            }
          } catch {
            setError('QR code is not valid');
          }
        },
        () => { /* ignore scan failures */ }
      );
    } catch (err: unknown) {
      setScanning(false);
      const message = err instanceof Error ? err.message : 'Camera access denied';
      setError(message);
    }
  };

  const stopScanner = async () => {
    if (scannerRef.current) {
      try {
        await scannerRef.current.stop();
      } catch { /* ignore */ }
      scannerRef.current = null;
      setScanning(false);
    }
  };

  useEffect(() => {
    return () => { stopScanner(); };
  }, []);

  const content = (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      gap: 16,
      padding: overlay ? '0' : '0 20px',
      width: '100%',
      maxWidth: 360,
    }}>
      <div className="glass" style={{
        padding: 20,
        textAlign: 'center',
        width: '100%',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, marginBottom: 8 }}>
          <ScanIcon size={20} color="var(--accent)" />
          <h3 style={{ fontSize: 16, fontWeight: 600 }}>Scan QR Code</h3>
        </div>
        <p style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>
          Open Fluentia on your PC and scan the QR code
        </p>

        {!manualMode ? (
          <>
            <div style={{
              position: 'relative',
              borderRadius: 16,
              overflow: 'hidden',
              background: 'rgba(0,0,0,0.3)',
              minHeight: 280,
            }}>
              <div id="qr-reader" ref={containerRef} style={{ width: '100%' }} />

              {!scanning && !error && (
                <div style={{
                  position: 'absolute',
                  inset: 0,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}>
                  <button className="glass-btn accent" onClick={startScanner}>
                    <ScanIcon size={16} color="white" /> Start Camera
                  </button>
                </div>
              )}
            </div>

            {error && (
              <div style={{
                marginTop: 12,
                padding: '10px 16px',
                background: 'rgba(255, 59, 48, 0.12)',
                borderRadius: 12,
                color: 'var(--danger)',
                fontSize: 13,
              }}>
                {error}
                <div style={{ marginTop: 8 }}>
                  <button className="glass-btn" style={{ fontSize: 13, padding: '8px 16px' }} onClick={startScanner}>
                    Retry
                  </button>
                </div>
              </div>
            )}

            {scanning && (
              <div style={{ marginTop: 12 }}>
                <button className="glass-btn danger" style={{ fontSize: 13, padding: '8px 16px' }} onClick={stopScanner}>
                  Stop Camera
                </button>
              </div>
            )}
          </>
        ) : (
          <ManualConnect onConnect={onScan} />
        )}

        <div style={{ marginTop: 12 }}>
          <button
            style={{
              background: 'none',
              border: 'none',
              color: 'var(--accent)',
              fontSize: 13,
              cursor: 'pointer',
              padding: '4px 8px',
            }}
            onClick={() => { setManualMode(!manualMode); if (scanning) stopScanner(); }}
          >
            {manualMode ? 'Use Camera' : 'Manual Connection'}
          </button>
        </div>
      </div>
    </div>
  );

  if (overlay) {
    return (
      <div className="scan-overlay">
        <button className="scan-overlay-close" onClick={() => { stopScanner(); onClose?.(); }}>
          <CloseIcon size={18} color="white" />
        </button>
        {content}
      </div>
    );
  }

  return (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      height: '100%',
      padding: '0 20px',
    }}>
      {content}
    </div>
  );
};

const ManualConnect: React.FC<{ onConnect: (info: ConnectionInfo) => void }> = ({ onConnect }) => {
  const [code, setCode] = useState('');
  const [status, setStatus] = useState('');
  const [verifyId, setVerifyId] = useState('');
  const [waiting, setWaiting] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);

  const handleSubmit = () => {
    const trimmed = code.trim().toUpperCase();
    if (trimmed.length < 6) {
      setStatus('Code must be at least 6 characters');
      return;
    }
    setStatus('Connecting...');
    setWaiting(true);

    // Connect to server and send device code
    const wsUrl = window.location.origin.replace(/^http/, 'ws') + '/ws';
    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      ws.send(JSON.stringify({
        type: 'device_code_join',
        deviceCode: trimmed,
        userAgent: navigator.userAgent.slice(0, 100),
        deviceId: localStorage.getItem('fluentia_device_id') || 'unknown',
      }));
    };

    ws.onmessage = (e) => {
      try {
        const msg = JSON.parse(e.data);
        if (msg.type === 'device_code_pending' && msg.verifyId) {
          setVerifyId(msg.verifyId);
          setStatus('Waiting for PC approval...');
        } else if (msg.type === 'joined' && msg.approved) {
          // Success! Build ConnectionInfo
          ws.close();
          const info: ConnectionInfo = {
            s: wsUrl,
            t: msg.token,
            k: msg.publicKey || '',
          };
          onConnect(info);
        } else if (msg.type === 'error') {
          setStatus(msg.error || 'Connection failed');
          setWaiting(false);
          ws.close();
        }
      } catch { /* ignore */ }
    };

    ws.onerror = () => {
      setStatus('Connection failed');
      setWaiting(false);
    };

    ws.onclose = () => {
      wsRef.current = null;
    };
  };

  useEffect(() => {
    return () => { wsRef.current?.close(); };
  }, []);

  return (
    <div style={{ textAlign: 'center' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, marginBottom: 12 }}>
        <KeyboardIcon size={18} color="var(--accent)" />
        <span style={{ fontSize: 14, fontWeight: 600 }}>Enter Device Code</span>
      </div>
      <p style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 12 }}>
        Enter the 8-character code shown on your PC
      </p>
      <input
        className="glass-input"
        value={code}
        onChange={(e) => setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8))}
        placeholder='e.g. A3F7K9B2'
        disabled={waiting}
        maxLength={8}
        style={{
          fontSize: 22,
          fontFamily: 'monospace',
          textAlign: 'center',
          letterSpacing: '0.2em',
          fontWeight: 700,
          marginBottom: 8,
        }}
        autoFocus
      />
      {verifyId && (
        <div className="glass glass-xs" style={{
          padding: '8px 16px',
          marginBottom: 8,
          fontSize: 13,
          color: 'var(--accent)',
        }}>
          Verification ID: <strong style={{ fontFamily: 'monospace' }}>{verifyId}</strong>
          <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginTop: 2 }}>
            Confirm this matches the ID shown on your PC
          </div>
        </div>
      )}
      {status && <div style={{ color: waiting ? 'var(--text-secondary)' : 'var(--danger)', fontSize: 12, marginBottom: 8 }}>{status}</div>}
      <button
        className="glass-btn accent full-width"
        style={{ fontSize: 14 }}
        onClick={handleSubmit}
        disabled={!code.trim() || waiting}
      >
        {waiting ? 'Waiting...' : 'Connect'}
      </button>
    </div>
  );
};
