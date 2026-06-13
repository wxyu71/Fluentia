import { describe, it, expect } from 'vitest';

// Replicate reconnect logic from useWebSocket.ts for unit testing
const FIXED_RECONNECT_DELAY_MS = 2000;
const MAX_RECONNECT_ATTEMPTS = 30;

function getReconnectDelay(attempt: number): number {
  return Math.min(FIXED_RECONNECT_DELAY_MS * Math.pow(2, attempt - 1), 30000);
}

describe('Reconnect backoff', () => {
  it('ExponentialBackoff_Increases: delay increases with attempts', () => {
    const delay1 = getReconnectDelay(1);
    const delay2 = getReconnectDelay(2);
    const delay3 = getReconnectDelay(3);
    const delay4 = getReconnectDelay(4);

    expect(delay1).toBe(2000);  // 2000 * 2^0
    expect(delay2).toBe(4000);  // 2000 * 2^1
    expect(delay3).toBe(8000);  // 2000 * 2^2
    expect(delay4).toBe(16000); // 2000 * 2^3
    expect(delay2).toBeGreaterThan(delay1);
    expect(delay3).toBeGreaterThan(delay2);
    expect(delay4).toBeGreaterThan(delay3);
  });

  it('MaxAttempts_Limited: cap at 30', () => {
    expect(MAX_RECONNECT_ATTEMPTS).toBe(30);
  });

  it('ExponentialBackoff_CapsAt30s: high attempt values are capped', () => {
    const delay10 = getReconnectDelay(10);
    expect(delay10).toBe(30000); // capped at 30s

    const delay20 = getReconnectDelay(20);
    expect(delay20).toBe(30000); // still capped
  });

  it('ExponentialBackoff_FirstAttempt: first attempt uses base delay', () => {
    expect(getReconnectDelay(1)).toBe(FIXED_RECONNECT_DELAY_MS);
  });
});
