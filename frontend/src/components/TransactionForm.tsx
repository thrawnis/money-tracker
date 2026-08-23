import { useState, useEffect, useRef, type FormEvent, type RefObject, type KeyboardEvent } from 'react';
import type { Transaction, Category, Payee, Account } from '../types';
import { getCategories, createCategory } from '../api/categories';
import { getPayees, createPayee } from '../api/payees';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import styles from './TransactionForm.module.css';

export interface SplitInput {
  categoryId?: number;
  amount: number;
  memo?: string;
}

interface Props {
  accountId: number;
  accounts?: Account[];
  initial?: Partial<Transaction>;
  initialPayeeName?: string;
  initialCategoryLabel?: string;
  onSave: (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits' | 'isVoided'> & { targetAccountId?: number; transferDestAccountId?: number; splits?: SplitInput[] }) => Promise<void>;
  onCancel: () => void;
  onDirtyChange?: (dirty: boolean) => void;
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

const PLAIN_NUMBER_RE = /^-?\d*\.?\d+$/;

type EvalResult = { ok: true; value: number } | { ok: false; position: number; message: string };

// Safe arithmetic evaluator for the Amount field — supports + - * / ( ) and
// unary +/-, hand-rolled rather than eval/Function since it runs on raw
// keystrokes. Reports the character position of the first problem so the UI
// can point at exactly what's wrong with an invalid formula.
function evaluateArithmeticDetailed(expr: string): EvalResult {
  const s = expr.trim();
  if (!s) return { ok: false, position: 0, message: 'Empty formula' };
  const badChar = s.match(/[^\d+\-*/().\s]/);
  if (badChar?.index !== undefined) {
    return { ok: false, position: badChar.index, message: `Unexpected character "${badChar[0]}"` };
  }

  let i = 0;
  const skipSpace = () => { while (s[i] === ' ') i++; };
  const parseNumber = (): number => {
    const start = i;
    while (i < s.length && /[\d.]/.test(s[i])) i++;
    const numStr = s.slice(start, i);
    const val = numStr ? parseFloat(numStr) : NaN;
    if (isNaN(val)) throw { position: start, message: 'Expected a number' };
    return val;
  };
  const parseFactor = (): number => {
    skipSpace();
    if (s[i] === '(') {
      i++;
      const val = parseExpr();
      skipSpace();
      if (s[i] !== ')') throw { position: i, message: 'Expected ")"' };
      i++;
      return val;
    }
    if (s[i] === '-') { i++; return -parseFactor(); }
    if (s[i] === '+') { i++; return parseFactor(); }
    if (i >= s.length) throw { position: i, message: 'Expected a number' };
    return parseNumber();
  };
  const parseTerm = (): number => {
    let val = parseFactor();
    skipSpace();
    while (s[i] === '*' || s[i] === '/') {
      const op = s[i]; i++;
      const rhs = parseFactor();
      val = op === '*' ? val * rhs : val / rhs;
      skipSpace();
    }
    return val;
  };
  const parseExpr = (): number => {
    let val = parseTerm();
    skipSpace();
    while (s[i] === '+' || s[i] === '-') {
      const op = s[i]; i++;
      const rhs = parseTerm();
      val = op === '+' ? val + rhs : val - rhs;
      skipSpace();
    }
    return val;
  };

  try {
    const result = parseExpr();
    skipSpace();
    if (i !== s.length) return { ok: false, position: i, message: `Unexpected character "${s[i]}"` };
    if (!isFinite(result)) return { ok: false, position: 0, message: 'Result is not a finite number' };
    return { ok: true, value: result };
  } catch (err) {
    const e = err as { position?: number; message?: string };
    return { ok: false, position: e.position ?? i, message: e.message ?? 'Invalid formula' };
  }
}

function evaluateArithmetic(expr: string): number | null {
  const result = evaluateArithmeticDetailed(expr);
  return result.ok ? result.value : null;
}

// Resolves the Amount field's raw text to a number, evaluating it as an
// arithmetic expression when it isn't already a plain number.
function resolveAmountValue(raw: string): number | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  if (PLAIN_NUMBER_RE.test(trimmed)) return parseFloat(trimmed);
  return evaluateArithmetic(trimmed);
}

// Amount input that doubles as a calculator: typing a formula shows a live
// "= result" preview (or a pointer at the first bad character); pressing
// Enter on a formula evaluates it in place — without submitting the form —
// and briefly flashes the field. Enter on an already-plain number is left
// alone so the browser's native "Enter submits the form" behavior fires,
// letting handleSubmit do the final 2-decimal round.
function AmountField({
  value, onChange, className, placeholder, tabIndex, inputRef, autoFocus,
}: {
  value: string;
  onChange: (val: string) => void;
  className?: string;
  placeholder?: string;
  tabIndex?: number;
  inputRef?: RefObject<HTMLInputElement | null>;
  autoFocus?: boolean;
}) {
  const [flash, setFlash] = useState(false);
  const flashTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => { if (flashTimer.current) clearTimeout(flashTimer.current); }, []);

