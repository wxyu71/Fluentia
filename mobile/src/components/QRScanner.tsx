import React, { useEffect, useRef, useState, useCallback } from 'react';
import { Html5Qrcode } from 'html5-qrcode';
import { ScanIcon, CloseIcon, KeyboardIcon } from './Icons';
import type { ConnectionInfo } from '../types';

interface QRScannerProps {
  onScan: (info: ConnectionInfo) => void;
  deviceId?: string;
  overlay?: boolean;    // true = modal overlay, false = full page
  onClose?: () => void; // required when overlay=true
}

export const QRScanner: React.FC<QRScannerProps> = ({ onScan, deviceId: _deviceId = 'unknown', overlay = false, onClose }) => {
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
              const wsUrl = parts.length >= 4 ? parts[3]
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
    return () => {
      const scanner = scannerRef.current;
      if (scanner) {
        scanner.stop().catch(() => {}).finally(() => {
          scannerRef.current = null;
          setScanning(false);
        });
      }
    };
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
                  <button className="glass-btn accent" onClick={() => { void startScanner(); }}>
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
                  <button className="glass-btn" style={{ fontSize: 13, padding: '8px 16px' }} onClick={() => { void startScanner(); }}>
                    Retry
                  </button>
                </div>
              </div>
            )}

            {scanning && (
              <div style={{ marginTop: 12 }}>
                <button className="glass-btn danger" style={{ fontSize: 13, padding: '8px 16px' }} onClick={() => { void stopScanner(); }}>
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
            onClick={() => { setManualMode(!manualMode); if (scanning) void stopScanner(); }}
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
        <button className="scan-overlay-close" onClick={() => { void stopScanner(); onClose?.(); }}>
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
  const timeoutRef = useRef<number | null>(null);
  const [chars, setChars] = useState<string[]>(Array(8).fill(''));
  const [status, setStatus] = useState('');
  const [verifyId, setVerifyId] = useState('');
  const [waiting, setWaiting] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);
  const handoffPendingRef = useRef(false);
  const inputRefs = useRef<(HTMLInputElement | null)[]>(Array(8).fill(null));

  const getCode = useCallback(() => chars.join(''), [chars]);

  const focusBox = (idx: number) => {
    if (idx >= 0 && idx < 8) inputRefs.current[idx]?.focus();
  };

  const cancelPending = useCallback(() => {
    handoffPendingRef.current = false;
    if (timeoutRef.current !== null) {
      window.clearTimeout(timeoutRef.current);
      timeoutRef.current = null;
    }
    wsRef.current?.close();
    wsRef.current = null;
    setWaiting(false);
    setVerifyId('');
    setStatus('');
  }, []);

  const handleSubmit = useCallback((code: string) => {
    if (code.length !== 8 || waiting) return;
    setStatus('Connecting');
    setWaiting(true);

    const wsUrl = window.location.origin.replace(/^http/, 'ws') + '/ws';
    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;
    timeoutRef.current = window.setTimeout(() => {
      handoffPendingRef.current = false;
      setStatus('Connection failed. Check your network.');
      setWaiting(false);
      ws.close();
      timeoutRef.current = null;
    }, 8000);

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
          setStatus('Waiting for approval on your PC');
        } else if (msg.type === 'joined' && msg.approved) {
          if (timeoutRef.current !== null) {
            window.clearTimeout(timeoutRef.current);
            timeoutRef.current = null;
          }
          handoffPendingRef.current = true;
          const info: ConnectionInfo = { s: wsUrl, t: msg.token, k: msg.publicKey || '' };
          onConnect(info);
        } else if (msg.type === 'error') {
          if (timeoutRef.current !== null) {
            window.clearTimeout(timeoutRef.current);
            timeoutRef.current = null;
          }
          handoffPendingRef.current = false;
          setStatus(msg.error || 'Connection failed');
          setWaiting(false);
          ws.close();
        }
      } catch {
        // Ignore malformed messages.
      }
    };

    ws.onerror = () => {
      if (timeoutRef.current !== null) {
        window.clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
      handoffPendingRef.current = false;
      setStatus('Connection failed');
      setWaiting(false);
    };

    ws.onclose = () => {
      if (timeoutRef.current !== null && !handoffPendingRef.current) {
        window.clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
      if (!handoffPendingRef.current) {
        setWaiting(false);
      }
      wsRef.current = null;
    };
  }, [onConnect, waiting]);

  const handleKeyDown = useCallback((idx: number, e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Backspace') {
      e.preventDefault();
      if (chars[idx]) {
        setChars((prev) => {
          const next = [...prev];
          next[idx] = '';
          return next;
        });
      } else if (idx > 0) {
        setChars((prev) => {
          const next = [...prev];
          next[idx - 1] = '';
          return next;
        });
        focusBox(idx - 1);
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
  }, [chars, getCode, handleSubmit]);

  const handleInput = useCallback((idx: number, e: React.ChangeEvent<HTMLInputElement>) => {
    const raw = e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, '');
    if (!raw) return;

    if (raw.length > 1) {
      const newChars = [...chars];
      let pos = idx;
      for (const ch of raw) {
        if (pos >= 8) break;
        newChars[pos] = ch;
        pos += 1;
      }
      setChars(newChars);
      focusBox(Math.min(pos, 7));

      const pastedCode = newChars.join('');
      if (!newChars.includes('')) {
        window.setTimeout(() => handleSubmit(pastedCode), 80);
      }
      return;
    }

    const newChars = [...chars];
    newChars[idx] = raw[0];
    setChars(newChars);
    if (idx < 7) focusBox(idx + 1);

    const fullCode = newChars.join('');
    if (!newChars.includes('')) {
      window.setTimeout(() => handleSubmit(fullCode), 80);
    }
  }, [chars, handleSubmit]);

  const handlePaste = useCallback((e: React.ClipboardEvent<HTMLInputElement>) => {
    e.preventDefault();
    const pasted = e.clipboardData.getData('text').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8);
    const newChars = Array.from({ length: 8 }, (_, index) => pasted[index] || '');
    setChars(newChars);
    const nextEmpty = newChars.findIndex((char) => !char);
    focusBox(nextEmpty === -1 ? 7 : nextEmpty);
    if (pasted.length === 8) {
      window.setTimeout(() => handleSubmit(pasted), 80);
    }
  }, [handleSubmit]);

  useEffect(() => () => {
    const ws = wsRef.current;
    if (!ws) {
      return;
    }

    if (handoffPendingRef.current) {
      window.setTimeout(() => {
        try {
          ws.close();
        } catch {
          // Ignore close errors after handoff.
        }
      }, 5000);
      return;
    }

    ws.close();
  }, []);

  const boxStyle = (filled: boolean): React.CSSProperties => ({
    width: '100%',
    minWidth: 0,
    height: 'clamp(42px, 10vw, 50px)',
    borderRadius: 12,
    background: filled ? 'var(--glass-bg, rgba(255,255,255,0.12))' : 'rgba(255,255,255,0.06)',
    border: `1.5px solid ${filled ? 'var(--accent)' : 'var(--separator)'}`,
    color: 'var(--text-primary)',
    fontSize: 'clamp(18px, 5vw, 21px)',
    fontWeight: 700,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    textAlign: 'center',
    outline: 'none',
    caretColor: 'transparent',
    transition: 'border-color 0.15s ease, background 0.15s ease',
    WebkitAppearance: 'none',
    padding: 0,
  });

  const groupStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: 'clamp(4px, 1.8vw, 8px)',
    flex: '1 1 0',
    minWidth: 0,
  };

  const renderSlot = (index: number) => (
    <input
      key={index}
      ref={(el) => { inputRefs.current[index] = el; }}
      type="text"
      inputMode="text"
      autoComplete="off"
      autoCorrect="off"
      autoCapitalize="characters"
      spellCheck={false}
      maxLength={2}
      value={chars[index]}
      disabled={waiting}
      style={boxStyle(!!chars[index])}
      onFocus={(e) => e.target.select()}
      onChange={(e) => handleInput(index, e)}
      onKeyDown={(e) => handleKeyDown(index, e)}
      onPaste={handlePaste}
    />
  );

  return (
    <div style={{ textAlign: 'center' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, marginBottom: 12 }}>
        <KeyboardIcon size={18} color="var(--accent)" />
        <span style={{ fontSize: 14, fontWeight: 600 }}>Enter Device Code</span>
      </div>
      <p style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>
        Enter the 8-character code shown on your PC
      </p>

      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 'clamp(6px, 2vw, 12px)',
        width: 'min(100%, 360px)',
        margin: '0 auto 16px',
      }}>
        <div style={groupStyle}>
          {[0, 1, 2, 3].map(renderSlot)}
        </div>
        <span style={{
          fontSize: 18,
          fontWeight: 700,
          color: 'var(--text-secondary)',
          userSelect: 'none',
          flexShrink: 0,
        }}>
          ·
        </span>
        <div style={groupStyle}>
          {[4, 5, 6, 7].map(renderSlot)}
        </div>
      </div>

      {verifyId && (
        <div className="glass glass-xs" style={{
          padding: '10px 16px',
          marginBottom: 12,
          fontSize: 13,
        }}>
          <div style={{ color: 'var(--text-secondary)', marginBottom: 4 }}>Verification ID</div>
          <div style={{ fontSize: 20, fontWeight: 700, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace', color: 'var(--accent)', letterSpacing: '0.15em' }}>
            {verifyId}
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginTop: 4 }}>
            Confirm this matches the code on your PC
          </div>
        </div>
      )}

      {status && (
        <div style={{
          color: waiting && !status.toLowerCase().includes('failed') ? 'var(--text-secondary)' : 'var(--danger)',
          fontSize: 12,
          marginBottom: 10,
        }}>
          {status}
        </div>
      )}

      <div style={{ display: 'flex', gap: 10 }}>
        <button
          className="glass-btn accent full-width"
          style={{ fontSize: 14 }}
          onClick={() => handleSubmit(getCode())}
          disabled={getCode().length !== 8 || chars.includes('') || waiting}
        >
          {waiting ? 'Waiting for approval' : 'Connect'}
        </button>
        {waiting && (
          <button
            className="glass-btn"
            style={{ fontSize: 14, paddingInline: 16 }}
            onClick={cancelPending}
          >
            Cancel
          </button>
        )}
      </div>
    </div>
  );
};
