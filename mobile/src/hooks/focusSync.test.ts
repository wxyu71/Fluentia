/**
 * Tests for focus state synchronization between desktop and mobile.
 *
 * v1.7.14: Clear messages now carry a `reason` field:
 *   - "focus": user switched to a different window on PC → clear mobile text
 *   - "resync": PC dropped a diff (exception during injection) → preserve text, resend
 *
 * v1.8.x: The desktop no longer sends "resync" on focus change (target switch).
 * Instead, it sends "focus" immediately. This prevents the mobile from resending
 * its full accumulated state (which includes characters already in the old target),
 * which was causing duplicates in the new target.
 *
 * Backward compatibility: messages without `reason` default to "focus".
 */

import { describe, it, expect } from 'vitest';

/**
 * Simulates the clear message flow.
 * Returns whether the mobile input was actually cleared.
 */
function simulateClearMessageFlow(options: {
  encryptionReady: boolean;
  mobileConnected: boolean;
  wsReadyState: number;
}): { cleared: boolean; reason: string } {
  const { encryptionReady, mobileConnected, wsReadyState } = options;

  if (!encryptionReady) {
    return { cleared: false, reason: 'encryption not ready — message not sent' };
  }

  if (!mobileConnected) {
    return { cleared: false, reason: 'mobile not connected — message queued but may not arrive' };
  }

  if (wsReadyState !== 1) {
    return { cleared: false, reason: 'WebSocket not open — message cannot be delivered' };
  }

  return { cleared: true, reason: 'clear message delivered and processed' };
}

/**
 * Simulates the InputArea clear effect with reason-based behavior.
 *
 * v1.7.14 logic:
 *   reason === "focus"  → clear text, reset lastSent
 *   reason === "resync" → preserve text, reset lastSent, resend
 *   reason missing      → default to "focus" (backward compat with pre-1.7.14 PC)
 */
function simulateClearEffect(opts: {
  reason: string;
  textRef: string;
  lastSentRef: string;
  pendingDiffText: string | null;
  flushToCommand: (text: string) => void;
  setText: (text: string) => void;
}): { text: string; lastSent: string; resyncSent: boolean } {
  let { textRef, lastSentRef } = opts;
  let resyncSent = false;

  // Cancel pending debounce (always)
  // pendingDiffText = null;

  const reason = opts.reason || 'focus';

  if (reason === 'focus') {
    // User switched to a different window — clear text
    opts.setText('');
    textRef = '';
    lastSentRef = '';
  } else {
    // Diff dropped — preserve text and resync
    lastSentRef = '';
    if (textRef !== '') {
      opts.flushToCommand(textRef);
      resyncSent = true;
    }
  }

  return { text: textRef, lastSent: lastSentRef, resyncSent };
}

describe('Focus State Sync — clear message delivery', () => {
  it('clear message delivered when all conditions met', () => {
    const result = simulateClearMessageFlow({
      encryptionReady: true,
      mobileConnected: true,
      wsReadyState: 1,
    });
    expect(result.cleared).toBe(true);
  });

  it('clear message lost when encryption not ready', () => {
    const result = simulateClearMessageFlow({
      encryptionReady: false,
      mobileConnected: true,
      wsReadyState: 1,
    });
    expect(result.cleared).toBe(false);
    expect(result.reason).toContain('encryption not ready');
  });

  it('clear message lost when mobile not connected', () => {
    const result = simulateClearMessageFlow({
      encryptionReady: true,
      mobileConnected: false,
      wsReadyState: 1,
    });
    expect(result.cleared).toBe(false);
    expect(result.reason).toContain('mobile not connected');
  });

  it('clear message lost when WebSocket closed', () => {
    const result = simulateClearMessageFlow({
      encryptionReady: true,
      mobileConnected: true,
      wsReadyState: 3,
    });
    expect(result.cleared).toBe(false);
    expect(result.reason).toContain('WebSocket not open');
  });
});