  const trimmed = value.trim();
  const isFormula = trimmed !== '' && !PLAIN_NUMBER_RE.test(trimmed);
  const evalResult = isFormula ? evaluateArithmeticDetailed(trimmed) : null;

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || !isFormula) return;
    e.preventDefault();
    if (evalResult?.ok) {
      onChange(String(evalResult.value));
      setFlash(true);
      if (flashTimer.current) clearTimeout(flashTimer.current);
      flashTimer.current = setTimeout(() => setFlash(false), 700);
    }
  };

  return (
    <div className={styles.amountFieldWrap}>
      <input
        ref={inputRef}
        type="text"
        inputMode="decimal"
        className={`${className ?? styles.input}${flash ? ` ${styles.amountFlash}` : ''}`}
        value={value}
        onChange={e => onChange(e.target.value)}
        onKeyDown={handleKeyDown}
        tabIndex={tabIndex}
        placeholder={placeholder}
        autoFocus={autoFocus}
      />
      {evalResult && (
        evalResult.ok
          ? <div className={styles.amountPreview}>= {evalResult.value.toFixed(2)}</div>
          : <div className={styles.amountFormulaError}>{evalResult.message}</div>
      )}
    </div>
  );
}

function CategoryAutocomplete({
  value, onChange, categories, className, placeholder, tabIndex, autoFocus, inputRef,
}: {
  value: string;
  onChange: (val: string) => void;
  categories: Category[];
  className?: string;
  placeholder?: string;
  tabIndex?: number;
  autoFocus?: boolean;
  inputRef?: RefObject<HTMLInputElement | null>;
}) {
  const [suggestions, setSuggestions] = useState<{ id: number; label: string }[]>([]);
  const [show, setShow] = useState(false);
  const [highlight, setHighlight] = useState(-1);

  const updateSuggestions = (val: string) => {
    if (val.length >= 1) {
      const flat = flattenCategories(categories);
      const filtered = flat.filter(c => c.label.toLowerCase().includes(val.toLowerCase()));
      setSuggestions(filtered.slice(0, 10));
      setShow(true);
      setHighlight(-1);
    } else {
      setShow(false);
      setHighlight(-1);
    }
  };

  const handleChange = (val: string) => {
    onChange(val);
    updateSuggestions(val);
  };

  const select = (entry: { id: number; label: string }) => {
    onChange(entry.label);
    setShow(false);
    setHighlight(-1);
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (!show || suggestions.length === 0) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setHighlight(h => (h + 1) % suggestions.length);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlight(h => (h - 1 + suggestions.length) % suggestions.length);
    } else if (e.key === 'Enter') {
      if (highlight >= 0) {
        e.preventDefault();
        select(suggestions[highlight]);
      }
    } else if (e.key === 'Escape') {
      setShow(false);
      setHighlight(-1);
    }
  };

  return (
    <div className={styles.fieldRelative}>
      <input
        ref={inputRef}
        type="text"
        className={className ?? styles.input}
        value={value}
        onChange={e => handleChange(e.target.value)}
        onKeyDown={handleKeyDown}
        onFocus={() => updateSuggestions(value)}
        onBlur={() => setTimeout(() => setShow(false), 150)}
        tabIndex={tabIndex}
        autoComplete="off"
        placeholder={placeholder}
        autoFocus={autoFocus}
      />
      {show && suggestions.length > 0 && (
        <ul className={styles.suggestions}>
          {suggestions.map((c, i) => (
            <li
              key={c.id}
              onMouseDown={() => select(c)}
              className={i === highlight ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}
            >
              {c.label}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

let splitRowSeq = 0;
interface SplitRow {
  key: number;
  categoryInput: string;
  amount: string;
  memo: string;
}

export default function TransactionForm({ accountId: _accountId, accounts, initial, initialPayeeName, initialCategoryLabel, onSave, onCancel, onDirtyChange }: Props) {
  const today = new Date().toISOString().slice(0, 10);
  const [date, setDate] = useState(initial?.date ?? today);
  const [payeeInput, setPayeeInput] = useState(initialPayeeName ?? initial?.payee?.name ?? '');
  const [payeeId, setPayeeId] = useState<number | undefined>(initialPayeeName ? undefined : initial?.payeeId);
  const [memo, setMemo] = useState(initial?.memo ?? '');
  const [amount, setAmount] = useState(initial?.amount?.toString() ?? '');
  // Expense/Income toggle: lets the user type plain positive numbers and
  // have the correct sign applied automatically. Defaults to Expense for a
  // brand-new transaction; an existing amount's sign decides which mode
  // it opens in so re-editing doesn't silently flip it.
  const [amountMode, setAmountMode] = useState<'expense' | 'income'>(
    initial?.amount != null && initial.amount > 0 ? 'income' : 'expense'
  );
  const [status] = useState<Transaction['status']>(initial?.status ?? 'Uncleared');
  const [postDate, setPostDate] = useState(initial?.postDate ?? '');
  const [showPostDate, setShowPostDate] = useState(!!initial?.postDate);

  const [categories, setCategories] = useState<Category[]>([]);
  const [payees, setPayees] = useState<Payee[]>([]);

  // Category autocomplete
  const [categoryInput, setCategoryInput] = useState('');
  const categoryRef = useRef<HTMLInputElement>(null);

  // Split across multiple categories — mutually exclusive with the single
  // category field above and disallowed on transfers.
  const [isSplit, setIsSplit] = useState(!!(initial?.splits && initial.splits.length > 0));
  const [splitRows, setSplitRows] = useState<SplitRow[]>([]);
  const [splitError, setSplitError] = useState('');

  // Payee autocomplete
  const [payeeSuggestions, setPayeeSuggestions] = useState<Payee[]>([]);
  const [showPayeeSuggestions, setShowPayeeSuggestions] = useState(false);
  const [payeeHighlight, setPayeeHighlight] = useState(-1);
  const payeeRef = useRef<HTMLInputElement>(null);

  const [targetAccountId, setTargetAccountId] = useState<number | undefined>(undefined);
  const isExistingTransfer = !!(initial?.transferTransactionId);
  const [isTransfer, setIsTransfer] = useState(isExistingTransfer);
  const [transferDestAccountId, setTransferDestAccountId] = useState<number | undefined>(
    isExistingTransfer ? (initial?.transferAccountId ?? undefined) : undefined
  );
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [saveError, setSaveError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  // Snapshot of field values once the initial category/payee resolution settles,
  // so unsaved-changes tracking reflects actual edits rather than firing the
  // moment the form opens (categoryInput starts empty and is filled in below).
  const [formBaseline, setFormBaseline] = useState<string | null>(null);
  const splitSnapshot = () => splitRows.map(r => ({ categoryInput: r.categoryInput, amount: r.amount, memo: r.memo }));
  const isDirty = formBaseline !== null && JSON.stringify({
    date, payeeInput, payeeId, memo, amount, postDate, amountMode,
    categoryInput, isTransfer, transferDestAccountId, targetAccountId,
    isSplit, splits: splitSnapshot(),
  }) !== formBaseline;
  useUnsavedChanges(isDirty);

  useEffect(() => {
    onDirtyChange?.(isDirty);
    return () => onDirtyChange?.(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isDirty]);

  useEffect(() => {
    Promise.all([getCategories(), getPayees()]).then(([cats, pays]) => {
      setCategories(cats);
      setPayees(pays);
      let resolvedCategoryInput = '';
      if (initialCategoryLabel) {
        resolvedCategoryInput = initialCategoryLabel;
      } else if (initial?.categoryId) {
        const flat = flattenCategories(cats);
        const found = flat.find(c => c.id === initial.categoryId);
        if (found) resolvedCategoryInput = found.label;
      }
      if (resolvedCategoryInput) setCategoryInput(resolvedCategoryInput);

      const flat = flattenCategories(cats);
      const resolvedSplitRows: SplitRow[] = (initial?.splits ?? []).map(s => ({
        key: splitRowSeq++,
        categoryInput: s.categoryId ? (flat.find(c => c.id === s.categoryId)?.label ?? '') : '',
        amount: s.amount.toString(),
        memo: s.memo ?? '',
      }));
      if (resolvedSplitRows.length > 0) setSplitRows(resolvedSplitRows);

      setFormBaseline(JSON.stringify({
        date, payeeInput, payeeId, memo, amount, postDate, amountMode,
        categoryInput: resolvedCategoryInput, isTransfer, transferDestAccountId, targetAccountId,
        isSplit, splits: resolvedSplitRows.map(r => ({ categoryInput: r.categoryInput, amount: r.amount, memo: r.memo })),
      }));
    }).catch(console.error);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handlePayeeInput = (val: string) => {
    setPayeeInput(val);
    setPayeeId(undefined);
    setPayeeHighlight(-1);
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
    setPayeeHighlight(-1);
  };

  const handlePayeeKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (!showPayeeSuggestions || payeeSuggestions.length === 0) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setPayeeHighlight(h => (h + 1) % payeeSuggestions.length);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setPayeeHighlight(h => (h - 1 + payeeSuggestions.length) % payeeSuggestions.length);
    } else if (e.key === 'Enter') {
      if (payeeHighlight >= 0) {
        e.preventDefault();
        selectPayee(payeeSuggestions[payeeHighlight]);
      }
    } else if (e.key === 'Escape') {
      setShowPayeeSuggestions(false);
      setPayeeHighlight(-1);
    }
  };

  // ── Splits ─────────────────────────────────────────────────────────────
  const splitTotal = splitRows.reduce((sum, r) => sum + (resolveAmountValue(r.amount) ?? 0), 0);
  const splitRemaining = (resolveAmountValue(amount) ?? 0) - splitTotal;

  const applyAmountSign = (mode: 'expense' | 'income', val: number) =>
    mode === 'expense' ? -Math.abs(val) : Math.abs(val);

  // Re-signs the already-typed amount(s) immediately so switching modes gives
  // instant feedback rather than waiting for submit to flip the sign.
  const handleModeChange = (nextMode: 'expense' | 'income') => {
    setAmountMode(nextMode);
    const resolved = resolveAmountValue(amount);
    if (resolved !== null) setAmount(String(applyAmountSign(nextMode, resolved)));
    setSplitRows(prev => prev.map(r => {
      const rVal = resolveAmountValue(r.amount);
      return rVal !== null ? { ...r, amount: String(applyAmountSign(nextMode, rVal)) } : r;
    }));
  };

  const toggleSplit = () => {
    if (isSplit) {
      setIsSplit(false);
      setSplitRows([]);
      setSplitError('');
    } else {
      setIsSplit(true);
      setSplitRows([
        { key: splitRowSeq++, categoryInput, amount, memo: '' },
        { key: splitRowSeq++, categoryInput: '', amount: '', memo: '' },
      ]);
    }
  };

  const addSplitRow = () => setSplitRows(prev => [...prev, { key: splitRowSeq++, categoryInput: '', amount: '', memo: '' }]);
  const removeSplitRow = (key: number) => setSplitRows(prev => prev.filter(r => r.key !== key));
  const updateSplitRow = (key: number, patch: Partial<SplitRow>) =>
    setSplitRows(prev => prev.map(r => r.key === key ? { ...r, ...patch } : r));

  const validate = () => {
    const e: Record<string, string> = {};
    if (!date) e.date = 'Date required';
    if (resolveAmountValue(amount) === null) e.amount = 'Valid amount or formula required';
    if (isTransfer && !transferDestAccountId) e.transferDest = 'Destination account required';
    if (isTransfer && transferDestAccountId === _accountId) e.transferDest = 'Source and destination must differ';
    if (isSplit) {
      if (splitRows.length < 2) {
        e.splits = 'Add at least two splits, or turn off splitting.';
      } else if (splitRows.some(r => resolveAmountValue(r.amount) === null)) {
        e.splits = 'Every split needs a valid amount.';
      } else if (Math.abs(splitRemaining) > 0.004) {
        e.splits = `Splits must add up to the total (${splitRemaining > 0 ? 'short' : 'over'} by ${Math.abs(splitRemaining).toFixed(2)}).`;
      }
    }
    setErrors(e);
    setSplitError(e.splits ?? '');
    return Object.keys(e).length === 0;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setSubmitting(true);
    setSaveError('');

    const roundTo2 = (n: number) => Math.round(n * 100) / 100;
    let finalAmount = roundTo2(resolveAmountValue(amount)!);
    if (!isTransfer) finalAmount = applyAmountSign(amountMode, finalAmount);
    setAmount(finalAmount.toFixed(2));

    try {
      let resolvedPayeeId = payeeId;
      if (payeeInput.trim() && !resolvedPayeeId) {
        try {
          const newPayee = await createPayee(payeeInput.trim());
          resolvedPayeeId = newPayee.id;
          setPayees(prev => [...prev, newPayee]);
          setPayeeId(newPayee.id);
        } catch (err) {
          const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
          setSaveError(msg ?? 'Failed to create payee.');
          setSubmitting(false);
          return;
        }
      }

      let resolvedCategoryId: number | undefined;
      let resolvedSplits: { categoryId?: number; amount: number; memo?: string }[] | undefined;

      if (isTransfer) {
        // no category on transfers
      } else if (isSplit) {
        resolvedSplits = [];
        const roundedSplitRows = splitRows.map(row => ({
          ...row,
          amount: applyAmountSign(amountMode, roundTo2(resolveAmountValue(row.amount)!)).toFixed(2),
        }));
        setSplitRows(roundedSplitRows);
        for (const row of roundedSplitRows) {
          const catId = await resolveCategory(row.categoryInput, categories, setCategories);
          resolvedSplits.push({ categoryId: catId, amount: Number(row.amount), memo: row.memo || undefined });
        }
      } else {
        resolvedCategoryId = await resolveCategory(categoryInput, categories, setCategories);
      }

      await onSave({
        date,
        postDate: showPostDate && postDate ? postDate : undefined,
        payeeId: isTransfer ? undefined : resolvedPayeeId,
        categoryId: isTransfer ? undefined : resolvedCategoryId,
        memo: memo || undefined,
        amount: finalAmount,
        status,
        targetAccountId,
        transferDestAccountId: isTransfer ? transferDestAccountId : undefined,
        splits: resolvedSplits,
      });
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSaveError(msg ?? 'Failed to save transaction.');
    } finally {
      setSubmitting(false);
    }
  };

  // For transfer mode: active accounts only for new transfers, all accounts for editing
  const transferAccounts = accounts
    ? (isExistingTransfer ? accounts : accounts.filter(a => a.isActive))
        .filter(a => a.id !== _accountId)
        .sort((a, b) => a.name.localeCompare(b.name))
    : [];

  return (
    <form onSubmit={handleSubmit} className={styles.form}>
      {/* Transfer toggle — only show for new transactions or existing transfers */}
      {(!initial?.id || isExistingTransfer) && (
        <div className={styles.transferToggleRow}>
          <label className={styles.transferToggleLabel}>
            <input
              type="checkbox"
              checked={isTransfer}
              onChange={e => {
                setIsTransfer(e.target.checked);
                if (!e.target.checked) setTransferDestAccountId(undefined);
                if (e.target.checked) { setIsSplit(false); setSplitRows([]); setSplitError(''); }
              }}
              disabled={isExistingTransfer}
            />
            {' '}Transfer between accounts
          </label>
        </div>
      )}
      <div className={styles.row}>
        <div className={styles.field}>
          <label className={styles.label}>
            Date
            <button
              type="button"
              className={styles.postDateToggle}
              onClick={() => setShowPostDate(s => !s)}
              title="Post Date: the date this transaction settled or posted to your bank/credit card statement, which is often 1–3 days after the transaction date."
            >
              {showPostDate ? '📅−' : '📅+'}
            </button>
          </label>
          <input
            type="date"
            className={styles.input}
            value={date}
            onChange={e => setDate(e.target.value)}
            tabIndex={1}
            autoFocus={!initial?.id}
          />
          {errors.date && <span className={styles.error}>{errors.date}</span>}
          {showPostDate && (
            <div className={styles.postDateRow}>
              <label className={styles.postDateLabel}>
                Post Date
                <span className={styles.postDateHint}>Date posted to statement</span>
              </label>
              <input
                type="date"
                className={styles.input}
                value={postDate}
                onChange={e => setPostDate(e.target.value)}
                tabIndex={2}
              />
            </div>
          )}
        </div>

        {isTransfer ? (
          <div className={styles.field}>
            <label className={styles.label}>Destination Account</label>
            <select
              className={styles.select}
              value={transferDestAccountId ?? ''}
              onChange={e => setTransferDestAccountId(e.target.value ? Number(e.target.value) : undefined)}
              disabled={isExistingTransfer}
              tabIndex={2}
            >
              <option value="">— Select account —</option>
              {transferAccounts.map(a => (
                <option key={a.id} value={a.id} style={!a.isActive ? { color: '#999' } : undefined}>
                  {a.name}{!a.isActive ? ' (inactive)' : ''}
                </option>
              ))}
            </select>
            {errors.transferDest && <span className={styles.error}>{errors.transferDest}</span>}
          </div>
        ) : (
          <>
            <div className={styles.fieldRelative}>
              <label className={styles.label}>Payee</label>
              <input
                ref={payeeRef}
                type="text"
                className={styles.input}
                value={payeeInput}
                onChange={e => handlePayeeInput(e.target.value)}
                onKeyDown={handlePayeeKeyDown}
                onFocus={() => {
                  if (payeeInput.length < 1) return;
                  const filtered = payees.filter(p => p.name.toLowerCase().includes(payeeInput.toLowerCase()));
                  setPayeeSuggestions(filtered.slice(0, 8));
                  setShowPayeeSuggestions(true);
                }}
                onBlur={() => setTimeout(() => setShowPayeeSuggestions(false), 150)}
                tabIndex={2}
                autoComplete="off"
              />
              {showPayeeSuggestions && payeeSuggestions.length > 0 && (
                <ul className={styles.suggestions}>
                  {payeeSuggestions.map((p, i) => (
                    <li
                      key={p.id}
                      onMouseDown={() => selectPayee(p)}
                      className={i === payeeHighlight ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}
                    >
                      {p.name}
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <div className={styles.fieldRelative}>
              <label className={styles.label}>
                Category
                <button
                  type="button"
                  className={styles.postDateToggle}
                  onClick={toggleSplit}
                  title={isSplit ? 'Use a single category' : 'Split across multiple categories'}
                >
                  {isSplit ? '⛙−' : '⛙+'}
                </button>
              </label>
              {isSplit ? (
                <div className={styles.input} style={{ color: '#667', fontStyle: 'italic' }}>
                  Split across {splitRows.length} categories
                </div>
              ) : (
                <CategoryAutocomplete
                  inputRef={categoryRef}
                  value={categoryInput}
                  onChange={setCategoryInput}
                  categories={categories}
                  tabIndex={3}
                  placeholder="e.g. Food or Food: Groceries"
                />
              )}
            </div>
          </>
        )}

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
          {!isTransfer && (
            <div className={styles.amountModeToggle}>
              <button
                type="button"
                className={amountMode === 'expense' ? `${styles.amountModeBtn} ${styles.amountModeExpense}` : styles.amountModeBtn}
                onClick={() => handleModeChange('expense')}
              >
                Expense
              </button>
              <button
                type="button"
                className={amountMode === 'income' ? `${styles.amountModeBtn} ${styles.amountModeIncome}` : styles.amountModeBtn}
                onClick={() => handleModeChange('income')}
              >
                Income
              </button>
            </div>
          )}
          <AmountField
            value={amount}
            onChange={setAmount}
            tabIndex={5}
            placeholder="e.g. 42.50 or (33.40*17)/14"
          />
          {errors.amount && <span className={styles.error}>{errors.amount}</span>}
        </div>
      </div>

      {isSplit && !isTransfer && (
        <div className={styles.splitsSection}>
          <div className={styles.splitsHeader}>
            <span className={styles.label}>Splits</span>
            <span className={splitRemaining === 0 ? styles.splitsRemainingOk : styles.splitsRemainingBad}>
              Remaining: {splitRemaining.toFixed(2)}
            </span>
          </div>
          {splitRows.map(row => (
            <div key={row.key} className={styles.splitRow}>
              <CategoryAutocomplete
                value={row.categoryInput}
                onChange={val => updateSplitRow(row.key, { categoryInput: val })}
                categories={categories}
                placeholder="Category"
              />
              <input
                type="text"
                className={styles.input}
                placeholder="Memo"
                value={row.memo}
                onChange={e => updateSplitRow(row.key, { memo: e.target.value })}
              />
              <AmountField
                value={row.amount}
                onChange={val => updateSplitRow(row.key, { amount: val })}
                placeholder="Amount"
              />
              <button
                type="button"
                className={styles.splitRemoveBtn}
                onClick={() => removeSplitRow(row.key)}
                disabled={splitRows.length <= 2}
                title="Remove split"
              >✕</button>
            </div>
          ))}
          <button type="button" className={styles.btnSecondaryLink} onClick={addSplitRow}>
            + Add Split
          </button>
          {splitError && <div className={styles.error}>{splitError}</div>}
        </div>
      )}

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

      {saveError && <div className={styles.error}>{saveError}</div>}
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
