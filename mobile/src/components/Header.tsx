import React from 'react';
import { BluetoothIcon, LockIcon, WifiOffIcon } from './Icons';
import type { ConnectionState } from '../types';

const APP_VERSION = __APP_VERSION__;

interface HeaderProps {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
  pendingStatus?: string | null;
  transportSummary?: string;
  showBluetoothIndicator?: boolean;
  bleTransportReady?: boolean;
  wsDisconnected?: boolean;
}

export const Header: React.FC<HeaderProps> = ({ connectionState, peerConnected, encryptionReady, pendingStatus, transportSummary, showBluetoothIndicator, bleTransportReady, wsDisconnected }) => {
  const bleOnly = wsDisconnected && bleTransportReady && encryptionReady;

  const statusText = (): string => {
    if (bleOnly) return 'BLE only';
    if (pendingStatus && connectionState !== 'preempted' && !encryptionReady) {
      return pendingStatus;
    }

    switch (connectionState) {
      case 'connected':
        if (encryptionReady && peerConnected) return 'Encrypted';
        if (peerConnected) return 'Key exchange';
        return 'Waiting for PC';
      case 'connecting': return 'Connecting';
      case 'preempted': return 'Preempted';
      default: return 'Not Connected';
    }
  };

  const dotClass = (): string => {
    if (bleOnly) return 'ble-only';
    if (connectionState === 'connected' && encryptionReady && peerConnected) return 'connected';
    if (connectionState === 'connecting') return 'connecting';
    if (connectionState === 'preempted') return 'preempted';
    if (connectionState === 'connected') return 'connecting';
    return 'disconnected';
  };

  return (
    <header style={{
      padding: '16px 20px',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
    }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h1 style={{
            fontSize: 22,
            fontWeight: 700,
            letterSpacing: '-0.025em',
            color: 'var(--text-primary)',
          }}>
            Fluentia
          </h1>
          <span style={{
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.08em',
            color: 'var(--accent)',
          }}>
            v{APP_VERSION}
          </span>
        </div>
        <span style={{
          fontSize: 10,
          color: 'var(--text-secondary)',
          letterSpacing: '0.03em',
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          lineHeight: 1.2,
        }}>
          {showBluetoothIndicator && <BluetoothIcon size={12} color="var(--accent)" />}
          {transportSummary ?? 'WS + BLE available'}
        </span>
      </div>

      <div className="glass glass-xs" style={{
        padding: '6px 14px',
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        fontSize: 12,
        fontWeight: 500,
      }}>
        <span className={`status-dot ${dotClass()}`} />
        <span style={{ color: 'var(--text-secondary)' }}>{statusText()}</span>
        {encryptionReady && peerConnected && (
          <>
            <LockIcon size={12} color="var(--success)" />
            {bleTransportReady && <BluetoothIcon size={12} color="var(--accent)" />}
          </>
        )}
        {bleOnly && <WifiOffIcon size={12} color="var(--warning)" />}
      </div>
    </header>
  );
};
