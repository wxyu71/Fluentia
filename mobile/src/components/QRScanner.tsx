import React, { useEffect, useRef, useState, useCallback } from 'react';
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
  // 8 individual character slots
  const [chars, setChars] = useState<string[]>(Array(8).fill(''));
  const [status, setStatus] = useState('');
  const [verifyId, setVerifyId] = useState('');
  const [waiting, setWaiting] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);
  const inputRefs = useRef<(HTMLInputElement | null)[]>(Array(8).fill(null));

  const getCode = () => chars.join('');

  const focusBox = (idx: number) => {
    if (idx >= 0 && idx < 8) inputRefs.current[idx]?.focus();
  };

  const handleKeyDown = useCallback((idx: number, e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Backspace') {
      e.preventDefault();
      if (chars[idx]) {
        // Clear current box
        setChars(prev => { const n = [...prev]; n[idx] = ''; return n; });
      } else {
        // Move back and clear previous
        if (idx > 0) {
          setChars(prev => { const n = [...prev]; n[idx - 1] = ''; return n; });
          focusBox(idx - 1);
        }
      }
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      focusBox(idx - 1);
    } else if (e.key === 'ArrowRight') {
      e.preventDefault();
      focusBox(idx + 1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const code = getCode();
      if (code.length === 8) handleSubmit(code);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chars]);

  const handleInput = useCallback((idx: number, e: React.ChangeEvent<HTMLInputElement>) => {
    // Take only alphanumeric, uppercase
    const raw = e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '');
    if (!raw) return;

    if (raw.length > 1) {
      // Paste: distribute from current position
      const newChars = [...chars];
      let pos = idx;
      for (const ch of raw) {
        if (pos >= 8) break;
        newChars[pos] = ch;
        pos++;
      }
      setChars(newChars);
      focusBox(Math.min(pos, 7));
      return;
    }

    // Single char
    const newChars = [...chars];
    newChars[idx] = raw[0];
    setChars(newChars);
    if (idx < 7) focusBox(idx + 1);
    // Auto-submit when all filled
    const fullCode = newChars.join('');
    if (fullCode.length === 8 && !newChars.includes('')) {
      setTimeout(() => handleSubmit(fullCode), 80);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chars]);

  const handlePaste = useCallback((e: React.ClipboardEvent<HTMLInputElement>) => {
    e.preventDefault();
    const pasted = e.clipboardData.getData('text').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8);
    const newChars = [...chars];
    for (let i = 0; i < 8; i++) newChars[i] = pasted[i] || '';
    setChars(newChars);
    const nextEmpty = newChars.findIndex(c => !c);
    focusBox(nextEmpty === -1 ? 7 : nextEmpty);
    if (pasted.length === 8) {
      setTimeout(() => handleSubmit(pasted), 80);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chars]);

  const handleSubmit = useCallback((code: string) => {
    if (code.length !== 8 || waiting) return;
    setStatus('Connecting...');
    setWaiting(true);

    const wsUrl = window.location.origin.replace(/^http/, 'ws') + '/ws';
    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      ws.send(JSON.stringify({
        type: 'device_code_join',
        deviceCode: code,
        userAgent: navigator.userAgent.slice(0, 100),
        deviceId: localStorage.getItem('fluentia_device_id') || 'unknown',
      }));
    };

    ws.onmessage = (e) => {
      try {
        const msg = JSON.parse(e.data as string);
        if (msg.type === 'device_code_pending' && msg.verifyId) {
          setVerifyId(msg.verifyId);
          setStatus('Waiting for PC approval...');
        } else if (msg.type === 'joined' && msg.approved) {
          ws.close();
          const info: ConnectionInfo = { s: wsUrl, t: msg.token, k: msg.publicKey || '' };
          onConnect(info);
        } else if (msg.type === 'error') {
          setStatus(msg.error || 'Connection failed');
          setWaiting(false);
          ws.close();
        }
      } catch { /* ignore */ }
    };

    ws.onerror = () => { setStatus('Connection failed'); setWaiting(false); };
    ws.onclose = () => { wsRef.current = null; };
  }, [waiting, onConnect]);

  useEffect(() => { return () => { wsRef.current?.close(); }; }, []);

  // Render
  const boxStyle = (filled: boolean): React.CSSProperties => ({
    width: 36,
    height: 44,
    borderRadius: 10,
    background: filled ? 'var(--glass-bg, rgba(255,255,255,0.12))' : 'rgba(255,255,255,0.06)',
    border: `2px solid ${filled ? 'var(--accent)' : 'rgba(255,255,255,0.2)'}`,
    color: 'var(--text)',
    fontSize: 20,
    fontWeight: 700,
    fontFamily: 'monospace',
    textAlign: 'center' as const,
    outline: 'none',
    caretColor: 'transparent',
    transition: 'border-color 0.15s',
    WebkitAppearance: 'none',
    // Disable browser autocomplete UI inside the box
    padding: 0,
  });

  return (
    <div style={{ textAlign: 'center' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, marginBottom: 12 }}>
        <KeyboardIcon size={18} color="var(--accent)" />
        <span style={{ fontSize: 14, fontWeight: 600 }}>Enter Device Code</span>
      </div>
      <p style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>
        Enter the 8-character code shown on your PC
      </p>

      {/* 8-box input: 4 boxes · dot · 4 boxes */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, marginBottom: 16 }}>
        {Array.from({ length: 8 }, (_, i) => (
          <React.Fragment key={i}>
            {i === 4 && (
              <span style={{
                fontSize: 20,
                fontWeight: 700,
                color: 'var(--text-secondary)',
                lineHeight: 1,
                userSelect: 'none',
                marginBottom: 2,
              }}>·</span>
            )}
            <input
              ref={el => { inputRefs.current[i] = el; }}
              type="text"
              inputMode="text"
              autoComplete="off"
              autoCorrect="off"
              autoCapitalize="characters"
              spellCheck={false}
              maxLength={2}  // allow 2 for composition; we trim in handler
              value={chars[i]}
              disabled={waiting}
              style={boxStyle(!!chars[i])}
              onFocus={e => e.target.select()}
              onChange={e => handleInput(i, e)}
              onKeyDown={e => handleKeyDown(i, e)}
              onPaste={handlePaste}
            />
          </React.Fragment>
        ))}
      </div>

      {verifyId && (
        <div className="glass glass-xs" style={{
          padding: '10px 16px',
          marginBottom: 12,
          fontSize: 13,
        }}>
          <div style={{ color: 'var(--text-secondary)', marginBottom: 4 }}>Verification ID</div>
          <div style={{ fontSize: 20, fontWeight: 700, fontFamily: 'monospace', color: 'var(--accent)', letterSpacing: '0.15em' }}>
            {verifyId}
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginTop: 4 }}>
            Confirm this matches the code on your PC
          </div>
        </div>
      )}

      {status && (
        <div style={{
          color: waiting && !status.includes('failed') ? 'var(--text-secondary)' : 'var(--danger)',
          fontSize: 12,
          marginBottom: 10,
        }}>
          {status}
        </div>
      )}

      <button
        className="glass-btn accent full-width"
        style={{ fontSize: 14 }}
        onClick={() => handleSubmit(getCode())}
        disabled={getCode().length !== 8 || chars.includes('') || waiting}
      >
        {waiting ? 'Waiting for approval…' : 'Connect'}
      </button>
    </div>
  );
};
