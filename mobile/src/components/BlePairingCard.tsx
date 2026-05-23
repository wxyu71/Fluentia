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
  return (
    <div className="glass" style={{ padding: 18, marginBottom: 16, width: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
        <BluetoothIcon size={18} color="var(--accent)" />
        <h3 style={{ fontSize: 15, fontWeight: 600 }}>Bluetooth Pairing</h3>
      </div>
      <div style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 12 }}>
        {isSupported
          ? 'Try nearby BLE pairing before falling back to QR or device code.'
          : 'This browser does not support Web Bluetooth, so BLE pairing is unavailable here.'}
      </div>

      <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 10 }}>
        Status: {status}{deviceName ? ` · ${deviceName}` : ''}{isSupported ? ` · Adapter ${isAvailable ? 'available' : 'unavailable'}` : ''}
      </div>

      {verificationCode && (
        <div style={{
          marginBottom: 10,
          padding: '10px 14px',
          borderRadius: 12,
          background: 'rgba(255,255,255,0.06)',
          fontSize: 13,
          color: 'var(--text-primary)',
        }}>
          Compare code with PC: <strong>{verificationCode}</strong>
        </div>
      )}

      {error && (
        <div style={{ marginBottom: 10, fontSize: 12, color: 'var(--danger)' }}>
          {error}
        </div>
      )}

      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button className="glass-btn accent" disabled={!isSupported || isConnecting} onClick={onRequestPairing}>
          <BluetoothIcon size={16} color="white" /> {isConnecting ? 'Connecting...' : 'Pair via BLE'}
        </button>
        <button className="glass-btn" disabled={!deviceName} onClick={onDisconnect}>
          Disconnect BLE
        </button>
      </div>
    </div>
  );
};