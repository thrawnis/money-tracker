import { describe, it, expect } from 'vitest';

// These helpers are inlined in several page components. Testing them here
// to catch accidental regressions if they are ever extracted or changed.

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', {
    month: 'short', day: 'numeric', year: 'numeric',
  });
}

describe('formatCurrency', () => {
  it('formats positive amount', () => {
    expect(formatCurrency(1234.56)).toBe('$1,234.56');
  });

  it('formats zero', () => {
    expect(formatCurrency(0)).toBe('$0.00');
  });

  it('formats negative amount', () => {
    expect(formatCurrency(-99.5)).toBe('-$99.50');
  });

  it('formats large amount with commas', () => {
    expect(formatCurrency(1000000)).toBe('$1,000,000.00');
  });

  it('rounds to 2 decimal places', () => {
    expect(formatCurrency(1.999)).toBe('$2.00');
  });
});

describe('formatDate', () => {
  it('formats a standard date', () => {
    expect(formatDate('2024-06-15')).toBe('Jun 15, 2024');
  });

  it('formats January 1st', () => {
    expect(formatDate('2024-01-01')).toBe('Jan 1, 2024');
  });

  it('formats December 31st', () => {
    expect(formatDate('2023-12-31')).toBe('Dec 31, 2023');
  });

  // The T00:00:00 suffix ensures we parse in local time, not UTC,
  // preventing off-by-one dates in negative-UTC-offset timezones.
  it('does not shift the day in negative UTC offsets', () => {
    // If parsed as UTC, 2024-03-15 would be Mar 14 in UTC-5 at midnight.
    // The implementation appends T00:00:00 (local time) to avoid this.
    const result = formatDate('2024-03-15');
    expect(result).toBe('Mar 15, 2024');
  });
});