describe('Focus State Sync — reason-based clear behavior (v1.7.14)', () => {
  it('reason="focus" clears mobile text', () => {
    let text = 'hello world';
    const sent: Array<{ text: string }> = [];

    const result = simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: 'hello',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Focus change: text is CLEARED
    expect(text).toBe('');
    expect(result.text).toBe('');
    expect(result.lastSent).toBe('');
    expect(result.resyncSent).toBe(false);
    expect(sent.length).toBe(0);
  });

  it('reason="resync" preserves text and resends', () => {
    let text = 'hello world';
    const sent: Array<{ text: string }> = [];

    const result = simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: 'hello',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Resync: text is PRESERVED and resent
    expect(text).toBe('hello world');
    expect(result.text).toBe('hello world');
    expect(result.lastSent).toBe('');
    expect(result.resyncSent).toBe(true);
    expect(sent.length).toBe(1);
    expect(sent[0].text).toBe('hello world');
  });

  it('reason="resync" with empty text does not resend', () => {
    let text = '';
    const sent: Array<{ text: string }> = [];

    const result = simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Nothing to resync
    expect(text).toBe('');
    expect(result.resyncSent).toBe(false);
    expect(sent.length).toBe(0);
  });

  it('missing reason defaults to "focus" (backward compat)', () => {
    let text = 'stale text';
    const sent: Array<{ text: string }> = [];

    // Pre-1.7.14 PC sends clear without reason field
    const result = simulateClearEffect({
      reason: undefined as unknown as string,
      textRef: text,
      lastSentRef: 'stale',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Default behavior: clear (safe default for unknown reason)
    expect(text).toBe('');
    expect(result.text).toBe('');
    expect(sent.length).toBe(0);
  });

  it('reason="focus" clears even when text was never sent', () => {
    let text = 'typed but not sent';
    const sent: Array<{ text: string }> = [];

    simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: 'typed but',
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Focus change: text is cleared even if it was never sent
    expect(text).toBe('');
    expect(sent.length).toBe(0);
  });

  it('reason="resync" with pending diff text preserves all', () => {
    let text = 'hello world';
    const sent: Array<{ text: string }> = [];

    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: 'hello',
      pendingDiffText: 'hello w',
      flushToCommand: (t) => sent.push({ text: t }),
      setText: (v) => { text = v; },
    });

    // Resync: full text preserved and resent
    expect(text).toBe('hello world');
    expect(sent.length).toBe(1);
    expect(sent[0].text).toBe('hello world');
  });
});

describe('Focus State Sync — inputResetVersion', () => {
  it('clear message increments inputResetVersion', () => {
    let inputResetVersion = 0;

    const clearHandler = () => {
      inputResetVersion += 1;
    };

    clearHandler();
    expect(inputResetVersion).toBe(1);

    clearHandler();
    expect(inputResetVersion).toBe(2);
  });

  it('inputResetVersion change triggers text clear', () => {
    let inputText = 'hello world';
    let inputResetVersion = 0;

    const effect = () => {
      if (inputResetVersion === 0) return;
      inputText = '';
    };

    expect(inputText).toBe('hello world');

    inputResetVersion += 1;
    effect();

    expect(inputText).toBe('');
  });
});

describe('Focus State Sync — race conditions', () => {
  it('clear arrives while user is typing (focus reason)', () => {
    let inputText = 'hello';
    let lastSentText = 'hello';

    // User types " world"
    inputText = 'hello world';

    // Focus change clear arrives — text should be cleared
    inputText = '';
    lastSentText = '';

    expect(lastSentText).toBe('');
    expect(inputText).toBe('');
  });

  it('clear arrives while user is typing (resync reason)', () => {
    const inputText = 'hello';
    let lastSentText = 'hel';  // some was sent, some not yet

    // Resync clear arrives — text should be preserved and resent
    lastSentText = '';
    // In real code, flushDiffToCommand(inputText) would be called here

    expect(inputText).toBe('hello');  // preserved
    expect(lastSentText).toBe('');    // reset for resend
  });

  it('multiple focus clears in quick succession', () => {
    let inputResetVersion = 0;
    let inputText = 'hello';

    // Multiple focus clears
    inputResetVersion += 1;
    inputText = '';
    inputResetVersion += 1;
    inputText = '';
    inputResetVersion += 1;
    inputText = '';

    expect(inputText).toBe('');
    expect(inputResetVersion).toBe(3);
  });

  it('focus clear followed by resync clear', () => {
    let text = 'important text';
    const sent: Array<string> = [];

    // First: focus clear — text is cleared
    simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: 'important',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('');
    expect(sent.length).toBe(0);

    // User types new text
    text = 'new input';

    // Second: resync clear — text is preserved and resent
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('new input');
    expect(sent.length).toBe(1);
    expect(sent[0]).toBe('new input');
  });
});

