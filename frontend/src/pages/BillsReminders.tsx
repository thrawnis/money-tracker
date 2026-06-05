import { useEffect, useState, type FormEvent } from 'react';
import {
  getScheduledTransactions,
  createScheduledTransaction,
  updateScheduledTransaction,
  deleteScheduledTransaction,
} from '../api/scheduledTransactions';
import { getAccounts } from '../api/accounts';
import { getCategories } from '../api/categories';
import { getPayees, createPayee } from '../api/payees';
import type { ScheduledTransaction, Account, Category, Payee, FrequencyUnit } from '../types';
import styles from './BillsReminders.module.css';

const FREQUENCY_UNITS: FrequencyUnit[] = ['Days', 'Weeks', 'Months', 'Years'];

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

function formatFrequency(interval: number, unit: FrequencyUnit): string {
  if (interval === 1) {
    return `Every ${unit.slice(0, -1)}`; // "Every Month", "Every Week", etc.
  }
  return `Every ${interval} ${unit}`;
}

interface FormState {
  name: string;
  accountId: string;
  payeeInput: string;
  payeeId?: number;
  categoryId: string;
  memo: string;
  amount: string;
  frequencyInterval: string;
  frequencyUnit: FrequencyUnit;
  nextDueDate: string;
  reminderDays: string;
  isActive: boolean;
}

const emptyForm = (): FormState => ({
  name: '',
  accountId: '',
  payeeInput: '',
  payeeId: undefined,
  categoryId: '',
  memo: '',
  amount: '',
  frequencyInterval: '1',
  frequencyUnit: 'Months',
  nextDueDate: new Date().toISOString().slice(0, 10),
  reminderDays: '3',
  isActive: true,
});

export default function BillsReminders() {
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

  const validate = () => {
    const e: Record<string, string> = {};
    if (!form.name) e.name = 'Name required';
    if (!form.accountId) e.accountId = 'Account required';
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
        const newP = await createPayee(form.payeeInput);
        resolvedPayeeId = newP.id;
        setPayees(prev => [...prev, newP]);
      }
      const data = {
        name: form.name,
        accountId: Number(form.accountId),
        payeeId: resolvedPayeeId,
        categoryId: form.categoryId ? Number(form.categoryId) : undefined,
        memo: form.memo || undefined,
        amount: Number(form.amount),
        frequencyInterval: Number(form.frequencyInterval),
        frequencyUnit: form.frequencyUnit,
        nextDueDate: form.nextDueDate,
        reminderDays: Number(form.reminderDays) || 0,
        isActive: form.isActive,
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
    setForm({
      name: item.name,
      accountId: String(item.accountId),
      payeeInput: item.payee?.name ?? '',
      payeeId: item.payeeId,
      categoryId: item.categoryId ? String(item.categoryId) : '',
      memo: item.memo ?? '',
      amount: String(item.amount),
      frequencyInterval: String(item.frequencyInterval),
      frequencyUnit: item.frequencyUnit,
      nextDueDate: item.nextDueDate.slice(0, 10),
      reminderDays: String(item.reminderDays),
      isActive: item.isActive,
    });
    setShowForm(true);
  };

  const handleDelete = async (id: number) => {
    if (!confirm('Delete this bill?')) return;
    await deleteScheduledTransaction(id);
    load();
  };

  const daysUntil = (dateStr: string) => {
    const due = new Date(dateStr + 'T00:00:00');
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    return Math.round((due.getTime() - today.getTime()) / 86400000);
  };

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Bills &amp; Reminders</h2>
        <button className={styles.btnPrimary} onClick={() => { setEditId(null); setForm(emptyForm()); setShowForm(s => !s); }}>
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
            <div className={styles.formField}>
              <label>Category</label>
              <select value={form.categoryId} onChange={e => setForm(f => ({ ...f, categoryId: e.target.value }))}>
                <option value="">None</option>
                {allCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
              </select>
            </div>
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
                  onChange={e => setForm(f => ({ ...f, frequencyInterval: e.target.value }))}
                />
                <select
                  className={styles.frequencyUnit}
                  value={form.frequencyUnit}
                  onChange={e => setForm(f => ({ ...f, frequencyUnit: e.target.value as FrequencyUnit }))}
                >
                  {FREQUENCY_UNITS.map(u => <option key={u} value={u}>{u}</option>)}
                </select>
              </div>
              {formErrors.frequencyInterval && <span className={styles.fieldError}>{formErrors.frequencyInterval}</span>}
            </div>
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
              <th>Name</th>
              <th>Payee</th>
              <th>Account</th>
              <th>Amount</th>
              <th>Frequency</th>
              <th>Next Due</th>
              <th>Days</th>
              <th>Active</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {items.length === 0 ? (
              <tr><td colSpan={9} className={styles.emptyMsg}>No bills found.</td></tr>
            ) : (
              items.map(item => {
                const days = daysUntil(item.nextDueDate);
                return (
                  <tr key={item.id} className={days < 0 ? styles.overdue : ''}>
                    <td>{item.name}</td>
                    <td>{item.payee?.name ?? '—'}</td>
                    <td>{item.account?.name ?? accounts.find(a => a.id === item.accountId)?.name ?? '—'}</td>
                    <td className={styles.amount}>{formatCurrency(item.amount)}</td>
                    <td>{formatFrequency(item.frequencyInterval, item.frequencyUnit)}</td>
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
