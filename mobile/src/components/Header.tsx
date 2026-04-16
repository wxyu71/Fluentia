import React from 'react';
import type { ConnectionState } from '../types';

interface HeaderProps {
  connectionState: ConnectionState;
  peerConnected: boolean;
  encryptionReady: boolean;
}

export const Header: React.FC<HeaderProps> = ({ connectionState, peerConnected, encryptionReady }) => {
  const statusText = (): string => {
    switch (connectionState) {
      case 'connected':
        if (encryptionReady && peerConnected) return 'Encrypted';
        if (peerConnected) return 'Key Exchange...';
        return 'Waiting for PC';
      case 'connecting': return 'Connecting...';
      case 'preempted': return 'Preempted';
      default: return 'Not Connected';
    }
  };

  const dotClass = (): string => {
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
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <h1 style={{
          fontSize: 22,
          fontWeight: 700,
          letterSpacing: '-0.025em',
          color: 'var(--text-primary)',
        }}>
          Fluentia
        </h1>
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
          <span style={{ fontSize: 11 }}>🔒</span>
        )}
      </div>
    </header>
  );
};