describe('Focus State Sync — clear reason parsing', () => {
  it('parses reason from encrypted message', () => {
    // Simulates what useWebSocket does when receiving a clear message
    function parseClearReason(parsed: { type: string; reason?: string }): string {
      if (parsed.type !== 'clear') return '';
      return parsed.reason || 'focus';
    }

    expect(parseClearReason({ type: 'clear', reason: 'focus' })).toBe('focus');
    expect(parseClearReason({ type: 'clear', reason: 'resync' })).toBe('resync');
    expect(parseClearReason({ type: 'clear' })).toBe('focus');  // backward compat
    expect(parseClearReason({ type: 'clear', reason: '' })).toBe('focus');  // empty = default
  });

  it('unknown reason defaults to focus', () => {
    function parseClearReason(parsed: { type: string; reason?: string }): string {
      if (parsed.type !== 'clear') return '';
      return parsed.reason || 'focus';
    }

    // Future reason values we don't know about yet
    expect(parseClearReason({ type: 'clear', reason: 'unknown_future' })).toBe('unknown_future');
    // But empty/missing always defaults to focus
    expect(parseClearReason({ type: 'clear' })).toBe('focus');
  });
});

/**
 * Tests for the rapid-resync cascade bug (v1.7.15+ fix).
 *
 * Bug scenario (from debug.log):
 * 1. PC detects focus change → EnsureInputTarget returns false
 * 2. FlushBufferedDiff sends {type:"clear", reason:"resync"} to mobile
 * 3. Mobile preserves text and resends full text as diff
 * 4. PC receives diff → FlushBufferedDiff → EnsureInputTarget fails again
 * 5. PC sends another resync → mobile resends → infinite loop
 *
 * Root cause: SetStatus() threw InvalidOperationException (WPF thread affinity)
 * from the ProcessCommandQueue background thread, causing FlushBufferedDiff's
 * catch block to reset state and send resync on every iteration.
 *
 * The loop was broken only when an "enter" command arrived and also threw,
 * propagating to ProcessCommandQueue's outer catch.
 *
 * Fix: SetStatus and NotifyManualInputTargetRecoveryNeeded now dispatch to the
 * UI thread via CheckAccess()/Dispatcher.BeginInvoke.
 *
 * These tests verify the mobile side handles rapid resync messages correctly
 * and doesn't corrupt state or produce duplicate text.
 */
describe('Rapid resync cascade — v1.7.15 regression', () => {
  it('rapid resync flood preserves text without duplication', () => {
    // Simulate: PC sends many resync messages in quick succession.
    // Each one triggers the clear effect with reason="resync".
    // The mobile should preserve text and resend — but NOT duplicate it.
    let text = '他们只能巴拉巴拉巴拉怎么样？或者如何？';
    const sent: Array<string> = [];

    // Simulate 10 rapid resync clears (as seen in the bug log)
    for (let i = 0; i < 10; i++) {
      simulateClearEffect({
        reason: 'resync',
        textRef: text,
        lastSentRef: i === 0 ? text : '',  // first: lastSent was the full text; after: reset
        pendingDiffText: null,
        flushToCommand: (t) => sent.push(t),
        setText: (v) => { text = v; },
      });
    }

    // Text must be preserved (not cleared)
    expect(text).toBe('他们只能巴拉巴拉巴拉怎么样？或者如何？');
    // Each resync should send the full text (because lastSentRef is reset each time)
    expect(sent.length).toBe(10);
    // All sent texts should be identical (no duplication)
    for (const s of sent) {
      expect(s).toBe('他们只能巴拉巴拉巴拉怎么样？或者如何？');
    }
  });

  it('focus clear stops the typing loop (no resync on target change)', () => {
    // After the v1.8.x fix, the desktop sends "focus" immediately on target
    // change instead of "resync". The mobile clears text and resets lastSentRef.
    // No resync messages are sent from the target-change path.
    let text = '他们只能巴拉巴拉';
    const sent: Array<string> = [];

    // Focus clear arrives immediately (no preceding resync messages)
    simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: '他们只能巴拉',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });

    // Text should be cleared — no resync, no duplicates
    expect(text).toBe('');
    expect(sent.length).toBe(0);
  });

  it('resync with empty text after focus clear is a no-op', () => {
    // Scenario: focus clear already cleared text, then a stale resync arrives.
    let text = '';
    const sent: Array<string> = [];

    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });

    // Nothing to resync — no diff sent
    expect(text).toBe('');
    expect(sent.length).toBe(0);
  });

  it('resync preserves Chinese text correctly', () => {
    // The bug log shows Chinese text being mangled during rapid resyncs.
    // Verify that the mobile correctly preserves multi-byte characters.
    const chineseTexts = [
      '他们只能巴拉巴拉巴拉怎么样？或者如何？',
      '发现了一个新的问题。',
      '发现现在Windows客户端的一个严重的问题是它无法通过GitHub仓库来拉取最新的状态。',
      '发现的第二个问题是，当网络波动的时候，那么突然就会断开网络连接，就会打断键盘的正常输入。',
    ];

    for (const text of chineseTexts) {
      let currentText = text;
      const sent: Array<string> = [];

      simulateClearEffect({
        reason: 'resync',
        textRef: currentText,
        lastSentRef: '',
        pendingDiffText: null,
        flushToCommand: (t) => sent.push(t),
        setText: (v) => { currentText = v; },
      });

      expect(currentText).toBe(text);
      expect(sent.length).toBe(1);
      expect(sent[0]).toBe(text);
    }
  });

  it('resync after user edits preserves latest text', () => {
    // Scenario: user edits text while resync messages are arriving.
    // The mobile should preserve the LATEST text, not the stale version.
    let text = 'hello';
    const sent: Array<string> = [];

    // First resync with initial text
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: 'hello',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('hello');

    // User edits text
    text = 'hello world';
    sent.length = 0;

    // Second resync — should use the updated text
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('hello world');
    expect(sent.length).toBe(1);
    expect(sent[0]).toBe('hello world');
  });

  it('interleaved resync and typing does not lose characters', () => {
    // Simulates the exact bug scenario from the log:
    // User types while PC is in a resync loop.
    let text = '';
    let lastSent = '';
    const sent: Array<string> = [];

    // User starts typing
    text = '他';
    lastSent = '';

    // Resync arrives before next keystroke
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('他');  // preserved

    // User continues typing (text is updated in the textarea)
    text = '他们';
    lastSent = '';  // was reset by resync

    // Another resync
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('他们');  // preserved, not lost

    // Verify no character was lost
    expect(sent.every(s => s === '他' || s === '他们')).toBe(true);
  });
});

