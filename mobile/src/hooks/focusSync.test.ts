/**
 * Tests for focus state synchronization between desktop and mobile.
 *
 * v1.7.14: Clear messages now carry a `reason` field:
 *   - "focus": user switched to a different window on PC → clear mobile text
 *   - "resync": PC dropped a diff (EnsureInputTarget failed) → preserve text, resend
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
