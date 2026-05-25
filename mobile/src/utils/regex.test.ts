import { describe, it, expect } from 'vitest';
import { parseRegexMarkdown, applyRegexFilters } from './regex';

describe('parseRegexMarkdown', () => {
  it('parses a single regex from a code block', () => {
    const md = '```regex\n\\b(?:um|uh)\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
    expect(result.rules[0].source).toBe('\\b(?:um|uh)\\b');
    expect(result.rules[0].flags).toContain('g');
  });

  it('parses multiple rules from one code block', () => {
    const md = '```regex\n\\bum\\b\n\\buh\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(2);
  });

  it('skips comment lines starting with #', () => {
    const md = '```regex\n# This is a comment\n\\bum\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
  });

  it('skips comment lines starting with //', () => {
    const md = '```regex\n// This is a comment\n\\bum\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
  });

  it('skips empty lines', () => {
    const md = '```regex\n\\bum\\b\n\n\\buh\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(2);
  });

  it('parses /pattern/flags syntax', () => {
    const md = '```regex\n/\\bum\\b/i\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules[0].source).toBe('\\bum\\b');
    expect(result.rules[0].flags).toContain('i');
    expect(result.rules[0].flags).toContain('g');
  });

  it('throws on no code block', () => {
    expect(() => parseRegexMarkdown('no code block here')).toThrow('No regex code block found');
  });

  it('throws on empty code block', () => {
    expect(() => parseRegexMarkdown('```regex\n\n```')).toThrow();
  });

  it('parses multiple code blocks', () => {
    const md = '```regex\n\\bum\\b\n```\n\n```regex\n\\buh\\b\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(2);
  });

  it('handles regex with special characters', () => {
    const md = '```regex\n(?<=^|[\\s,.;!?])(?:嗯+|呃+)\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
    expect(result.rules[0].regex).toBeInstanceOf(RegExp);
  });
});

describe('applyRegexFilters', () => {
  const markdown = '```regex\n\\b(?:um|uh)\\b\n```';

  it('replaces matches when enabled', () => {
    const result = applyRegexFilters('um hello uh world', markdown, true);
    expect(result).toBe('hello world');
  });

  it('returns input unchanged when disabled', () => {
    const result = applyRegexFilters('um hello uh world', markdown, false);
    expect(result).toBe('um hello uh world');
  });

  it('returns input when empty markdown', () => {
    expect(applyRegexFilters('hello', '', true)).toBe('hello');
  });

  it('returns input when empty input', () => {
    expect(applyRegexFilters('', markdown, true)).toBe('');
  });

  it('collapses multiple spaces after replacement', () => {
    const result = applyRegexFilters('um  hello  uh  world', markdown, true);
    expect(result).toBe('hello world');
  });

  it('trims leading whitespace after replacement', () => {
    const result = applyRegexFilters('um hello', markdown, true);
    expect(result).toBe('hello');
  });
});
