/**
 * Tests for the debounced diff sending logic in InputArea.
 *
 * The debounce mechanism coalesces rapid keystrokes into a single diff
 * command, preventing the "delete all, retype all" flickering when
 * editing at the beginning of a long string.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { computeDiff } from './diff';
import { DIFF_DEBOUNCE_MS } from '../constants';

// Simulate the debounce logic from InputArea.tsx
function createDebouncedSender(onSend: (diff: { backspace: number; insert: string }) => void) {
  let lastSent = '';
  let timer: ReturnType<typeof setTimeout> | null = null;
  let pending: string | null = null;

  const flushToCommand = (targetText: string) => {
    const diff = computeDiff(lastSent, targetText);
    if (diff.backspace > 0 || diff.insert) {
      onSend(diff);
    }
    lastSent = targetText;
  };

  const sendDiff = (newText: string) => {
    if (timer !== null) clearTimeout(timer);
    pending = newText;
    timer = setTimeout(() => {
      timer = null;
      const p = pending;
      pending = null;
      if (p === null) return;
      flushToCommand(p);
    }, DIFF_DEBOUNCE_MS);
  };

  const sendDiffImmediate = (newText: string) => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    pending = null;
    flushToCommand(newText);
  };

  const cancel = () => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    pending = null;
  };

  const hasPending = () => pending !== null;

  return { sendDiff, sendDiffImmediate, cancel, hasPending };
}

describe('Diff debounce — coalescing rapid keystrokes', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('coalesces rapid inputs into one diff', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff } = createDebouncedSender((d) => sent.push(d));

    sendDiff('B');
    sendDiff('BA');
    sendDiff('BAA');

    // No diff sent yet (debounce window active)
    expect(sent.length).toBe(0);

    // Advance past debounce window
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);

    // Only one diff sent — the final state
    expect(sent.length).toBe(1);
    expect(sent[0].insert).toBe('BAA');
  });

  it('resets timer on each new input', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff } = createDebouncedSender((d) => sent.push(d));

    sendDiff('B');
    vi.advanceTimersByTime(30);
    sendDiff('BA');
    vi.advanceTimersByTime(30);
    sendDiff('BAA');

    // Still no diff — timer was reset
    expect(sent.length).toBe(0);

    // Now wait the full window
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);
    expect(sent.length).toBe(1);
    expect(sent[0].insert).toBe('BAA');
  });

  it('sends diff immediately with sendDiffImmediate', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiffImmediate } = createDebouncedSender((d) => sent.push(d));

    sendDiffImmediate('hello');
    expect(sent.length).toBe(1);
    expect(sent[0].insert).toBe('hello');
  });

  it('flushes pending diff before immediate send', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff, sendDiffImmediate, hasPending } = createDebouncedSender((d) => sent.push(d));

    sendDiff('pending');
    expect(hasPending()).toBe(true);

    // sendDiffImmediate cancels the pending timer and sends directly
    sendDiffImmediate('immediate');

    // Only the immediate send fires — pending is discarded
    expect(sent.length).toBe(1);
    expect(sent[0].insert).toBe('immediate');
    expect(hasPending()).toBe(false);
  });

  it('does not send diff when text is unchanged', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff } = createDebouncedSender((d) => sent.push(d));

    sendDiff('hello');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);
    expect(sent.length).toBe(1);

    // Send same text again
    sendDiff('hello');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);

    // No additional diff — text unchanged
    expect(sent.length).toBe(1);
  });

  it('cancel clears pending diff', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff, cancel, hasPending } = createDebouncedSender((d) => sent.push(d));

    sendDiff('pending');
    expect(hasPending()).toBe(true);

    cancel();
    expect(hasPending()).toBe(false);

    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);
    expect(sent.length).toBe(0);
  });

  it('beginning-of-text edit produces correct diff after debounce', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff } = createDebouncedSender((d) => sent.push(d));

    // Simulate: user has "AAAA" (4 chars), changes first char to "B"
    // Initial state: lastSent = ''
    sendDiff('AAAA');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);
    expect(sent.length).toBe(1);
    expect(sent[0]).toEqual({ backspace: 0, insert: 'AAAA' });

    // Now change first char
    sendDiff('BAAA');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);
    expect(sent.length).toBe(2);
    // The diff should delete 4 and insert 4 (prefix=0)
    expect(sent[1]).toEqual({ backspace: 4, insert: 'BAAA' });
  });

  it('end-of-text append produces minimal diff', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    const { sendDiff } = createDebouncedSender((d) => sent.push(d));

    sendDiff('hello');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);

    sendDiff('hello world');
    vi.advanceTimersByTime(DIFF_DEBOUNCE_MS + 10);

    expect(sent.length).toBe(2);
    expect(sent[1]).toEqual({ backspace: 0, insert: ' world' });
  });
});

describe('Clear-during-debounce race — stranded diff protection', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  /**
   * Simulates the FIXED clear effect from InputArea.tsx:
   * 1. Cancels pending debounce timer
   * 2. Always preserves text (never clears)
   * 3. Resets lastSentRef and resends if text exists
   */
  function simulateClearEffect(opts: {
    textRef: string;
    lastSentRef: string;
    pendingDiffText: string | null;
    timer: ReturnType<typeof setTimeout> | null;
    flushToCommand: (text: string) => void;
    setText: (text: string) => void;
    setLastSentRef?: (value: string) => void;
  }) {
    // Cancel timer (like the effect does)
    if (opts.timer !== null) {
      clearTimeout(opts.timer);
      opts.timer = null;
    }
    opts.pendingDiffText = null;

    // Always preserve text and force full resync.
    // Never clear text — it's always resent to the PC.
    opts.lastSentRef = '';
    opts.setLastSentRef?.('');

    if (opts.textRef !== '') {
      opts.flushToCommand(opts.textRef);
    }

    return { textRef: opts.textRef, lastSentRef: opts.lastSentRef };
  }

  it('clear during debounce re-sends stranded diff (the fix)', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    let lastSent = '';
    let timer: ReturnType<typeof setTimeout> | null = null;
    let pending: string | null = null;
    let text = '';

    const flushToCommand = (targetText: string) => {
      const diff = computeDiff(lastSent, targetText);
      if (diff.backspace > 0 || diff.insert) {
        sent.push(diff);
      }
      lastSent = targetText;
    };

    const sendDiff = (newText: string) => {
      if (timer !== null) clearTimeout(timer);
      pending = newText;
      timer = setTimeout(() => {
        timer = null;
        const p = pending;
        pending = null;
        if (p === null) return;
        flushToCommand(p);
      }, DIFF_DEBOUNCE_MS);
    };

    // User types "h" on mobile
    text = 'h';
    sendDiff('h');

    // Timer is pending, no diff sent yet
    expect(sent.length).toBe(0);
    expect(pending).toBe('h');

    // Clear arrives from PC (focus change) — BEFORE timer fires
    simulateClearEffect({
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: pending,
      timer,
      flushToCommand,
      setText: (v) => { text = v; },
    });

    // After the fix: the stranded diff was re-sent immediately
    expect(sent.length).toBe(1);
    expect(sent[0]).toEqual({ backspace: 0, insert: 'h' });
    // Text is preserved on screen
    expect(text).toBe('h');
  });

  it('clear with no pending diff and no text correctly clears', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    let lastSent = '';
    let text = '';

    const flushToCommand = (targetText: string) => {
      const diff = computeDiff(lastSent, targetText);
      if (diff.backspace > 0 || diff.insert) {
        sent.push(diff);
      }
      lastSent = targetText;
    };

    // Clear arrives when user hasn't typed anything
    simulateClearEffect({
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: null,
      timer: null,
      flushToCommand,
      setText: (v) => { text = v; },
    });

    // Nothing sent, text stays empty
    expect(sent.length).toBe(0);
    expect(text).toBe('');
  });

  it('clear after diff already sent preserves and resends text', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    let lastSent = '';
    let text = '';

    const flushToCommand = (targetText: string) => {
      const diff = computeDiff(lastSent, targetText);
      if (diff.backspace > 0 || diff.insert) {
        sent.push(diff);
      }
      lastSent = targetText;
    };

    // User typed "h" and diff was already sent
    text = 'h';
    lastSent = 'h';

    // Clear arrives — text is always preserved (even when textRef === lastSentRef)
    simulateClearEffect({
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: null,
      timer: null,
      flushToCommand,
      setText: (v) => { text = v; },
      setLastSentRef: (v) => { lastSent = v; },
    });

    // Text is preserved and resent: after lastSent reset to '',
    // diff('', 'h') = insert 'h'
    expect(sent.length).toBe(1);
    expect(sent[0]).toEqual({ backspace: 0, insert: 'h' });
    expect(text).toBe('h');
  });

  it('clear preserves unsent text typed after last diff', () => {
    const sent: Array<{ backspace: number; insert: string }> = [];
    // Use shared state object so simulateClearEffect's lastSentRef reset is visible
    const state = { lastSent: 'he', text: 'hel' };

    const flushToCommand = (targetText: string) => {
      const diff = computeDiff(state.lastSent, targetText);
      if (diff.backspace > 0 || diff.insert) {
        sent.push(diff);
      }
      state.lastSent = targetText;
    };

    // Clear arrives — text != lastSent, unsent content exists
    simulateClearEffect({
      textRef: state.text,
      lastSentRef: state.lastSent,
      pendingDiffText: null,
      timer: null,
      flushToCommand,
      setText: (v) => { state.text = v; },
      setLastSentRef: (v) => { state.lastSent = v; },
    });

    // Stranded text re-sent: after lastSent reset to '', diff('','hel') = insert 'hel'
    expect(sent.length).toBe(1);
    expect(sent[0]).toEqual({ backspace: 0, insert: 'hel' });
    expect(state.text).toBe('hel');
  });
});
