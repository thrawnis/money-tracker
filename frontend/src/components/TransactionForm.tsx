import { useState, useEffect, useRef, type FormEvent } from 'react';
import type { Transaction, Category, Payee, Account } from '../types';
import { getCategories, createCategory } from '../api/categories';
import { getPayees, createPayee } from '../api/payees';
import styles from './TransactionForm.module.css';

interface Props {
  accountId: number;
  accounts?: Account[];
  initial?: Partial<Transaction>;
  initialPayeeName?: string;
  initialCategoryLabel?: string;
  onSave: (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'> & { targetAccountId?: number }) => Promise<void>;
  onCancel: () => void;
}

// Flatten category tree into searchable entries
function flattenCategories(categories: Category[]): { id: number; label: string }[] {
  const flat: { id: number; label: string }[] = [];
  for (const cat of categories) {
    flat.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        flat.push({ id: sub.id, label: `${cat.name}: ${sub.name}` });
      }
    }
  }
  return flat;
}

// Given a label like "Food: Groceries", resolve or create the category ID
async function resolveCategory(
  input: string,
  categories: Category[],
  setCategories: (fn: (prev: Category[]) => Category[]) => void,
): Promise<number | undefined> {
  const trimmed = input.trim();
  if (!trimmed) return undefined;

  const flat = flattenCategories(categories);
  const match = flat.find(c => c.label.toLowerCase() === trimmed.toLowerCase());
  if (match) return match.id;

  // Parse "Parent: Sub" or plain "Name"
  const colonIdx = trimmed.indexOf(':');
  if (colonIdx > 0) {
    const parentName = trimmed.slice(0, colonIdx).trim();
    const subName = trimmed.slice(colonIdx + 1).trim();

    // Find or create parent
    let parent = categories.find(c => c.name.toLowerCase() === parentName.toLowerCase());
    if (!parent) {
      parent = await createCategory({ name: parentName, parentId: undefined });
      setCategories(prev => [...prev, { ...parent!, subCategories: [] }]);
    }

    // Find or create sub under parent
    const existingSub = parent.subCategories?.find(s => s.name.toLowerCase() === subName.toLowerCase());
    if (existingSub) return existingSub.id;

    const newSub = await createCategory({ name: subName, parentId: parent.id });
    setCategories(prev => prev.map(c =>
      c.id === parent!.id
        ? { ...c, subCategories: [...(c.subCategories ?? []), newSub] }
        : c
    ));
    return newSub.id;
  } else {
    // Plain category
    const newCat = await createCategory({ name: trimmed, parentId: undefined });
    setCategories(prev => [...prev, { ...newCat, subCategories: [] }]);
    return newCat.id;
  }
}