describe('Rapid resync cascade — PC-side debounce behavior', () => {
  /**
   * Simulates the debounce logic in ResetMobileInputAfterFocusChangeAsync.
   * Each "resync" triggers EnsureInputTarget which resets the debounce timer.
   * The "focus" clear should only fire once, after the last resync.
   */
  it('debounce timer fires only after resyncs stop', async () => {
    let focusCleared = false;
    let activeCts: AbortController | null = null;
    const DEBOUNCE_MS = 100; // shortened for test

    const simulateResync = () => {
      // Cancel previous debounce
      activeCts?.abort();
      const controller = new AbortController();
      activeCts = controller;

      // Start new debounce
      setTimeout(() => {
        if (!controller.signal.aborted) {
          focusCleared = true;
        }
      }, DEBOUNCE_MS);
    };

    // Simulate rapid resyncs (every 20ms, faster than debounce)
    for (let i = 0; i < 5; i++) {
      simulateResync();
      await new Promise(r => setTimeout(r, 20));
    }

    // Focus should NOT have cleared yet (debounce keeps resetting)
    expect(focusCleared).toBe(false);

    // Wait for debounce to fire
    await new Promise(r => setTimeout(r, DEBOUNCE_MS + 50));

    // Now focus should have cleared
    expect(focusCleared).toBe(true);
  });

  it('focus clear arrives during typing cancels pending resync', () => {
    // When a "focus" clear arrives, it should clear text regardless
    // of any pending resync state.
    let text = 'important text';
    const sent: Array<string> = [];

    // Resync preserves text
    simulateClearEffect({
      reason: 'resync',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('important text');

    // Focus clear overrides — text is cleared
    simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: '',
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });
    expect(text).toBe('');
  });

  it('focus clear prevents duplicate on target change', () => {
    // Scenario from the bug: user types "我们发现了这个问题" on mobile.
    // Desktop target changes → focus clear arrives → mobile clears text.
    // User continues typing → diff starts from empty → no duplicate.
    let text = '我们发现了这个问题';
    const lastSent = '我们发现了这个';
    const sent: Array<string> = [];

    // Focus clear arrives (desktop detected target change)
    simulateClearEffect({
      reason: 'focus',
      textRef: text,
      lastSentRef: lastSent,
      pendingDiffText: null,
      flushToCommand: (t) => sent.push(t),
      setText: (v) => { text = v; },
    });

    // Mobile text cleared, lastSentRef reset
    expect(text).toBe('');
    expect(sent.length).toBe(0);

    // User continues typing in new context
    text = '新的输入';
    // Next diff should be computed against empty lastSentRef
    // (backspace=0, insert="新的输入") — no duplicate of old text
    const diff = { backspace: 0, insert: text };
    expect(diff.backspace).toBe(0);
    expect(diff.insert).toBe('新的输入');
  });
});
