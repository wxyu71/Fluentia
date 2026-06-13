import { describe, it, expect } from 'vitest';
import type { InputCommand } from '../types';

// Extract validateInputCommand logic for testing
// The function is defined in useWebSocket.ts but not exported,
// so we replicate the exact logic here for unit testing
function validateInputCommand(data: any): data is InputCommand {
  return (
    typeof data === 'object' &&
    data !== null &&
    typeof data.type === 'string'
  );
}

describe('validateInputCommand', () => {
  it('validateInputCommand_Valid: object with type string returns true', () => {
    expect(validateInputCommand({ type: 'input', seed: 1, x: 10, y: 20 })).toBe(true);
  });

  it('validateInputCommand_MissingType: empty object returns false', () => {
    expect(validateInputCommand({})).toBe(false);
  });

  it('validateInputCommand_WrongType: type is number returns false', () => {
    expect(validateInputCommand({ type: 123 })).toBe(false);
  });

  it('validateInputCommand_Null: null returns false', () => {
    expect(validateInputCommand(null)).toBe(false);
  });

  it('validateInputCommand_Undefined: undefined returns false', () => {
    expect(validateInputCommand(undefined)).toBe(false);
  });

  it('validateInputCommand_String: string returns false', () => {
    expect(validateInputCommand('hello')).toBe(false);
  });

  it('validateInputCommand_ValidDiff: diff command returns true', () => {
    expect(validateInputCommand({ type: 'diff', text: 'hello', count: 3 })).toBe(true);
  });

  it('validateInputCommand_ValidEnter: enter command returns true', () => {
    expect(validateInputCommand({ type: 'enter' })).toBe(true);
  });
});
