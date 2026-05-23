import React from 'react';
import { BluetoothIcon } from './Icons';

interface BlePairingCardProps {
  isSupported: boolean;
  isAvailable: boolean;
  isConnecting: boolean;
  status: string;
  error: string | null;
  deviceName: string | null;
  verificationCode: string | null;
  onRequestPairing: () => void;
  onDisconnect: () => void;
}

export const BlePairingCard: React.FC<BlePairingCardProps> = ({
  isSupported,
  isAvailable,
  isConnecting,
  status,
  error,
  deviceName,
  verificationCode,
  onRequestPairing,
  onDisconnect,
}) => {
  const statusLine = [status, deviceName].filter(Boolean).join(' · ');

  return (
    <div className="glass glass-xs" style={{ padding: '12px 14px', marginBottom: 12, width: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8, marginBottom: 6 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
          <BluetoothIcon size={16} color="var(--accent)" />
          <h3 style={{ fontSize: 13, fontWeight: 600, margin: 0 }}>BLE</h3>
        </div>
        <span style={{ fontSize: 11, color: 'var(--text-secondary)', flexShrink: 0 }}>
          {isAvailable ? 'Nearby' : 'Off'}
        </span>
      </div>

      <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 10, lineHeight: 1.35 }}>
        {isSupported
          ? 'Optional nearby fallback after QR pairing.'
          : 'Web Bluetooth is unavailable in this browser.'}
      </div>

      <div style={{ fontSize: 11, color: 'var(--text-secondary)', marginBottom: 10, lineHeight: 1.35 }}>
        {statusLine}
      </div>

      {verificationCode && (
        <div style={{
          marginBottom: 10,
          padding: '8px 10px',
          borderRadius: 10,
          background: 'rgba(255,255,255,0.06)',
          fontSize: 12,
          color: 'var(--text-primary)',
        }}>
          Code <strong>{verificationCode}</strong>
        </div>
      )}

      {error && (
        <div style={{ marginBottom: 10, fontSize: 11, color: 'var(--danger)', lineHeight: 1.35 }}>
          {error}
        </div>
      )}

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button className="glass-btn accent" disabled={!isSupported || isConnecting} onClick={onRequestPairing} style={{ fontSize: 12, padding: '8px 12px' }}>
          <BluetoothIcon size={14} color="white" /> {isConnecting ? 'Connecting...' : 'Pair BLE'}
        </button>
        <button className="glass-btn" disabled={!deviceName} onClick={onDisconnect} style={{ fontSize: 12, padding: '8px 12px' }}>
          Disconnect
        </button>
      </div>
    </div>
  );
};