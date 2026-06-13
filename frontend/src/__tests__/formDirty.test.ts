import { describe, it, expect } from 'vitest';

// Validates the JSON-snapshot dirty-check pattern used in BillsReminders
// and other forms. The baseline is captured at form-open time; the form is
// dirty when the current values differ from that snapshot.

interface BillForm {
  name: string;
  amount: string;
  nextDueDate: string;
  frequencyInterval: string;
  frequencyUnit: string;
  reminderDays: string;
  accountId: string;
  payeeId: string;
  categoryId: string;
  memo: string;
  isActive: boolean;
  transferAccountId: string;
}

function emptyForm(): BillForm {
  const today = new Date().toISOString().slice(0, 10);
  return {
    name: '', amount: '', nextDueDate: today,
    frequencyInterval: '1', frequencyUnit: 'Months',
    reminderDays: '3', accountId: '', payeeId: '',
    categoryId: '', memo: '', isActive: true, transferAccountId: '',
  };
}

function isDirty(form: BillForm, baseline: string) {
  return JSON.stringify(form) !== baseline;
}

describe('form dirty check', () => {
  it('fresh form is not dirty', () => {
    const form = emptyForm();
    const baseline = JSON.stringify(form);
    expect(isDirty(form, baseline)).toBe(false);
  });

  it('changing name makes form dirty', () => {
    const form = emptyForm();
    const baseline = JSON.stringify(form);
    expect(isDirty({ ...form, name: 'Rent' }, baseline)).toBe(true);
  });

  it('restoring to baseline makes form clean', () => {
    const form = emptyForm();
    const baseline = JSON.stringify(form);
    const modified = { ...form, name: 'Rent' };
    expect(isDirty(modified, baseline)).toBe(true);
    // Simulate user clearing the field
    expect(isDirty({ ...modified, name: '' }, baseline)).toBe(false);
  });

  it('only nextDueDate being today does not make form dirty on open', () => {
    // The old bug: checking `form.nextDueDate !== ''` was always true
    // because nextDueDate defaults to today's date string (not empty).
    // The snapshot pattern fixes this: the baseline includes the default date,
    // so comparing equal snapshots yields not-dirty.
    const form = emptyForm();
    const baseline = JSON.stringify(form);
    expect(isDirty(form, baseline)).toBe(false);
    expect(form.nextDueDate).not.toBe(''); // sanity check: it IS set, not empty
  });

  it('changing amount to a number makes form dirty', () => {
    const form = emptyForm();
    const baseline = JSON.stringify(form);
    expect(isDirty({ ...form, amount: '500' }, baseline)).toBe(true);
  });

  it('editing form then loading a different bill resets baseline', () => {
    const form1 = { ...emptyForm(), name: 'Rent', amount: '1200' };
    const baseline1 = JSON.stringify(form1);

    // User navigates to a different bill — new form + new baseline
    const form2 = { ...emptyForm(), name: 'Phone', amount: '80' };
    const baseline2 = JSON.stringify(form2);

    // form2 is not dirty vs its own baseline
    expect(isDirty(form2, baseline2)).toBe(false);
    // but form2 would be dirty if we mistakenly used the old baseline
    expect(isDirty(form2, baseline1)).toBe(true);
  });
});
