import type { TextDiff } from '../types';

// Grapheme segmenter — counts visible characters, not UTF-16 code units.
// This is critical for emoji: 🇺🇸 is 1 grapheme but 4 UTF-16 units.
// Windows SendInput VK_BACK deletes one grapheme cluster per backspace,
// so we must count graphemes, not code units.
const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter
  ? new Intl.Segmenter(undefined, { granularity: 'grapheme' })
  : null;

function graphemeLength(text: string): number {
  if (!segmenter) return text.length; // fallback for environments without Segmenter
  return [...segmenter.segment(text)].length;
}

/**
 * Compute the diff between old and new text using common-prefix only.
 *
 * IMPORTANT: We intentionally do NOT use suffix matching.
 * The PC applies diffs via SendInput backspaces which always delete
 * from the cursor at the END of text.  A suffix-optimised diff would
 * produce a smaller (backspace, insert) pair that targets the middle
 * of the string, but backspace-from-end cannot reach the middle
 * without also deleting the unchanged suffix — so the suffix chars
 * would be lost and the result garbled.
 *
 * Prefix-only:  backspace = grapheme_count(old) − prefix_graphemes
 *               insert    = new[prefix..]
 * This is always correct for cursor-at-end injection.
 */
export function computeDiff(oldText: string, newText: string): TextDiff {
  if (oldText === newText) return { backspace: 0, insert: '' };

  const minLen = Math.min(oldText.length, newText.length);
  let prefix = 0;
  while (prefix < minLen && oldText[prefix] === newText[prefix]) {
    prefix++;
  }

  const suffix = oldText.substring(prefix);
  return {
    backspace: graphemeLength(suffix),
    insert: newText.substring(prefix),
  };
}
