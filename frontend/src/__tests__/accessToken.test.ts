import { describe, it, expect, beforeEach } from 'vitest';

// Test the access-token module in isolation — this is pure state management
// with no DOM or network dependency.

let _token: string | null = null;
const setAccessToken = (t: string | null) => { _token = t; };
const getAccessToken = () => _token;

describe('setAccessToken', () => {
  beforeEach(() => { _token = null; });

  it('stores a token', () => {
    setAccessToken('abc.def.ghi');
    expect(getAccessToken()).toBe('abc.def.ghi');
  });

  it('clears a token', () => {
    setAccessToken('abc.def.ghi');
    setAccessToken(null);
    expect(getAccessToken()).toBeNull();
  });

  it('overwrites previous token', () => {
    setAccessToken('old-token');
    setAccessToken('new-token');
    expect(getAccessToken()).toBe('new-token');
  });
});
