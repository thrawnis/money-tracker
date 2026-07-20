import { useEffect, useState, type FormEvent } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import {
  getScheduledTransactions,
  createScheduledTransaction,
  updateScheduledTransaction,
  deleteScheduledTransaction,
} from '../api/scheduledTransactions';
import { getAccounts } from '../api/accounts';
import { getCategories, createCategory } from '../api/categories';
import { getPayees, createPayee } from '../api/payees';
import type { ScheduledTransaction, Account, Category, Payee, FrequencyUnit } from '../types';
import styles from './BillsReminders.module.css';

const FREQUENCY_UNITS: FrequencyUnit[] = ['Days', 'Weeks', 'Months', 'Years'];

// Bit N = JS Date.getDay() value N (Sunday=0 → bit 1, ... Saturday=6 → bit 64).
const WEEKDAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

const maskFromDays = (days: boolean[]): number | undefined => {
  const mask = days.reduce((m, checked, i) => checked ? m | (1 << i) : m, 0);
  return mask || undefined; // 0 means "none selected" — treat as unset
};

const daysFromMask = (mask?: number): boolean[] =>
  Array.from({ length: 7 }, (_, i) => !!(mask && (mask & (1 << i))));

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

function formatFrequency(interval: number, unit: FrequencyUnit, daysOfWeekMask?: number): string {
  if (unit === 'Weeks' && daysOfWeekMask) {
    const days = WEEKDAY_LABELS.filter((_, i) => daysOfWeekMask & (1 << i));
    return `Every ${days.join('/')}`;
  }
  if (interval === 1) {
    return `Every ${unit.slice(0, -1)}`; // "Every Month", "Every Week", etc.
  }
  return `Every ${interval} ${unit}`;
}

type SortColumn = 'name' | 'payee' | 'account' | 'amount' | 'frequency' | 'nextDueDate' | 'days' | 'isActive';

// Rough day-equivalent so Frequency sorts by how often it recurs rather than
// alphabetically ("Every 2 Weeks" vs "Every Month" should compare sensibly).
const FREQUENCY_UNIT_DAYS: Record<FrequencyUnit, number> = { Days: 1, Weeks: 7, Months: 30, Years: 365 };
function frequencyDays(item: ScheduledTransaction): number {
  return item.frequencyInterval * FREQUENCY_UNIT_DAYS[item.frequencyUnit];
}

function payeeLabel(item: ScheduledTransaction, accounts: Account[]): string {
  return item.transferAccountId
    ? `Transfer → ${accounts.find(a => a.id === item.transferAccountId)?.name ?? 'account'}`
    : (item.payee?.name ?? '');
}

interface FormState {
  name: string;
  accountId: string;
  isTransfer: boolean;
  transferAccountId: string;
  payeeInput: string;
  payeeId?: number;
  categoryInput: string;
  categoryId?: number;
  memo: string;
  amount: string;
  frequencyInterval: string;
  frequencyUnit: FrequencyUnit;
  /** Only used when frequencyUnit === 'Weeks'; index i = WEEKDAY_LABELS[i]. */
  daysOfWeek: boolean[];
  nextDueDate: string;
  reminderDays: string;
  isActive: boolean;
}

// Passed via navigate('/bills', { state: { prefill } }) from a transaction's
// "Make Recurring…" context menu action in AccountRegister.
export interface RecurringPrefill {
  accountId: number;
  isTransfer: boolean;
  transferAccountId?: number;
  payeeId?: number;
  payeeName?: string;
  categoryId?: number;
  memo?: string;
  amount: number;
  nextDueDate: string;
}

const formFromPrefill = (p: RecurringPrefill, allCategories: { id: number; label: string }[]): FormState => ({
  ...emptyForm(),
  accountId: String(p.accountId),
  isTransfer: p.isTransfer,
  transferAccountId: p.transferAccountId ? String(p.transferAccountId) : '',
  payeeInput: p.isTransfer ? '' : (p.payeeName ?? ''),
  payeeId: p.isTransfer ? undefined : p.payeeId,
  categoryInput: !p.isTransfer && p.categoryId ? (allCategories.find(c => c.id === p.categoryId)?.label ?? '') : '',
  categoryId: p.isTransfer ? undefined : p.categoryId,
  memo: p.memo ?? '',
  amount: String(p.amount),
  nextDueDate: p.nextDueDate.slice(0, 10),
});

