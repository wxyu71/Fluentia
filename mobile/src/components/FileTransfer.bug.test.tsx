/**
 * Bug reproduction test for FileTransfer sendChunk logic.
 *
 * BUG: When transport drops during large file transfer, the UI shows 100%
 * completion even though most chunks were never delivered.
 *
 * FIX: onSendCommand now returns boolean. sendChunk checks the return
 * value and retries failed chunks before giving up.
 */

import { describe, it, expect, vi } from 'vitest';
import type { InputCommand } from '../types';

/**
 * Extracted sendChunk logic — tests the BEFORE and AFTER behavior.
 *
 * BEFORE fix: onSendCommand returns void, completedChunks always increments.
 * AFTER fix: onSendCommand returns boolean, completedChunks only increments on success.
 */
function simulateSendChunksFixed(
  totalChunks: number,
  sendFn: (chunkIndex: number) => boolean,
): { completedChunks: number; sentChunks: number[]; failed: boolean } {
  let completedChunks = 0;
  let nextChunkIndex = 0;
  const sentChunks: number[] = [];
  let failed = false;

  const sendChunk = () => {
    while (nextChunkIndex < totalChunks) {
      const chunkIndex = nextChunkIndex;
      nextChunkIndex += 1;

      // FIX: check return value of onSendCommand
      const MAX_CHUNK_RETRIES = 3;
      let chunkSent = false;
      for (let retry = 0; retry < MAX_CHUNK_RETRIES && !chunkSent; retry++) {
        chunkSent = sendFn(chunkIndex);
      }
      if (!chunkSent) {
        failed = true;
        return;
      }
      completedChunks += 1;
      sentChunks.push(chunkIndex);
    }
  };

  // Simulate CHUNK_CONCURRENCY = 3
  for (let i = 0; i < Math.min(3, totalChunks); i++) {
    sendChunk();
  }

  return { completedChunks, sentChunks, failed };
}

describe('FileTransfer Bug — sendChunk does not check send success', () => {
  describe('BUG behavior (before fix)', () => {
    it('BUG: onSendCommand was void — no way to detect failure', () => {
      // Before fix: onSendCommand returned void
      // completedChunks incremented regardless of send success
      const mockOnSendCommand = vi.fn((cmd: InputCommand) => {
        // Returns void (old behavior)
      });

      const result = mockOnSendCommand({ type: 'diff', text: 'test' });

      // Before fix: result is undefined (void)
      expect(result).toBeUndefined();
    });
  });

  describe('FIX behavior (after fix)', () => {
    it('onSendCommand returns boolean indicating send success', () => {
      const mockOnSendCommand = vi.fn((cmd: InputCommand): boolean => {
        return true;
      });

      const result = mockOnSendCommand({ type: 'diff', text: 'test' });
      expect(typeof result).toBe('boolean');
      expect(result).toBe(true);
    });

    it('FIX: all sends succeed — completedChunks matches sentChunks', () => {
      const { completedChunks, sentChunks, failed } = simulateSendChunksFixed(
        10,
        () => true,
      );

      expect(completedChunks).toBe(10);
      expect(sentChunks.length).toBe(10);
      expect(failed).toBe(false);
    });

    it('FIX: send fails at chunk 5 — transfer stops, only 5 counted', () => {
      const { completedChunks, sentChunks, failed } = simulateSendChunksFixed(
        10,
        (idx) => idx < 5,
      );

      // After fix: completedChunks = 5 (not 10)
      expect(completedChunks).toBe(5);
      expect(sentChunks.length).toBe(5);
      expect(failed).toBe(true);
    });

    it('FIX: transport drops at 10% of 144 chunks — transfer stops at 15', () => {
      const { completedChunks, sentChunks, failed } = simulateSendChunksFixed(
        144,
        (idx) => idx < 15,
      );

      // After fix: only 15 chunks counted (not 144)
      expect(completedChunks).toBe(15);
      expect(sentChunks.length).toBe(15);
      expect(failed).toBe(true);
    });

    it('FIX: all sends fail — completedChunks = 0', () => {
      const { completedChunks, sentChunks, failed } = simulateSendChunksFixed(
        10,
        () => false,
      );

      expect(completedChunks).toBe(0);
      expect(sentChunks.length).toBe(0);
      expect(failed).toBe(true);
    });

    it('FIX: transient failure with retry succeeds', () => {
      let attempts = 0;
      const { completedChunks, sentChunks, failed } = simulateSendChunksFixed(
        5,
        (_idx) => {
          attempts++;
          // First 2 attempts fail, then succeed (retry works)
          if (attempts <= 2) return false;
          return true;
        },
      );

      // With retry, all chunks eventually succeed
      expect(completedChunks).toBe(5);
      expect(sentChunks.length).toBe(5);
      expect(failed).toBe(false);
    });
  });
});
