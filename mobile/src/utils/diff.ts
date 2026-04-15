import type { TextDiff } from '../types';

/**
 * Compute the minimal diff between old and new text using
 * common prefix + common suffix to minimise backspace/insert operations.
 * This correctly handles mid-string edits from voice IME corrections.
 */
export function computeDiff(oldText: string, newText: string): TextDiff {
  if (oldText === newText) return { backspace: 0, insert: '' };

  // Common prefix
  const minLen = Math.min(oldText.length, newText.length);
  let prefix = 0;
  while (prefix < minLen && oldText[prefix] === newText[prefix]) {
    prefix++;
  }

  // Common suffix (must not overlap with prefix)
  let suffix = 0;
  const maxSuffix = minLen - prefix;
  while (
    suffix < maxSuffix &&
    oldText[oldText.length - 1 - suffix] === newText[newText.length - 1 - suffix]
  ) {
    suffix++;
  }

  return {
    backspace: oldText.length - prefix - suffix,
    insert: newText.substring(prefix, newText.length - suffix),
  };
}