const emptyForm = (): FormState => ({
  name: '',
  accountId: '',
  isTransfer: false,
  transferAccountId: '',
  payeeInput: '',
  payeeId: undefined,
  categoryInput: '',
  categoryId: undefined,
  memo: '',
  amount: '',
  frequencyInterval: '1',
  frequencyUnit: 'Months',
  daysOfWeek: [false, false, false, false, false, false, false],
  nextDueDate: new Date().toISOString().slice(0, 10),
  reminderDays: '3',
  isActive: true,
});

export default function BillsReminders() {
  usePageTitle('Bills & Reminders');
  const location = useLocation();
  const navigate = useNavigate();
  const [items, setItems] = useState<ScheduledTransaction[]>([]);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [payees, setPayees] = useState<Payee[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showForm, setShowForm] = useState(false);
  const [editId, setEditId] = useState<number | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm());
  const [formErrors, setFormErrors] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);
  const [payeeSuggestions, setPayeeSuggestions] = useState<Payee[]>([]);
  const [categorySuggestions, setCategorySuggestions] = useState<{ id: number; label: string }[]>([]);
  const [showCatSugg, setShowCatSugg] = useState(false);
  const [sortBy, setSortBy] = useState<SortColumn>('nextDueDate');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  // Dirty = form differs from its snapshot at open time (an untouched form,
  // new or editing, is not dirty — emptyForm() pre-fills nextDueDate)
  const [formBaseline, setFormBaseline] = useState(() => JSON.stringify(emptyForm()));
  const formDirty = showForm && !saving && JSON.stringify(form) !== formBaseline;
  useUnsavedChanges(formDirty);
  const [showSugg, setShowSugg] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      const [bills, accs, cats, pays] = await Promise.all([
        getScheduledTransactions(),
        getAccounts(),
        getCategories(),
        getPayees(),
      ]);
      setItems(bills);
      setAccounts(accs);
      setCategories(cats);
      setPayees(pays);
    } catch {
      setError('Failed to load bills.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const allCategories: { id: number; label: string }[] = [];
  for (const cat of categories) {
    allCategories.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        allCategories.push({ id: sub.id, label: `${cat.name} : ${sub.name}` });
      }
    }
  }

  // Arriving from a transaction's "Make Recurring…" action — open the New
  // Bill form pre-filled once categories are loaded (needed to resolve the
  // category label), then clear the nav state so a refresh/back doesn't
  // reopen it.
  useEffect(() => {
    const prefill = (location.state as { prefill?: RecurringPrefill } | null)?.prefill;
    if (!prefill || loading) return;
    setEditId(null);
    const f = formFromPrefill(prefill, allCategories);
    setForm(f);
    setFormBaseline(JSON.stringify(f));
    setShowForm(true);
    navigate('.', { replace: true, state: null });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loading, location.state]);

  const handleCategoryInput = (val: string) => {
    setForm(f => ({ ...f, categoryInput: val, categoryId: undefined }));
    if (val.length >= 1) {
      const sug = allCategories.filter(c => c.label.toLowerCase().includes(val.toLowerCase())).slice(0, 10);
      setCategorySuggestions(sug);
      setShowCatSugg(true);
    } else {
      setShowCatSugg(false);
    }
  };

  const selectCategory = (c: { id: number; label: string }) => {
    setForm(f => ({ ...f, categoryInput: c.label, categoryId: c.id }));
    setShowCatSugg(false);
  };

  // Same find-or-create-on-the-fly behavior as TransactionForm: an exact
  // label match resolves to its id; "Parent: Sub" creates whichever parts
  // don't already exist; a plain name creates a new top-level category.
  const resolveCategoryInput = async (input: string): Promise<number | undefined> => {
    const trimmed = input.trim();
    if (!trimmed) return undefined;

    const flat = allCategories;
    const exact = flat.find(c => c.label.toLowerCase() === trimmed.toLowerCase());
    if (exact) return exact.id;

    const colonIdx = trimmed.indexOf(':');
    if (colonIdx > 0) {
      const parentName = trimmed.slice(0, colonIdx).trim();
      const subName = trimmed.slice(colonIdx + 1).trim();

      let parent = categories.find(c => c.name.toLowerCase() === parentName.toLowerCase());
      if (!parent) {
        parent = await createCategory({ name: parentName, parentId: undefined });
        setCategories(prev => [...prev, { ...parent!, subCategories: [] }]);
      }

      const existingSub = parent.subCategories?.find(s => s.name.toLowerCase() === subName.toLowerCase());
      if (existingSub) return existingSub.id;

      const newSub = await createCategory({ name: subName, parentId: parent.id });
      setCategories(prev => prev.map(c =>
        c.id === parent!.id ? { ...c, subCategories: [...(c.subCategories ?? []), newSub] } : c
      ));
      return newSub.id;
    }

    const newCat = await createCategory({ name: trimmed, parentId: undefined });
    setCategories(prev => [...prev, { ...newCat, subCategories: [] }]);
    return newCat.id;
  };

  const validate = () => {
    const e: Record<string, string> = {};
    if (!form.name) e.name = 'Name required';
    if (!form.accountId) e.accountId = 'Account required';
    if (form.isTransfer && !form.transferAccountId) e.transferAccountId = 'Destination account required';
    if (form.isTransfer && form.transferAccountId === form.accountId) e.transferAccountId = 'Source and destination must differ';
    if (!form.amount || isNaN(Number(form.amount))) e.amount = 'Valid amount required';
    if (!form.nextDueDate) e.nextDueDate = 'Due date required';
    const interval = Number(form.frequencyInterval);
    if (!Number.isInteger(interval) || interval < 1) e.frequencyInterval = 'Must be a whole number ≥ 1';
    setFormErrors(e);
    return Object.keys(e).length === 0;
  };

  const handlePayeeInput = (val: string) => {
    setForm(f => ({ ...f, payeeInput: val, payeeId: undefined }));
    if (val.length >= 1) {
      const sug = payees.filter(p => p.name.toLowerCase().includes(val.toLowerCase())).slice(0, 8);
      setPayeeSuggestions(sug);
      setShowSugg(true);
    } else {
      setShowSugg(false);
    }
  };

  const selectPayee = (p: Payee) => {
    setForm(f => ({ ...f, payeeInput: p.name, payeeId: p.id }));
    setShowSugg(false);
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setSaving(true);
    try {
      let resolvedPayeeId = form.payeeId;
      if (form.payeeInput && !resolvedPayeeId) {
        try {
          const newP = await createPayee(form.payeeInput);
          resolvedPayeeId = newP.id;
          setPayees(prev => [...prev, newP]);
        } catch (payeeErr: unknown) {
          const msg = (payeeErr as { response?: { data?: { message?: string } } })?.response?.data?.message;
          setFormErrors(prev => ({ ...prev, submit: msg ?? 'Failed to create payee.' }));
          setSaving(false);
          return;
        }
      }
      let resolvedCategoryId = form.categoryId;
      if (form.categoryInput && !resolvedCategoryId) {
        try {
          resolvedCategoryId = await resolveCategoryInput(form.categoryInput);
        } catch (catErr: unknown) {
          const msg = (catErr as { response?: { data?: { message?: string } } })?.response?.data?.message;
          setFormErrors(prev => ({ ...prev, submit: msg ?? 'Failed to create category.' }));
          setSaving(false);
          return;
        }
      }
      const data = {
        name: form.name,
        accountId: Number(form.accountId),
        payeeId: form.isTransfer ? undefined : resolvedPayeeId,
        categoryId: form.isTransfer ? undefined : resolvedCategoryId,
        memo: form.memo || undefined,
        amount: Number(form.amount),
        frequencyInterval: Number(form.frequencyInterval),
        frequencyUnit: form.frequencyUnit,
        daysOfWeekMask: form.frequencyUnit === 'Weeks' ? maskFromDays(form.daysOfWeek) : undefined,
        nextDueDate: form.nextDueDate,
        reminderDays: Number(form.reminderDays) || 0,
        isActive: form.isActive,
        transferAccountId: form.isTransfer && form.transferAccountId ? Number(form.transferAccountId) : undefined,
      };
      if (editId !== null) {
        await updateScheduledTransaction(editId, data);
      } else {
        await createScheduledTransaction(data);
      }
      setShowForm(false);
      setEditId(null);
      setForm(emptyForm());
      load();
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setFormErrors({ submit: msg ?? 'Save failed.' });
    } finally {
      setSaving(false);
    }
  };

  const handleEdit = (item: ScheduledTransaction) => {
    setEditId(item.id);
    const f: FormState = {
      name: item.name,
      accountId: String(item.accountId),
      isTransfer: !!item.transferAccountId,
      transferAccountId: item.transferAccountId ? String(item.transferAccountId) : '',
      payeeInput: item.payee?.name ?? '',
      payeeId: item.payeeId,
      categoryInput: item.categoryId ? (allCategories.find(c => c.id === item.categoryId)?.label ?? '') : '',
      categoryId: item.categoryId,
      memo: item.memo ?? '',
      amount: String(item.amount),
      frequencyInterval: String(item.frequencyInterval),
      frequencyUnit: item.frequencyUnit,
      daysOfWeek: daysFromMask(item.daysOfWeekMask),
      nextDueDate: item.nextDueDate.slice(0, 10),
      reminderDays: String(item.reminderDays),
      isActive: item.isActive,
    };
    setForm(f);
    setFormBaseline(JSON.stringify(f));
    setShowForm(true);
  };

  const handleDelete = async (id: number) => {
    if (!confirm(
      'Delete this recurring bill/reminder?\n\n' +
      "This stops it from creating any new occurrences, but won't touch transactions " +
      "already created from it (past or future-dated) — those stay in your register " +
      'exactly as they are.'
    )) return;
    try {
      await deleteScheduledTransaction(id);
      load();
    } catch {
      setError('Failed to delete bill.');
    }
  };

  const daysUntil = (dateStr: string) => {
    const due = new Date(dateStr + 'T00:00:00');
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    return Math.round((due.getTime() - today.getTime()) / 86400000);
  };

  const handleSort = (col: SortColumn) => {
    if (sortBy === col) {
      setSortDir(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortBy(col);
      setSortDir('asc');
    }
  };

  const sortedItems = [...items].sort((a, b) => {
    let cmp = 0;
    switch (sortBy) {
      case 'name': cmp = a.name.localeCompare(b.name); break;
      case 'payee': cmp = payeeLabel(a, accounts).localeCompare(payeeLabel(b, accounts)); break;
      case 'account': {
        const an = a.account?.name ?? accounts.find(x => x.id === a.accountId)?.name ?? '';
        const bn = b.account?.name ?? accounts.find(x => x.id === b.accountId)?.name ?? '';
        cmp = an.localeCompare(bn);
        break;
      }
      case 'amount': cmp = a.amount - b.amount; break;
      case 'frequency': cmp = frequencyDays(a) - frequencyDays(b); break;
      case 'nextDueDate': cmp = a.nextDueDate.localeCompare(b.nextDueDate); break;
      case 'days': cmp = daysUntil(a.nextDueDate) - daysUntil(b.nextDueDate); break;
      case 'isActive': cmp = Number(a.isActive) - Number(b.isActive); break;
    }
    return sortDir === 'desc' ? -cmp : cmp;
  });

  const sortHeader = (col: SortColumn, label: string) => (
    <th className={styles.sortable} onClick={() => handleSort(col)}>
      {label}
      {sortBy === col
        ? <span className={styles.sortActive}>{sortDir === 'desc' ? ' ▼' : ' ▲'}</span>
        : <span className={styles.sortIdle}> ⇅</span>}
    </th>
  );

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Bills &amp; Reminders</h2>
        <button className={styles.btnPrimary} onClick={() => { setEditId(null); const f = emptyForm(); setForm(f); setFormBaseline(JSON.stringify(f)); setShowForm(s => !s); }}>
          {showForm && editId === null ? 'Cancel' : '+ Add Bill'}
        </button>
      </div>

      {error && <div className={styles.errorMsg}>{error}</div>}

      {showForm && (
        <form onSubmit={handleSubmit} className={styles.formPanel}>
          <h3 className={styles.formTitle}>{editId !== null ? 'Edit Bill' : 'New Bill'}</h3>
          {formErrors.submit && <div className={styles.formError}>{formErrors.submit}</div>}
          <div className={styles.formGrid}>
            <div className={styles.formField}>
              <label>Name *</label>
              <input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} />
              {formErrors.name && <span className={styles.fieldError}>{formErrors.name}</span>}
            </div>
            <div className={styles.formField}>
              <label>Account *</label>
              <select value={form.accountId} onChange={e => setForm(f => ({ ...f, accountId: e.target.value }))}>
                <option value="">Select…</option>
                {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
              </select>
              {formErrors.accountId && <span className={styles.fieldError}>{formErrors.accountId}</span>}
            </div>
            <div className={styles.formField} style={{ gridColumn: '1 / -1' }}>
              <label>
                <input
                  type="checkbox"
                  checked={form.isTransfer}
                  onChange={e => setForm(f => ({ ...f, isTransfer: e.target.checked, transferAccountId: '', payeeInput: '', payeeId: undefined, categoryInput: '', categoryId: undefined }))}
                />
                {' '}Scheduled transfer between accounts
              </label>
            </div>
            {form.isTransfer ? (
              <div className={styles.formField}>
                <label>Destination Account *</label>
                <select
                  value={form.transferAccountId}
                  onChange={e => setForm(f => ({ ...f, transferAccountId: e.target.value }))}
                >
                  <option value="">Select…</option>
                  {accounts
                    .filter(a => a.isActive && String(a.id) !== form.accountId)
                    .sort((a, b) => a.name.localeCompare(b.name))
                    .map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                </select>
                {formErrors.transferAccountId && <span className={styles.fieldError}>{formErrors.transferAccountId}</span>}
              </div>
            ) : (
              <div className={styles.formField} style={{ position: 'relative' }}>
                <label>Payee</label>
                <input
                  value={form.payeeInput}
                  onChange={e => handlePayeeInput(e.target.value)}
                  onBlur={() => setTimeout(() => setShowSugg(false), 150)}
                  autoComplete="off"
                />
                {showSugg && payeeSuggestions.length > 0 && (
                  <ul className={styles.suggestions}>
                    {payeeSuggestions.map(p => (
                      <li key={p.id} onMouseDown={() => selectPayee(p)} className={styles.suggestion}>{p.name}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}
            {!form.isTransfer && (
              <div className={styles.formField} style={{ position: 'relative' }}>
                <label>Category</label>
                <input
                  value={form.categoryInput}
                  onChange={e => handleCategoryInput(e.target.value)}
                  onBlur={() => setTimeout(() => setShowCatSugg(false), 150)}
                  autoComplete="off"
                  placeholder="e.g. Food or Food: Groceries"
                />
                {showCatSugg && categorySuggestions.length > 0 && (
                  <ul className={styles.suggestions}>
                    {categorySuggestions.map(c => (
                      <li key={c.id} onMouseDown={() => selectCategory(c)} className={styles.suggestion}>{c.label}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}
            <div className={styles.formField}>
              <label>Amount *</label>
              <input type="number" step="0.01" value={form.amount} onChange={e => setForm(f => ({ ...f, amount: e.target.value }))} />
              {formErrors.amount && <span className={styles.fieldError}>{formErrors.amount}</span>}
            </div>
            <div className={styles.formField}>
              <label>Frequency</label>
              <div className={styles.frequencyRow}>
                <span className={styles.frequencyEvery}>Every</span>
                <input
                  type="number"
                  min="1"
                  className={styles.frequencyInterval}
                  value={form.frequencyInterval}
                  disabled={form.frequencyUnit === 'Weeks' && form.daysOfWeek.some(Boolean)}
                  onChange={e => setForm(f => ({ ...f, frequencyInterval: e.target.value }))}
                />
                <select
                  className={styles.frequencyUnit}
                  value={form.frequencyUnit}
                  onChange={e => setForm(f => ({
                    ...f,
                    frequencyUnit: e.target.value as FrequencyUnit,
                    // Only meaningful for Weeks — clear so it can't linger
                    // stale on a schedule switched to Days/Months/Years.
                    daysOfWeek: e.target.value === 'Weeks' ? f.daysOfWeek : [false, false, false, false, false, false, false],
                  }))}
                >
                  {FREQUENCY_UNITS.map(u => <option key={u} value={u}>{u}</option>)}
                </select>
              </div>
              {formErrors.frequencyInterval && <span className={styles.fieldError}>{formErrors.frequencyInterval}</span>}
            </div>
            {form.frequencyUnit === 'Weeks' && (
              <div className={styles.formField} style={{ gridColumn: '1 / -1' }}>
                <label>Or on specific weekdays (overrides "Every N Weeks" above)</label>
                <div className={styles.weekdayRow}>
                  {WEEKDAY_LABELS.map((label, i) => (
                    <button
                      key={label}
                      type="button"
                      className={`${styles.weekdayToggle} ${form.daysOfWeek[i] ? styles.weekdayToggleActive : ''}`}
                      onClick={() => setForm(f => {
                        const next = [...f.daysOfWeek];
                        next[i] = !next[i];
                        return { ...f, daysOfWeek: next };
                      })}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </div>
            )}
            <div className={styles.formField}>
              <label>Next Due Date *</label>
              <input type="date" value={form.nextDueDate} onChange={e => setForm(f => ({ ...f, nextDueDate: e.target.value }))} />
              {formErrors.nextDueDate && <span className={styles.fieldError}>{formErrors.nextDueDate}</span>}
            </div>
            <div className={styles.formField}>
              <label>Reminder Days</label>
              <input type="number" min="0" value={form.reminderDays} onChange={e => setForm(f => ({ ...f, reminderDays: e.target.value }))} />
            </div>
            <div className={styles.formField}>
              <label>Memo</label>
              <input value={form.memo} onChange={e => setForm(f => ({ ...f, memo: e.target.value }))} />
            </div>
            <div className={styles.formField} style={{ justifyContent: 'flex-end' }}>
              <label>
                <input type="checkbox" checked={form.isActive} onChange={e => setForm(f => ({ ...f, isActive: e.target.checked }))} />
                {' '}Active
              </label>
            </div>
          </div>
          <div className={styles.formActions}>
            <button type="submit" className={styles.btnPrimary} disabled={saving}>
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button type="button" className={styles.btnSecondary} onClick={() => { setShowForm(false); setEditId(null); }}>
              Cancel
            </button>
          </div>
        </form>
      )}

      {loading ? (
        <p>Loading…</p>
      ) : (
        <table className={styles.table}>
          <thead>
            <tr>
              {sortHeader('name', 'Name')}
              {sortHeader('payee', 'Payee')}
              {sortHeader('account', 'Account')}
              {sortHeader('amount', 'Amount')}
              {sortHeader('frequency', 'Frequency')}
              {sortHeader('nextDueDate', 'Next Due')}
              {sortHeader('days', 'Days')}
              {sortHeader('isActive', 'Active')}
              <th></th>
            </tr>
          </thead>
          <tbody>
            {sortedItems.length === 0 ? (
              <tr><td colSpan={9} className={styles.emptyMsg}>No bills found.</td></tr>
            ) : (
              sortedItems.map(item => {
                const days = daysUntil(item.nextDueDate);
                return (
                  <tr key={item.id} className={days < 0 ? styles.overdue : ''}>
                    <td>{item.name}</td>
                    <td>
                      {item.transferAccountId
                        ? <span className={styles.transferLabel}>
                            Transfer → {accounts.find(a => a.id === item.transferAccountId)?.name ?? 'account'}
                          </span>
                        : (item.payee?.name ?? '—')}
                    </td>
                    <td>{item.account?.name ?? accounts.find(a => a.id === item.accountId)?.name ?? '—'}</td>
                    <td className={styles.amount}>{formatCurrency(item.amount)}</td>
                    <td>{formatFrequency(item.frequencyInterval, item.frequencyUnit, item.daysOfWeekMask)}</td>
                    <td>{formatDate(item.nextDueDate)}</td>
                    <td className={days < 0 ? styles.overdueText : ''}>{days < 0 ? 'Overdue' : `${days}d`}</td>
                    <td>{item.isActive ? 'Yes' : 'No'}</td>
                    <td className={styles.actions}>
                      <button className={styles.btnEdit} onClick={() => handleEdit(item)}>Edit</button>
                      <button className={styles.btnDelete} onClick={() => handleDelete(item.id)}>Del</button>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      )}
    </div>
  );
}
