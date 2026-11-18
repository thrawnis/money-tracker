/** Fixed-rate loan maths (monthly compounding), matching the server's LoanMath. */

const cents = (n: number) => Math.round(n * 100) / 100;

/** Monthly principal + interest for a loan of `principal` over `months` at `annualRate`%. */
export function loanPayment(principal: number, annualRate: number, months: number): number {
  if (principal <= 0 || months <= 0) return 0;
  if (annualRate <= 0) return cents(principal / months);
  const i = annualRate / 1200;
  return cents(principal * i / (1 - Math.pow(1 + i, -months)));
}

export const monthlyInterest = (owed: number, annualRate: number) =>
  owed <= 0 || annualRate <= 0 ? 0 : cents(owed * annualRate / 1200);

export interface ScheduleRow { n: number; date: string | null; payment: number; interest: number; principal: number; balance: number }
export interface Schedule { covers: boolean; months: number; totalInterest: number; rows: ScheduleRow[]; payoffDate: string | null }

const addMonths = (iso: string, m: number) => {
  const [y, mo, d] = iso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, mo - 1 + m, 1));
  const last = new Date(Date.UTC(dt.getUTCFullYear(), dt.getUTCMonth() + 1, 0)).getUTCDate();
  dt.setUTCDate(Math.min(d, last));
  return dt.toISOString().slice(0, 10);
};

/**
 * Month-by-month payoff of `owed` paying `payment` (P&I) + `extra` each month,
 * starting `firstDate` (YYYY-MM-DD, optional). `covers` is false when the
 * payment doesn't even cover the interest.
 */
export function payoffSchedule(owed: number, annualRate: number, payment: number, extra = 0, firstDate: string | null = null, max = 1200): Schedule {
  const pay = payment + Math.max(0, extra);
  if (owed <= 0) return { covers: true, months: 0, totalInterest: 0, rows: [], payoffDate: null };
  if (pay <= monthlyInterest(owed, annualRate)) return { covers: false, months: 0, totalInterest: 0, rows: [], payoffDate: null };
  const rows: ScheduleRow[] = [];
  let balance = owed, total = 0;
  while (balance > 0.004 && rows.length < max) {
    const interest = monthlyInterest(balance, annualRate);
    const thisPay = Math.min(pay, cents(balance + interest));
    const principal = cents(thisPay - interest);
    balance = cents(balance - principal);
    total = cents(total + interest);
    rows.push({ n: rows.length + 1, date: firstDate ? addMonths(firstDate, rows.length) : null, payment: thisPay, interest, principal, balance: Math.max(0, balance) });
  }
  return { covers: balance <= 0.004, months: rows.length, totalInterest: total, rows, payoffDate: rows.at(-1)?.date ?? null };
}
