import { describe, it, expect } from 'vitest';
import { computeDiff } from './diff';

describe('computeDiff', () => {
  it('returns zero diff for identical text', () => {
    expect(computeDiff('hello', 'hello')).toEqual({ backspace: 0, insert: '' });
  });

  it('returns zero diff for two empty strings', () => {
    expect(computeDiff('', '')).toEqual({ backspace: 0, insert: '' });
  });

  it('handles pure insertion at end', () => {
    expect(computeDiff('hello', 'hello world')).toEqual({ backspace: 0, insert: ' world' });
  });

  it('handles pure deletion from end', () => {
    expect(computeDiff('hello world', 'hello')).toEqual({ backspace: 6, insert: '' });
  });

  it('handles replacement (different suffix)', () => {
    // old: "hello" (5 chars), new: "hexxo" (5 chars)
    // common prefix: "he" (2 chars)
    // backspace: 5 - 2 = 3, insert: "xxo"
    expect(computeDiff('hello', 'hexxo')).toEqual({ backspace: 3, insert: 'xxo' });
  });

  it('handles old text being empty (insert all)', () => {
    expect(computeDiff('', 'hello')).toEqual({ backspace: 0, insert: 'hello' });
  });

  it('handles new text being empty (delete all)', () => {
    expect(computeDiff('hello', '')).toEqual({ backspace: 5, insert: '' });
  });

  it('handles completely different text', () => {
    expect(computeDiff('abc', 'xyz')).toEqual({ backspace: 3, insert: 'xyz' });
  });

  it('handles single character change', () => {
    expect(computeDiff('hello', 'hallo')).toEqual({ backspace: 4, insert: 'allo' });
  });

  it('handles Unicode characters (CJK)', () => {
    expect(computeDiff('你好世界', '你好朋友')).toEqual({ backspace: 2, insert: '朋友' });
  });

  it('handles Unicode emoji', () => {
    expect(computeDiff('hello 🎉', 'hello 🎉🎊')).toEqual({ backspace: 0, insert: '🎊' });
  });

  it('handles insertion in the middle (prefix-only approach)', () => {
    // old: "ac", new: "abc"
    // prefix: "a" (1 char), backspace: 2-1=1, insert: "abc".substring(1) = "bc"
    // Note: prefix-only means we delete "c" and retype "bc" even though "c" is unchanged
    expect(computeDiff('ac', 'abc')).toEqual({ backspace: 1, insert: 'bc' });
  });

  it('handles long text', () => {
    const old = 'a'.repeat(1000) + 'b'.repeat(1000);
    const new_ = 'a'.repeat(1000) + 'c'.repeat(1000);
    expect(computeDiff(old, new_)).toEqual({ backspace: 1000, insert: 'c'.repeat(1000) });
  });
});
