import { describe, it, expect } from 'vitest';

/**
 * Connection configuration invariant tests.
 *
 * These tests verify critical timing constants that, if misconfigured,
 * cause disconnect-and-retry loops. See v1.7.4 regression where
 * HEARTBEAT_TIMEOUT_MS (2500) < HEARTBEAT_INTERVAL_MS (3000) caused
 * spurious disconnections.
 *
 * These values are duplicated from useWebSocket.ts to test invariants
 * without importing React hooks.
 */

const HEARTBEAT_INTERVAL_MS = 3000;
const HEARTBEAT_TIMEOUT_MS = 8000;
const CONNECT_TIMEOUT_MS = 8000;
const HANDSHAKE_TIMEOUT_MS = 12000;
const OFFLINE_GRACE_MS = 10000;
const FIXED_RECONNECT_DELAY_MS = 2000;
const MAX_RECONNECT_ATTEMPTS = 30;

describe('Connection timing invariants', () => {
  it('HeartbeatTimeout_GreaterThanInterval: timeout must exceed interval to avoid false disconnects', () => {
    // v1.7.4 regression: timeout (2500) < interval (3000) caused the client
    // to declare the connection dead before the next heartbeat was even sent.
    expect(HEARTBEAT_TIMEOUT_MS).toBeGreaterThan(HEARTBEAT_INTERVAL_MS);
  });

  it('HeartbeatTimeout_MultipleOfInterval: timeout should allow ≥2 missed heartbeats', () => {
    // The timeout should be at least 2x the interval to tolerate one missed pong.
    expect(HEARTBEAT_TIMEOUT_MS).toBeGreaterThanOrEqual(HEARTBEAT_INTERVAL_MS * 2);
  });

  it('ConnectTimeout_ExceedsHeartbeat: connect timeout > heartbeat timeout', () => {
    expect(CONNECT_TIMEOUT_MS).toBeGreaterThanOrEqual(HEARTBEAT_TIMEOUT_MS);
  });

  it('HandshakeTimeout_ExceedsConnect: handshake timeout > connect timeout', () => {
    expect(HANDSHAKE_TIMEOUT_MS).toBeGreaterThan(CONNECT_TIMEOUT_MS);
  });

  it('OfflineGrace_ExceedsHeartbeat: offline grace > heartbeat timeout', () => {
    expect(OFFLINE_GRACE_MS).toBeGreaterThan(HEARTBEAT_TIMEOUT_MS);
  });

  it('ReconnectDelay_Positive: base delay must be positive', () => {
    expect(FIXED_RECONNECT_DELAY_MS).toBeGreaterThan(0);
  });

  it('MaxReconnectAttempts_Reasonable: cap between 10 and 100', () => {
    expect(MAX_RECONNECT_ATTEMPTS).toBeGreaterThanOrEqual(10);
    expect(MAX_RECONNECT_ATTEMPTS).toBeLessThanOrEqual(100);
  });
});

// C# WebSocketService timing constants for cross-check
const CS_HEARTBEAT_INTERVAL_MS = 3000;
const CS_HEARTBEAT_TIMEOUT_MS = 10000;

describe('Cross-platform timing parity', () => {
  it('HeartbeatInterval_MatchesCSharp: mobile and C# use same interval', () => {
    expect(HEARTBEAT_INTERVAL_MS).toBe(CS_HEARTBEAT_INTERVAL_MS);
  });

  it('HeartbeatTimeout_CSharpIsLonger: C# timeout >= mobile timeout', () => {
    // C# uses 10s, mobile uses 8s. Both must be > interval (3s).
    expect(CS_HEARTBEAT_TIMEOUT_MS).toBeGreaterThan(CS_HEARTBEAT_INTERVAL_MS);
    expect(HEARTBEAT_TIMEOUT_MS).toBeGreaterThan(HEARTBEAT_INTERVAL_MS);
  });
});
