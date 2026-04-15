import React, { useEffect, useRef, useState } from 'react';
import { Html5Qrcode } from 'html5-qrcode';
import type { ConnectionInfo } from '../types';

interface QRScannerProps {
  onScan: (info: ConnectionInfo) => void;
}

export const QRScanner: React.FC<QRScannerProps> = ({ onScan }) => {
  const scannerRef = useRef<Html5Qrcode | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);

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
            const data = JSON.parse(decodedText) as ConnectionInfo;
            if (data.s && data.t && data.k) {
              scanner.stop().catch(() => {});
              scannerRef.current = null;
              setScanning(false);
              onScan(data);
            } else {
              setError('Invalid QR code format');
            }
          } catch {
            setError('QR code is not valid JSON');
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
      stopScanner();
    };
  }, []);

  return (
    <div className="fade-in" style={{ display: 'flex', flexDirection: 'column', gap: 16, padding: '0 20px' }}>
      <div className="glass" style={{
        padding: 20,
        textAlign: 'center',
      }}>
        <h3 style={{ fontSize: 16, fontWeight: 600, marginBottom: 8 }}>
          Scan PC QR Code
        </h3>
        <p style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>
          Open Fluentia on your PC and scan the QR code to connect
        </p>

        {/* Scanner viewport */}
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
                📷 Start Camera
              </button>
            </div>
          )}
        </div>

        {error && (
          <div style={{
            marginTop: 12,
            padding: '10px 16px',
            background: 'rgba(239, 68, 68, 0.15)',
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
      </div>

      {/* Manual input fallback */}
      <ManualConnect onConnect={onScan} />
    </div>
  );
};

const ManualConnect: React.FC<{ onConnect: (info: ConnectionInfo) => void }> = ({ onConnect }) => {
  const [jsonInput, setJsonInput] = useState('');
  const [error, setError] = useState('');

  const handleConnect = () => {
    setError('');
    try {
      const data = JSON.parse(jsonInput) as ConnectionInfo;
      if (data.s && data.t && data.k) {
        onConnect(data);
      } else {
        setError('Missing required fields: s, t, k');
      }
    } catch {
      setError('Invalid JSON');
    }
  };

  return (
    <div className="glass" style={{ padding: 16 }}>
      <h4 style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 8 }}>
        Manual Connection
      </h4>
      <input
        className="glass-input"
        value={jsonInput}
        onChange={(e) => setJsonInput(e.target.value)}
        placeholder='Paste QR data JSON here...'
        style={{ fontSize: 13, marginBottom: 8 }}
      />
      {error && <div style={{ color: 'var(--danger)', fontSize: 12, marginBottom: 8 }}>{error}</div>}
      <button className="glass-btn full-width" style={{ fontSize: 13 }} onClick={handleConnect} disabled={!jsonInput}>
        Connect
      </button>
    </div>
  );
};
