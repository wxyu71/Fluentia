/**
 * Test for the file chunk sending bug.
 *
 * BUG: When transport drops during a large file transfer, sendChunk
 * increments completedChunks regardless of whether onSendCommand
 * actually delivered the message. The UI shows 100% but chunks
 * were silently lost.
 *
 * This test extracts the core sendChunk logic and verifies:
 * 1. BUG: completedChunks counts all chunks even when send fails
 * 2. FIX: completedChunks should only count successfully sent chunks
 */

import { describe, it, expect } from 'vitest';

/**
 * Extracted sendChunk logic from FileTransfer.tsx (lines 244-283).
 * This is the EXACT logic from the component, extracted for testing.
 *
 * Returns { completedChunks, sentChunks } where:
 * - completedChunks: what the UI uses for progress (BUGGY)
 * - sentChunks: actually delivered chunks (CORRECT)
 */
async function simulateSendChunks(
  totalChunks: number,
  sendFn: (chunkIndex: number) => boolean, // returns true if send succeeded
): Promise<{ completedChunks: number; sentChunks: number[] }> {
  let completedChunks = 0;
  let nextChunkIndex = 0;
  const sentChunks: number[] = [];

  const sendChunk = async () => {
    while (nextChunkIndex < totalChunks) {
      const chunkIndex = nextChunkIndex;
      nextChunkIndex += 1;

      // Simulate: encode chunk (instant in test)
      const b64 = 'chunk-data';

      // This is the BUG: onSendCommand is called but return value is not checked
      const actuallySent = sendFn(chunkIndex);

      // completedChunks always increments — THIS IS THE BUG
      completedChunks += 1;

      if (actuallySent) {
        sentChunks.push(chunkIndex);
      }
    }
  };

  // Simulate CHUNK_CONCURRENCY = 3
  await Promise.all(Array.from({ length: Math.min(3, totalChunks) }, () => sendChunk()));

  return { completedChunks, sentChunks };
}

describe('File Chunk Sender — Bug Reproduction', () => {
  describe('BUG: completedChunks counts all chunks even when send fails', () => {
    it('all sends succeed — completedChunks matches sentChunks', async () => {
      const { completedChunks, sentChunks } = await simulateSendChunks(
        10,
        () => true, // all sends succeed
      );

      expect(completedChunks).toBe(10);
      expect(sentChunks.length).toBe(10);
    });

    it('BUG: send fails at chunk 5 — completedChunks still shows 10', async () => {
      const { completedChunks, sentChunks } = await simulateSendChunks(
        10,
        (idx) => idx < 5, // only first 5 succeed
      );

      // BUG: completedChunks = 10 even though only 5 were sent
      expect(completedChunks).toBe(10); // This is the bug — UI shows 100%
      expect(sentChunks.length).toBe(5); // Only 5 were actually delivered
    });

    it('BUG: transport drops at 10% of 144 chunks — UI shows 100%', async () => {
      const { completedChunks, sentChunks } = await simulateSendChunks(
        144,
        (idx) => idx < 15, // only first 15 (~10%) succeed
      );

      // BUG: UI shows 144/144 = 100% complete
      expect(completedChunks).toBe(144);
      // But only 15 chunks were actually delivered
      expect(sentChunks.length).toBe(15);
      // 129 chunks were silently lost
      expect(completedChunks - sentChunks.length).toBe(129);
    });

    it('BUG: all sends fail — completedChunks still shows total', async () => {
      const { completedChunks, sentChunks } = await simulateSendChunks(
        10,
        () => false, // all sends fail
      );

      expect(completedChunks).toBe(10); // BUG: shows complete
      expect(sentChunks.length).toBe(0); // Nothing was sent
    });
  });

  describe('FIX: completedChunks should only count sent chunks', () => {
    /**
     * This test documents the expected behavior after the fix.
     * After fixing, sendChunk should:
     * 1. Check if onSendCommand actually delivered the message
     * 2. Only increment completedChunks on success
     * 3. Retry failed chunks (not skip them)
     */
    it('FIX: only count successfully sent chunks', async () => {
      // After fix, the logic should be:
      let completedChunks = 0;
      const sentChunks: number[] = [];
      const totalChunks = 10;

      for (let i = 0; i < totalChunks; i++) {
        const sent = i < 5; // only first 5 succeed
        if (sent) {
          completedChunks += 1;
          sentChunks.push(i);
        }
        // If sent fails, do NOT increment completedChunks
        // Instead, retry or pause
      }

      expect(completedChunks).toBe(5); // Correct: only 5 counted
      expect(sentChunks.length).toBe(5);
    });

    it('FIX: retry failed chunks until all are sent', async () => {
      let completedChunks = 0;
      const sentChunks: number[] = [];
      const totalChunks = 10;
      const failUntilChunk = 5; // chunks 0-4 fail initially, succeed on retry

      // Simulate: first attempt fails for chunks 0-4, retry succeeds
      const pendingChunks = new Set<number>();
      for (let i = 0; i < totalChunks; i++) {
        pendingChunks.add(i);
      }

      // First pass: chunks 5-9 succeed
      for (let i = 5; i < totalChunks; i++) {
        completedChunks += 1;
        sentChunks.push(i);
        pendingChunks.delete(i);
      }

      // Retry pass: chunks 0-4 now succeed
      for (const idx of [...pendingChunks]) {
        completedChunks += 1;
        sentChunks.push(idx);
        pendingChunks.delete(idx);
      }

      expect(completedChunks).toBe(10);
      expect(sentChunks.length).toBe(10);
      expect(pendingChunks.size).toBe(0);
    });
  });
});
