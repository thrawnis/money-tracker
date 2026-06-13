import { describe, it, expect } from 'vitest';

// Validates the sequence-ref guard pattern used in AccountRegister,
// TransactionSearch, and AllTransactions to prevent stale async responses
// from overwriting newer state.

async function simulateLoad(
  seq: { current: number },
  delay: number,
  value: string,
  onResult: (v: string) => void,
) {
  const capturedSeq = ++seq.current;
  await new Promise(r => setTimeout(r, delay));
  if (capturedSeq === seq.current) {
    onResult(value);
  }
}

describe('sequence guard', () => {
  it('last-issued request wins when requests complete out of order', async () => {
    const seq = { current: 0 };
    const results: string[] = [];
    const onResult = (v: string) => results.push(v);

    // Fire "accounts/1" (slow) then "accounts/2" (fast)
    const p1 = simulateLoad(seq, 50, 'accounts/1', onResult);
    const p2 = simulateLoad(seq, 10, 'accounts/2', onResult);

    await Promise.all([p1, p2]);

    // Only the second (newer) result should be committed
    expect(results).toEqual(['accounts/2']);
  });

  it('single request always commits its result', async () => {
    const seq = { current: 0 };
    const results: string[] = [];
    await simulateLoad(seq, 10, 'only-result', v => results.push(v));
    expect(results).toEqual(['only-result']);
  });

  it('three rapid requests, only the third commits', async () => {
    const seq = { current: 0 };
    const results: string[] = [];
    const onResult = (v: string) => results.push(v);

    const p1 = simulateLoad(seq, 60, 'first', onResult);
    const p2 = simulateLoad(seq, 40, 'second', onResult);
    const p3 = simulateLoad(seq, 20, 'third', onResult);

    await Promise.all([p1, p2, p3]);

    expect(results).toEqual(['third']);
  });

  it('sequential requests each commit (no overlap)', async () => {
    const seq = { current: 0 };
    const results: string[] = [];
    const onResult = (v: string) => results.push(v);

    await simulateLoad(seq, 10, 'first', onResult);
    await simulateLoad(seq, 10, 'second', onResult);

    expect(results).toEqual(['first', 'second']);
  });
});
