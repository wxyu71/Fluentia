import { describe, it, expect } from 'vitest';

// Replicate sanitizeFileName logic from useWebSocket.ts for unit testing
function sanitizeFileName(fileName: string): string {
  return fileName.replace(/[/\\]/g, '_').replace(/^\.+/, '');
}

describe('sanitizeFileName', () => {
  it('sanitizeFileName_PathTraversal: path separators replaced and leading dots stripped', () => {
    const result = sanitizeFileName('../../etc/passwd');
    // After replacing / with _: _.._.._etc_passwd
    // After stripping leading dots: _.._.._etc_passwd (starts with _)
    expect(result).not.toContain('/');
    expect(result).not.toContain('\\');
    // Leading dots are stripped, but internal dots remain
    expect(result.startsWith('.')).toBe(false);
  });

  it('sanitizeFileName_NormalFile: test.txt stays test.txt', () => {
    expect(sanitizeFileName('test.txt')).toBe('test.txt');
  });

  it('sanitizeFileName_Dots: ...hidden becomes hidden', () => {
    expect(sanitizeFileName('...hidden')).toBe('hidden');
  });

  it('sanitizeFileName_BackslashTraversal: backslashes replaced with underscores', () => {
    const result = sanitizeFileName('..\\..\\windows\\system32');
    expect(result).not.toContain('\\');
    expect(result).not.toContain('/');
    expect(result.startsWith('.')).toBe(false);
  });

  it('sanitizeFileName_SingleDot: .hidden becomes hidden', () => {
    expect(sanitizeFileName('.hidden')).toBe('hidden');
  });

  it('sanitizeFileName_MixedPath: slashes replaced with underscores', () => {
    const result = sanitizeFileName('/foo/bar.txt');
    expect(result).not.toContain('/');
    expect(result).not.toContain('\\');
    expect(result).toContain('foo');
    expect(result).toContain('bar.txt');
  });

  it('sanitizeFileName_EmptyString: empty stays empty', () => {
    expect(sanitizeFileName('')).toBe('');
  });
});
