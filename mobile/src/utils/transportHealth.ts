/**
 * TransportHealthMonitor: evaluates BLE and WebSocket health scores
 * and selects the best transport for each message type.
 *
 * Design principles:
 * - One encrypt call, one ciphertext, route to best channel
 * - Input messages prefer BLE (low latency) when healthy
 * - File transfer always prefers WS (high bandwidth)
 * - Handshake messages use a single reliable transport
 * - Low battery favors BLE (lower power consumption than WS/Wi-Fi)
 */

export type TransportKind = 'ble' | 'ws';
export type MessageType = 'input' | 'file' | 'handshake' | 'control';

export interface TransportScore {
  ble: number;   // 0-100
  ws: number;    // 0-100
}

export interface TransportHealthConfig {
  /** BLE consecutive failures before marking down */
  bleFailureThreshold: number;
  /** BLE RSSI below this marks degraded */
  bleRssiDegradedThreshold: number;
  /** WS heartbeat RTT above this (ms) marks degraded */
  wsRttDegradedMs: number;
  /** Battery level below this disables concurrent, prefers BLE */
  batteryLowThreshold: number;
  /** Battery level below this disables WS entirely */
  batteryCriticalThreshold: number;
  /** Recovery check interval (ms) */
  recoveryIntervalMs: number;
}

const DEFAULT_CONFIG: TransportHealthConfig = {
  bleFailureThreshold: 3,
  bleRssiDegradedThreshold: -80,
  wsRttDegradedMs: 1000,
  batteryLowThreshold: 0.20,
  batteryCriticalThreshold: 0.10,
  recoveryIntervalMs: 30000,
};

export class TransportHealthMonitor {
  private bleScore = 50;
  private wsScore = 50;
  private bleConsecutiveFailures = 0;
  private bleRssi = -50; // default assume decent signal
  private wsRtt = 100;   // default assume decent latency
  private wsConnected = false;
  private batteryLevel = 1.0;
  private batteryCharging = true;
  private config: TransportHealthConfig;
  private recoveryTimer: ReturnType<typeof setInterval> | null = null;

  // Track down state for recovery
  private bleDown = false;
  private wsDown = false;
  private bleDownSince = 0;
  private wsDownSince = 0;

  constructor(config?: Partial<TransportHealthConfig>) {
    this.config = { ...DEFAULT_CONFIG, ...config };
    this.startRecoveryTimer();
  }

  // --- BLE health updates ---

  /** Call when a BLE send succeeds (also used for recovery) */
  onBleSuccess(): void {
    this.bleConsecutiveFailures = 0;
    this.bleDown = false;
    this.bleDownSince = 0;
    this.recalculateBleScore();
  }

  /** Call when a BLE send fails */
  onBleFailure(): void {
    this.bleConsecutiveFailures++;
    if (this.bleConsecutiveFailures >= this.config.bleFailureThreshold) {
      this.bleDown = true;
      this.bleDownSince = Date.now();
      this.bleScore = 0;
    } else {
      this.recalculateBleScore();
    }
  }

  /** Update BLE RSSI signal strength */
  updateBleRssi(rssi: number): void {
    this.bleRssi = rssi;
    this.recalculateBleScore();
  }

  // --- WS health updates ---

  /** Call when WS heartbeat pong is received */
  updateWsRtt(rttMs: number): void {
    this.wsRtt = rttMs;
    this.wsConnected = true;
    this.wsDown = false;
    this.recalculateWsScore();
  }

  /** Call when WS connection state changes */
  setWsConnected(connected: boolean): void {
    this.wsConnected = connected;
    if (!connected) {
      this.wsDown = true;
      this.wsDownSince = Date.now();
      this.wsScore = 0;
    } else {
      this.wsDown = false;
      this.recalculateWsScore();
    }
  }

  // --- Battery ---

  /** Update battery status */
  updateBattery(level: number, charging: boolean): void {
    this.batteryLevel = Math.max(0, Math.min(1, level));
    this.batteryCharging = charging;
    this.recalculateBleScore();
    this.recalculateWsScore();
  }

  // --- Routing ---

  /** Get current scores */
  getScores(): TransportScore {
    return { ble: this.bleScore, ws: this.wsScore };
  }

  /** Check if a transport is available */
  isTransportAvailable(kind: TransportKind): boolean {
    if (kind === 'ble') return !this.bleDown && this.bleScore > 0;
    return !this.wsDown && this.wsScore > 0;
  }

