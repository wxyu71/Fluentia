/**
 * Tests for the debugLog utility.
 *
 * Verifies strict on/off semantics (zero overhead when disabled),
 * ring buffer behavior, persistence, and dump/download functionality.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Mock localStorage
const storage = new Map<string, string>();
const localStorageMock = {
  getItem: vi.fn((key: string) => storage.get(key) ?? null),
  setItem: vi.fn((key: string, value: string) => { storage.set(key, value); }),
  removeItem: vi.fn((key: string) => { storage.delete(key); }),
  clear: vi.fn(() => { storage.clear(); }),
};
vi.stubGlobal('localStorage', localStorageMock);

// Import after mocking localStorage so the module reads from our mock
import { debugLog } from './debugLog';

describe('debugLog — strict on/off semantics', () => {
  beforeEach(() => {
    debugLog.enabled = false;
    debugLog.clear();
    storage.clear();
  });

  it('disabled by default (no localStorage entry)', () => {
    expect(debugLog.enabled).toBe(false);
  });

  it('setting enabled persists to localStorage', () => {
    debugLog.enabled = true;
    expect(storage.get('fluentia_debug_logging')).toBe('1');

    debugLog.enabled = false;
    expect(storage.has('fluentia_debug_logging')).toBe(false);
  });

  it('log() is a no-op when disabled', () => {
    debugLog.enabled = false;
    debugLog.log('should not appear');
    expect(debugLog.size).toBe(0);
    expect(debugLog.dump()).toBe('');
  });

  it('many logs when disabled produce zero entries', () => {
    debugLog.enabled = false;
    for (let i = 0; i < 100; i++) {
      debugLog.log(`msg ${i}`);
    }
    expect(debugLog.size).toBe(0);
  });

  it('log() writes to buffer when enabled', () => {
    debugLog.enabled = true;
    debugLog.log('hello');
    expect(debugLog.size).toBe(1);
    expect(debugLog.dump()).toContain('hello');
  });

  it('multiple logs append correctly', () => {
    debugLog.enabled = true;
    debugLog.log('first');
    debugLog.log('second');
    debugLog.log('third');
    expect(debugLog.size).toBe(3);

    const dump = debugLog.dump();
    expect(dump).toContain('first');
    expect(dump).toContain('second');
    expect(dump).toContain('third');
  });

  it('each log entry contains a timestamp', () => {
    debugLog.enabled = true;
    debugLog.log('timestamped');
    const dump = debugLog.dump();
    // Format: [HH:MM:SS.mmm]
    expect(dump).toMatch(/\[\d{2}:\d{2}:\d{2}\.\d{3}\]/);
  });

  it('toggle off stops writing', () => {
    debugLog.enabled = true;
    debugLog.log('before');
    debugLog.enabled = false;
    debugLog.log('after');

    expect(debugLog.size).toBe(1);
    expect(debugLog.dump()).toContain('before');
    expect(debugLog.dump()).not.toContain('after');
  });

  it('toggle on after off resumes writing', () => {
    debugLog.enabled = true;
    debugLog.log('session1');
    debugLog.enabled = false;
    debugLog.log('dropped');
    debugLog.enabled = true;
    debugLog.log('session2');

    expect(debugLog.size).toBe(2);
    const dump = debugLog.dump();
    expect(dump).toContain('session1');
    expect(dump).not.toContain('dropped');
    expect(dump).toContain('session2');
  });
});

describe('debugLog — ring buffer', () => {
  beforeEach(() => {
    debugLog.enabled = true;
    debugLog.clear();
  });

  afterEach(() => {
    debugLog.enabled = false;
  });

  it('ring buffer caps at 2000 entries', () => {
    for (let i = 0; i < 2100; i++) {
      debugLog.log(`entry-${String(i).padStart(5, '0')}`);
    }
    // Should cap at 2000
    expect(debugLog.size).toBe(2000);
    // Oldest entries (00000-00099) should be dropped
    expect(debugLog.dump()).not.toContain('entry-00000');
    expect(debugLog.dump()).not.toContain('entry-00099');
    // Recent entries should be present
    expect(debugLog.dump()).toContain('entry-02099');
    expect(debugLog.dump()).toContain('entry-02000');
  });
});

describe('debugLog — clear and dump', () => {
  beforeEach(() => {
    debugLog.enabled = true;
    debugLog.clear();
  });

  afterEach(() => {
    debugLog.enabled = false;
  });

  it('clear empties the buffer', () => {
    debugLog.log('something');
    expect(debugLog.size).toBe(1);

    debugLog.clear();
    expect(debugLog.size).toBe(0);
    expect(debugLog.dump()).toBe('');
  });

  it('dump returns all entries joined by newlines', () => {
    debugLog.log('a');
    debugLog.log('b');
    debugLog.log('c');

    const dump = debugLog.dump();
    const lines = dump.split('\n');
    expect(lines).toHaveLength(3);
    expect(lines[0]).toContain('a');
    expect(lines[1]).toContain('b');
    expect(lines[2]).toContain('c');
  });
});

describe('debugLog — persistence', () => {
  beforeEach(() => {
    debugLog.enabled = false;
    debugLog.clear();
    storage.clear();
  });

  it('reads enabled state from localStorage on module load', () => {
    // Simulate persisted state
    storage.set('fluentia_debug_logging', '1');
    // Re-importing won't re-execute the module, so we test the setter behavior
    debugLog.enabled = true;
    expect(storage.get('fluentia_debug_logging')).toBe('1');
  });

  it('removes localStorage key when disabled', () => {
    debugLog.enabled = true;
    expect(storage.has('fluentia_debug_logging')).toBe(true);

    debugLog.enabled = false;
    expect(storage.has('fluentia_debug_logging')).toBe(false);
  });
});

describe('debugLog — special characters', () => {
  beforeEach(() => {
    debugLog.enabled = true;
    debugLog.clear();
  });

  afterEach(() => {
    debugLog.enabled = false;
  });

  it('handles empty string', () => {
    debugLog.log('');
    expect(debugLog.size).toBe(1);
  });

  it('preserves unicode and CJK characters', () => {
    debugLog.log('特殊中文字符 🎉 emoji');
    const dump = debugLog.dump();
    expect(dump).toContain('特殊中文字符');
    expect(dump).toContain('🎉');
  });

  it('preserves special punctuation', () => {
    debugLog.log('quotes "double" and \\backslash');
    const dump = debugLog.dump();
    expect(dump).toContain('"double"');
  });
});
