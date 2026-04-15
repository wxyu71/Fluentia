import type { TextDiff } from '../types';

/**
 * Compute the minimal diff between old and new text.
 * Returns the number of backspaces needed and the text to insert.
 */
export function computeDiff(oldText: string, newText: string): TextDiff {
  // Find common prefix
  let commonLen = 0;
  const minLen = Math.min(oldText.length, newText.length);
  while (commonLen < minLen && oldText[commonLen] === newText[commonLen]) {
    commonLen++;
  }

  return {
    backspace: oldText.length - commonLen,
    insert: newText.substring(commonLen),
  };
}
