/**
 * Debug logger for diagnosing text input synchronization issues.
 *
 * Strict on/off semantics: when disabled, Log() is a single boolean
 * check with zero allocations, no string formatting, no array pushes.
 *
 * Logs are kept in a ring buffer (max 2000 entries) and can be
 * downloaded as a text file from the settings UI.
 */

const STORAGE_KEY = 'fluentia_debug_logging';
const MAX_ENTRIES = 2000;

let _enabled = false;
let _buffer: string[] = [];

/** Restore enabled state from localStorage on module load. */
try {
  _enabled = localStorage.getItem(STORAGE_KEY) === '1';
} catch {
  _enabled = false;
}

export const debugLog = {
  /** Whether debug logging is active. */
  get enabled(): boolean {
    return _enabled;
  },

  set enabled(value: boolean) {
    _enabled = value;
    try {
      if (value) {
        localStorage.setItem(STORAGE_KEY, '1');
      } else {
        localStorage.removeItem(STORAGE_KEY);
      }
    } catch {
      // Storage failures are non-critical.
    }
  },

  /**
   * Write a timestamped entry to the ring buffer.
   * Returns immediately (zero cost) when not enabled.
   */
  log(message: string): void {
    if (!_enabled) return;

    const now = new Date();
    const ts = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}.${pad3(now.getMilliseconds())}`;
    _buffer.push(`[${ts}] ${message}`);
    if (_buffer.length > MAX_ENTRIES) {
      _buffer = _buffer.slice(-MAX_ENTRIES);
    }
  },

  /** Get all buffered log entries as a single string. */
  dump(): string {
    return _buffer.join('\n');
  },

  /** Clear the buffer. */
  clear(): void {
    _buffer = [];
  },

  /** Number of entries in the buffer. */
  get size(): number {
    return _buffer.length;
  },

  /** Download the log as a text file. */
  download(): void {
    if (_buffer.length === 0) return;
    const blob = new Blob([_buffer.join('\n')], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `fluentia-debug-${Date.now().toString(36)}.log`;
    a.rel = 'noopener';
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    a.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1500);
  },
};

function pad(n: number): string {
  return n < 10 ? `0${n}` : `${n}`;
}

function pad3(n: number): string {
  if (n < 10) return `00${n}`;
  if (n < 100) return `0${n}`;
  return `${n}`;
}
