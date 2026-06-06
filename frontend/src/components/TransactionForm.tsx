import { useState, useEffect, useRef, type FormEvent } from 'react';
import type { Transaction, Category, Payee, Account } from '../types';
import { getCategories } from '../api/categories';
import { getPayees, createPayee } from '../api/payees';
import styles from './TransactionForm.module.css';

interface Props {
  accountId: number;
  accounts?: Account[];
  initial?: Partial<Transaction>;
  onSave: (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'> & { targetAccountId?: number }) => Promise<void>;
  onCancel: () => void;
}

export default function TransactionForm({ accountId: _accountId, accounts, initial, onSave, onCancel }: Props) {
  const today = new Date().toISOString().slice(0, 10);
  const [date, setDate] = useState(initial?.date ?? today);
  const [payeeInput, setPayeeInput] = useState(initial?.payee?.name ?? '');
  const [payeeId, setPayeeId] = useState<number | undefined>(initial?.payeeId);
  const [categoryId, setCategoryId] = useState<number | undefined>(initial?.categoryId);
  const [subCategoryId, setSubCategoryId] = useState<number | undefined>(undefined);
  const [memo, setMemo] = useState(initial?.memo ?? '');
  const [amount, setAmount] = useState(initial?.amount?.toString() ?? '');
  const [status] = useState<Transaction['status']>(initial?.status ?? 'Uncleared');

  const [categories, setCategories] = useState<Category[]>([]);
  const [payees, setPayees] = useState<Payee[]>([]);
  const [payeeSuggestions, setPayeeSuggestions] = useState<Payee[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);

  const [targetAccountId, setTargetAccountId] = useState<number | undefined>(undefined);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const payeeRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    getCategories().then(setCategories).catch(console.error);
    getPayees().then(setPayees).catch(console.error);
  }, []);

  // Flatten categories for dropdown
  const allCategories: { id: number; label: string; parentId?: number }[] = [];
  for (const cat of categories) {
    allCategories.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        allCategories.push({ id: sub.id, label: `  ${cat.name} : ${sub.name}`, parentId: cat.id });
      }
    }
  }

  const topCategories = categories;
  const selectedParent = topCategories.find(c => c.id === categoryId);
  const subCategories = selectedParent?.subCategories ?? [];

  const handlePayeeInput = (val: string) => {
    setPayeeInput(val);
    setPayeeId(undefined);
    if (val.length >= 1) {
      const filtered = payees.filter(p => p.name.toLowerCase().includes(val.toLowerCase()));
      setPayeeSuggestions(filtered.slice(0, 8));
      setShowSuggestions(true);
    } else {
      setShowSuggestions(false);
    }
  };

  const selectPayee = (p: Payee) => {
    setPayeeInput(p.name);
    setPayeeId(p.id);
    if (p.defaultCategoryId) {
      const parent = categories.find(c => c.id === p.defaultCategoryId || c.subCategories?.some(s => s.id === p.defaultCategoryId));
      if (parent) {
        if (parent.id === p.defaultCategoryId) {
          setCategoryId(p.defaultCategoryId);
        } else {
          setCategoryId(parent.id);
          setSubCategoryId(p.defaultCategoryId);
        }
      }
    }
    setShowSuggestions(false);
  };

  const validate = () => {
    const e: Record<string, string> = {};
    if (!date) e.date = 'Date required';
    if (!amount || isNaN(Number(amount))) e.amount = 'Valid amount required';
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setSubmitting(true);

    let resolvedPayeeId = payeeId;
    if (payeeInput && !resolvedPayeeId) {
      // silently create payee
      try {
        const newPayee = await createPayee(payeeInput);
        resolvedPayeeId = newPayee.id;
        setPayees(prev => [...prev, newPayee]);
        setPayeeId(newPayee.id);
      } catch {
        // ignore
      }
    }

    const effectiveCategoryId = subCategoryId ?? categoryId;

    try {
      await onSave({
        date,
        payeeId: resolvedPayeeId,
        categoryId: effectiveCategoryId,
        memo: memo || undefined,
        amount: Number(amount),
        status,
        targetAccountId,
      });
    } finally {
      setSubmitting(false);
    }
  };

  const handleCategoryChange = async (val: string) => {
    const num = val ? Number(val) : undefined;
    setCategoryId(num);
    setSubCategoryId(undefined);
  };

  return (
    <form onSubmit={handleSubmit} className={styles.form}>
      <div className={styles.row}>
        <div className={styles.field}>
          <label className={styles.label}>Date</label>
          <input
            type="date"
            className={styles.input}
            value={date}
            onChange={e => setDate(e.target.value)}
            tabIndex={1}
            autoFocus={!initial?.id}
          />
          {errors.date && <span className={styles.error}>{errors.date}</span>}
        </div>

        <div className={styles.fieldRelative}>
          <label className={styles.label}>Payee</label>
          <input
            ref={payeeRef}
            type="text"
            className={styles.input}
            value={payeeInput}
            onChange={e => handlePayeeInput(e.target.value)}
            onBlur={() => setTimeout(() => setShowSuggestions(false), 150)}
            tabIndex={2}
            autoComplete="off"
          />
          {showSuggestions && payeeSuggestions.length > 0 && (
            <ul className={styles.suggestions}>
              {payeeSuggestions.map(p => (
                <li key={p.id} onMouseDown={() => selectPayee(p)} className={styles.suggestion}>
                  {p.name}
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Category</label>
          <select
            className={styles.select}
            value={categoryId ?? ''}
            onChange={e => handleCategoryChange(e.target.value)}
            tabIndex={3}
          >
            <option value="">— None —</option>
            {topCategories.map(cat => (
              <option key={cat.id} value={cat.id}>{cat.name}</option>
            ))}
          </select>
        </div>

        {subCategories.length > 0 && (
          <div className={styles.field}>
            <label className={styles.label}>Sub-Category</label>
            <select
              className={styles.select}
              value={subCategoryId ?? ''}
              onChange={e => setSubCategoryId(e.target.value ? Number(e.target.value) : undefined)}
              tabIndex={4}
            >
              <option value="">— None —</option>
              {subCategories.map(sub => (
                <option key={sub.id} value={sub.id}>{sub.name}</option>
              ))}
            </select>
          </div>
        )}

        <div className={styles.field}>
          <label className={styles.label}>Memo</label>
          <input
            type="text"
            className={styles.input}
            value={memo}
            onChange={e => setMemo(e.target.value)}
            tabIndex={5}
          />
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Amount</label>
          <input
            type="number"
            step="0.01"
            className={styles.input}
            value={amount}
            onChange={e => setAmount(e.target.value)}
            tabIndex={6}
          />
          {errors.amount && <span className={styles.error}>{errors.amount}</span>}
        </div>
      </div>

      {initial?.id && accounts && accounts.filter(a => a.id !== _accountId).length > 0 && (
        <div className={styles.moveRow}>
          <label className={styles.label}>Move to Account</label>
          <select
            className={styles.select}
            value={targetAccountId ?? ''}
            onChange={e => setTargetAccountId(e.target.value ? Number(e.target.value) : undefined)}
          >
            <option value="">— Keep in current account —</option>
            {accounts.filter(a => a.id !== _accountId).map(a => (
              <option key={a.id} value={a.id}>{a.name}</option>
            ))}
          </select>
        </div>
      )}

      <div className={styles.actions}>
        <button type="submit" className={styles.btnSave} disabled={submitting} tabIndex={7}>
          {submitting ? 'Saving…' : 'Save'}
        </button>
        <button type="button" className={styles.btnCancel} onClick={onCancel} tabIndex={8}>
          Cancel
        </button>
      </div>
    </form>
  );
}