export default function TransactionForm({ accountId: _accountId, accounts, initial, initialPayeeName, initialCategoryLabel, onSave, onCancel }: Props) {
  const today = new Date().toISOString().slice(0, 10);
  const [date, setDate] = useState(initial?.date ?? today);
  const [payeeInput, setPayeeInput] = useState(initialPayeeName ?? initial?.payee?.name ?? '');
  const [payeeId, setPayeeId] = useState<number | undefined>(initialPayeeName ? undefined : initial?.payeeId);
  const [memo, setMemo] = useState(initial?.memo ?? '');
  const [amount, setAmount] = useState(initial?.amount?.toString() ?? '');
  const [status] = useState<Transaction['status']>(initial?.status ?? 'Uncleared');

  const [categories, setCategories] = useState<Category[]>([]);
  const [payees, setPayees] = useState<Payee[]>([]);

  // Category autocomplete
  const [categoryInput, setCategoryInput] = useState('');
  const [categorySuggestions, setCategorySuggestions] = useState<{ id: number; label: string }[]>([]);
  const [showCatSuggestions, setShowCatSuggestions] = useState(false);
  const categoryRef = useRef<HTMLInputElement>(null);

  // Payee autocomplete
  const [payeeSuggestions, setPayeeSuggestions] = useState<Payee[]>([]);
  const [showPayeeSuggestions, setShowPayeeSuggestions] = useState(false);
  const payeeRef = useRef<HTMLInputElement>(null);

  const [targetAccountId, setTargetAccountId] = useState<number | undefined>(undefined);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    Promise.all([getCategories(), getPayees()]).then(([cats, pays]) => {
      setCategories(cats);
      setPayees(pays);
      if (initialCategoryLabel) {
        setCategoryInput(initialCategoryLabel);
      } else if (initial?.categoryId) {
        const flat = flattenCategories(cats);
        const found = flat.find(c => c.id === initial.categoryId);
        if (found) setCategoryInput(found.label);
      }
    }).catch(console.error);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleCategoryInput = (val: string) => {
    setCategoryInput(val);
    if (val.length >= 1) {
      const flat = flattenCategories(categories);
      const filtered = flat.filter(c => c.label.toLowerCase().includes(val.toLowerCase()));
      setCategorySuggestions(filtered.slice(0, 10));
      setShowCatSuggestions(true);
    } else {
      setShowCatSuggestions(false);
    }
  };

  const selectCategory = (entry: { id: number; label: string }) => {
    setCategoryInput(entry.label);
    setShowCatSuggestions(false);
  };

  const handlePayeeInput = (val: string) => {
    setPayeeInput(val);
    setPayeeId(undefined);
    if (val.length >= 1) {
      const filtered = payees.filter(p => p.name.toLowerCase().includes(val.toLowerCase()));
      setPayeeSuggestions(filtered.slice(0, 8));
      setShowPayeeSuggestions(true);
    } else {
      setShowPayeeSuggestions(false);
    }
  };

  const selectPayee = (p: Payee) => {
    setPayeeInput(p.name);
    setPayeeId(p.id);
    if (p.defaultCategoryId) {
      const flat = flattenCategories(categories);
      const found = flat.find(c => c.id === p.defaultCategoryId);
      if (found) setCategoryInput(found.label);
    }
    setShowPayeeSuggestions(false);
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

    try {
      let resolvedPayeeId = payeeId;
      if (payeeInput.trim() && !resolvedPayeeId) {
        const newPayee = await createPayee(payeeInput.trim());
        resolvedPayeeId = newPayee.id;
        setPayees(prev => [...prev, newPayee]);
        setPayeeId(newPayee.id);
      }

      const resolvedCategoryId = await resolveCategory(categoryInput, categories, setCategories);

      await onSave({
        date,
        payeeId: resolvedPayeeId,
        categoryId: resolvedCategoryId,
        memo: memo || undefined,
        amount: Number(amount),
        status,
        targetAccountId,
      });
    } finally {
      setSubmitting(false);
    }
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
            onBlur={() => setTimeout(() => setShowPayeeSuggestions(false), 150)}
            tabIndex={2}
            autoComplete="off"
          />
          {showPayeeSuggestions && payeeSuggestions.length > 0 && (
            <ul className={styles.suggestions}>
              {payeeSuggestions.map(p => (
                <li key={p.id} onMouseDown={() => selectPayee(p)} className={styles.suggestion}>
                  {p.name}
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className={styles.fieldRelative}>
          <label className={styles.label}>Category</label>
          <input
            ref={categoryRef}
            type="text"
            className={styles.input}
            value={categoryInput}
            onChange={e => handleCategoryInput(e.target.value)}
            onBlur={() => setTimeout(() => setShowCatSuggestions(false), 150)}
            tabIndex={3}
            autoComplete="off"
            placeholder="e.g. Food or Food: Groceries"
          />
          {showCatSuggestions && categorySuggestions.length > 0 && (
            <ul className={styles.suggestions}>
              {categorySuggestions.map(c => (
                <li key={c.id} onMouseDown={() => selectCategory(c)} className={styles.suggestion}>
                  {c.label}
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Memo</label>
          <input
            type="text"
            className={styles.input}
            value={memo}
            onChange={e => setMemo(e.target.value)}
            tabIndex={4}
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
            tabIndex={5}
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
        <button type="submit" className={styles.btnSave} disabled={submitting} tabIndex={6}>
          {submitting ? 'Saving…' : 'Save'}
        </button>
        <button type="button" className={styles.btnCancel} onClick={onCancel} tabIndex={7}>
          Cancel
        </button>
      </div>
    </form>
  );
}