  /**
   * Select the best transport for a given message type.
   * Returns null if no transport is available.
   */
  selectTransport(messageType: MessageType): TransportKind | null {
    const bleAvail = this.isTransportAvailable('ble');
    const wsAvail = this.isTransportAvailable('ws');

    if (!bleAvail && !wsAvail) return null;
    if (!bleAvail) return 'ws';
    if (!wsAvail) return 'ble';

    // Critical battery: prefer BLE (lower power)
    if (this.batteryLevel < this.config.batteryCriticalThreshold && !this.batteryCharging) {
      return 'ble';
    }

    switch (messageType) {
      case 'input':
        // Input messages: prefer BLE for low latency
        // But if BLE score is degraded, use WS
        if (this.bleScore >= 40) return 'ble';
        return 'ws';

      case 'file':
        // File transfer: always prefer WS (bandwidth)
        if (this.wsScore >= 40) return 'ws';
        return 'ble'; // fallback, will be slow

      case 'handshake':
        // Handshake: use the most reliable single transport
        return this.bleScore > this.wsScore ? 'ble' : 'ws';

      case 'control':
        // Control messages: use the higher-scored transport
        return this.bleScore > this.wsScore ? 'ble' : 'ws';

      default:
        return 'ws';
    }
  }

  /** Check if concurrent sending is allowed (both channels healthy, battery OK) */
  isConcurrentAllowed(): boolean {
    if (this.batteryCharging) {
      return this.bleScore >= 70 && this.wsScore >= 70;
    }
    // Low battery: no concurrent
    if (this.batteryLevel < this.config.batteryLowThreshold) return false;
    return this.bleScore >= 70 && this.wsScore >= 70;
  }

  /** Force mark a transport as down (e.g., BLE disconnect) */
  markDown(kind: TransportKind): void {
    if (kind === 'ble') {
      this.bleDown = true;
      this.bleDownSince = Date.now();
      this.bleScore = 0;
    } else {
      this.wsDown = true;
      this.wsDownSince = Date.now();
      this.wsScore = 0;
    }
  }

  /** Clean up timers */
  dispose(): void {
    if (this.recoveryTimer) {
      clearInterval(this.recoveryTimer);
      this.recoveryTimer = null;
    }
  }

  // --- Private ---

  private recalculateBleScore(): void {
    if (this.bleDown) {
      this.bleScore = 0;
      return;
    }

    let score = 50; // baseline

    // RSSI factor: -30 (excellent) to -90 (unusable)
    if (this.bleRssi >= -50) score += 20;
    else if (this.bleRssi >= -65) score += 10;
    else if (this.bleRssi >= -80) score -= 10;
    else score -= 30;

    // Consecutive failures factor
    score -= this.bleConsecutiveFailures * 15;

    // Battery factor: BLE is low power, so less penalty
    if (this.batteryLevel < this.config.batteryCriticalThreshold && !this.batteryCharging) {
      score -= 10; // small penalty, but BLE is still preferred over WS
    }

    this.bleScore = Math.max(0, Math.min(100, score));
  }

  private recalculateWsScore(): void {
    if (this.wsDown || !this.wsConnected) {
      this.wsScore = 0;
      return;
    }

    let score = 60; // baseline (WS is generally more reliable)

    // RTT factor
    if (this.wsRtt < 100) score += 20;
    else if (this.wsRtt < 300) score += 10;
    else if (this.wsRtt < this.config.wsRttDegradedMs) score += 0;
    else score -= 20;

    // Battery factor: WS/Wi-Fi uses more power
    if (!this.batteryCharging) {
      if (this.batteryLevel < this.config.batteryCriticalThreshold) {
        score -= 40; // heavy penalty, prefer BLE
      } else if (this.batteryLevel < this.config.batteryLowThreshold) {
        score -= 15;
      }
    }

    this.wsScore = Math.max(0, Math.min(100, score));
  }

  private startRecoveryTimer(): void {
    this.recoveryTimer = setInterval(() => {
      // Attempt recovery for down transports
      if (this.bleDown && Date.now() - this.bleDownSince > this.config.recoveryIntervalMs) {
        this.bleDown = false;
        this.bleConsecutiveFailures = 0;
        this.recalculateBleScore();
      }
      if (this.wsDown && Date.now() - this.wsDownSince > this.config.recoveryIntervalMs) {
        this.wsDown = false;
        this.recalculateWsScore();
      }
    }, this.config.recoveryIntervalMs);
  }
}
