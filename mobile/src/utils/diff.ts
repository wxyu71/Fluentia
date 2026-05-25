import type { TextDiff } from '../types';

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
 * Prefix-only:  backspace = old.len − prefix   →  deletes from end
 *               insert    = new[prefix..]       →  retypes the tail
 * This is always correct for cursor-at-end injection.
 */
export function computeDiff(oldText: string, newText: string): TextDiff {
  if (oldText === newText) return { backspace: 0, insert: '' };

  const minLen = Math.min(oldText.length, newText.length);
  let prefix = 0;
  while (prefix < minLen && oldText[prefix] === newText[prefix]) {
    prefix++;
  }

  return {
    backspace: oldText.length - prefix,
    insert: newText.substring(prefix),
  };
}
