/**
 * Tests for emoji and Unicode handling in diff computation.
 *
 * BUG: Flag emojis (🇺🇸) consist of 2 regional indicator symbols,
 * each outside BMP = 4 UTF-16 code units. computeDiff uses String.length
 * which returns 4, but Windows SendInput with VK_BACK deletes one
 * grapheme cluster per backspace. So 4 backspaces delete the flag
 * plus 3 preceding characters.
 *
 * FIX: Use Intl.Segmenter to count grapheme clusters for backspace.
 */

import { describe, it, expect } from 'vitest';
import { computeDiff } from './diff';

describe('computeDiff — emoji and Unicode', () => {
  describe('ASCII (baseline)', () => {
    it('simple deletion', () => {
      const diff = computeDiff('hello world', 'hello');
      expect(diff.backspace).toBe(6);
      expect(diff.insert).toBe('');
    });

    it('simple insertion', () => {
      const diff = computeDiff('hello', 'hello world');
      expect(diff.backspace).toBe(0);
      expect(diff.insert).toBe(' world');
    });
  });

  describe('CJK characters', () => {
    it('CJK deletion', () => {
      const diff = computeDiff('你好世界', '你好');
      expect(diff.backspace).toBe(2);
      expect(diff.insert).toBe('');
    });
  });

  describe('Emoji — single code point', () => {
    it('simple emoji insertion', () => {
      const diff = computeDiff('hello', 'hello🎉');
      expect(diff.backspace).toBe(0);
      // 🎉 is 1 code point but 2 UTF-16 code units
      expect(diff.insert).toBe('🎉');
    });

    it('simple emoji deletion', () => {
      const diff = computeDiff('hello🎉', 'hello');
      // 🎉 is 1 grapheme cluster — backspace should be 1
      expect(diff.backspace).toBe(1);
    });
  });

  describe('Flag emoji — the critical bug', () => {
    it('flag emoji has 4 UTF-16 code units but 1 grapheme cluster', () => {
      // 🇺🇸 = U+1F1FA U+1F1F8 (2 regional indicators)
      // Each is outside BMP = 2 UTF-16 code units each = 4 total
      const flag = '🇺🇸';
      expect(flag.length).toBe(4); // UTF-16 code units
    });

    it('FIX: deleting flag emoji sends 1 backspace (grapheme cluster)', () => {
      const oldText = 'hello🇺🇸';
      const newText = 'hello';
      const diff = computeDiff(oldText, newText);

      // 🇺🇸 is 1 grapheme cluster — backspace = 1
      expect(diff.backspace).toBe(1);
    });

    it('FIX: deleting flag from middle of text works correctly', () => {
      const oldText = 'abc🇺🇸def';
      const newText = 'abcdef';
      const diff = computeDiff(oldText, newText);

      // Common prefix: 'abc' (3 UTF-16 chars)
      // suffix = '🇺🇸def' — grapheme count = 1 (flag) + 3 (def) = 4
      expect(diff.backspace).toBe(4);
    });

    it('FIX: multiple flag emojis — each is 1 grapheme cluster', () => {
      const oldText = '🇺🇸🇬🇧';
      const newText = '';
      const diff = computeDiff(oldText, newText);

      // 2 flag emojis = 2 grapheme clusters
      expect(diff.backspace).toBe(2);
    });
  });

  describe('ZWJ sequences — family emoji', () => {
    it('family emoji is multiple code points joined by ZWJ', () => {
      // 👨‍👩‍👧‍👦 = 👨 ZWJ 👩 ZWJ 👧 ZWJ 👦 = 7 code points = 11 UTF-16 units
      const family = '👨‍👩‍👧‍👦';
      expect(family.length).toBe(11);
    });

    it('FIX: deleting family emoji sends 1 backspace (1 grapheme cluster)', () => {
      const oldText = 'hello👨‍👩‍👧‍👦';
      const newText = 'hello';
      const diff = computeDiff(oldText, newText);

      // 👨‍👩‍👧‍👦 is 1 grapheme cluster — backspace = 1
      expect(diff.backspace).toBe(1);
    });
  });

  describe('Skin tone modifiers', () => {
    it('skin tone emoji is 2 code points', () => {
      // 👋🏽 = 👋 + 🏽 = 2 code points = 4 UTF-16 units
      const wave = '👋🏽';
      expect(wave.length).toBe(4);
    });

    it('FIX: deleting skin tone emoji sends 1 backspace', () => {
      const oldText = 'hi👋🏽';
      const newText = 'hi';
      const diff = computeDiff(oldText, newText);

      // 👋🏽 is 1 grapheme cluster — backspace = 1
      expect(diff.backspace).toBe(1);
    });
  });
});
