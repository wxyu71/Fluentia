import { describe, it, expect } from 'vitest';
import { parseRegexMarkdown } from './regex';

describe('parseRule ReDoS protection', () => {
  it('parseRule_RejectsCatastrophicBacktracking: nested quantifiers return null', () => {
    // (a+)+ is a classic catastrophic backtracking pattern
    const md = '```regex\n(a+)+$\n```';
    // parseRule returns null for this, parseRegexMarkdown should throw
    // because no valid rules are found
    expect(() => parseRegexMarkdown(md)).toThrow('No regex rule found');
  });

  it('parseRule_AcceptsNormalRegex: \\d+ should work', () => {
    const md = '```regex\n\\d+\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
    expect(result.rules[0].source).toBe('\\d+');
    expect(result.rules[0].regex.test('123')).toBe(true);
  });

  it('parseRule_HandlesInvalidRegex: [invalid should return null', () => {
    const md = '```regex\n[invalid\n```';
    // Invalid regex should be skipped, resulting in no valid rules
    expect(() => parseRegexMarkdown(md)).toThrow('No regex rule found');
  });

  it('parseRule_RejectsTooLong: 200+ char pattern should return null', () => {
    const longPattern = 'a'.repeat(200);
    const md = '```regex\n' + longPattern + '\n```';
    expect(() => parseRegexMarkdown(md)).toThrow('No regex rule found');
  });

  it('parseRule_SkipsInvalidAndKeepsValid: mix of valid and invalid', () => {
    const md = '```regex\n[invalid\n\\d+\n```';
    const result = parseRegexMarkdown(md);
    expect(result.rules).toHaveLength(1);
    expect(result.rules[0].source).toBe('\\d+');
  });
});
