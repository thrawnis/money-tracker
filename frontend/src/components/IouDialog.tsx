import { useState } from 'react';
import { createIous, updateIou, type IouEntry, type IouLinkedTransaction, type IouPerson } from '../api/ious';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import PersonInput, { type PersonValue } from './PersonInput';
import styles from './AccountEditModal.module.css';

type ApiError = { response?: { data?: { message?: string } } };

interface Props {
  people: IouPerson[];
  /** Edit this IOU instead of creating new ones. */
  entry?: IouEntry;
  /** Tag this purchase: prefills date, description and the split total. */
  transaction?: IouLinkedTransaction;
  onSaved: () => void;
  onClose: () => void;
}

interface Line { key: number; person: PersonValue; amount: string }
let lineSeq = 0;
const newLine = (person: PersonValue = {}, amount = ''): Line => ({ key: ++lineSeq, person, amount });

const today = () => new Date().toLocaleDateString('en-CA');
const toCents = (s: string) => Math.round(parseFloat(s) * 100);

/**
 * New IOUs (one or several people, e.g. splitting a bill) or editing one.
 * Amounts are entered as positive numbers; "They owe me" / "I owe them" sets
 * the sign for every line.
 */
export default function IouDialog({ people, entry, transaction, onSaved, onClose }: Props) {
  const editing = entry != null;
  const [date, setDate] = useState(entry?.date ?? transaction?.date ?? today());
  const [description, setDescription] = useState(entry?.description ?? transaction?.payee ?? '');
  const [notes, setNotes] = useState(entry?.notes ?? '');
  const [theyOwe, setTheyOwe] = useState(entry ? entry.amount > 0 : (transaction ? transaction.amount < 0 : true));
  const [lines, setLines] = useState<Line[]>(() =>
    entry ? [newLine({ personId: entry.personId }, Math.abs(entry.amount).toFixed(2))] : [newLine()]);
  // Split helper
  const [total, setTotal] = useState(transaction ? Math.abs(transaction.amount).toFixed(2) : '');
  const [includeMe, setIncludeMe] = useState(true);
  // Settled by hand (edit only)
  const [paidOff, setPaidOff] = useState(!!entry?.paidOffManually);
  const [paidOffDate, setPaidOffDate] = useState(entry?.paidOffManually ? entry.paidOffDate ?? today() : today());
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  useUnsavedChanges(dirty && !saving);

  const touch = <T,>(set: (v: T) => void) => (v: T) => { set(v); setDirty(true); };

  const setLine = (key: number, patch: Partial<Line>) => {
    setLines(prev => prev.map(l => (l.key === key ? { ...l, ...patch } : l)));
    setDirty(true);
  };

  // Split evenly: the total divided among the people (plus you, if your share
  // is included), cents spread so the shares add up exactly.
  const splitEvenly = () => {
    const cents = toCents(total);
    if (!cents || cents <= 0) { setError('Enter the total to split.'); return; }
    const shares = lines.length + (includeMe ? 1 : 0);
    const base = Math.floor(cents / shares);
    let extra = cents - base * shares;
    setLines(prev => prev.map(l => {
      const c = base + (extra-- > 0 ? 1 : 0);
      return { ...l, amount: (c / 100).toFixed(2) };
    }));
    setError('');
    setDirty(true);
  };

  const close = () => {
    if (dirty && !confirm('Discard these changes?')) return;
    onClose();
  };

  const save = async () => {
    setError('');
    const parsed = lines.map(l => ({ ...l, cents: toCents(l.amount) }));
    if (parsed.some(l => l.person.personId == null && !l.person.newName)) { setError('Choose a person on every line.'); return; }
    if (parsed.some(l => !l.cents || l.cents <= 0)) { setError('Enter an amount greater than zero on every line.'); return; }
    const sign = theyOwe ? 1 : -1;
    setSaving(true);
    try {
      if (editing) {
        if (lines[0].person.personId == null) { setError('Pick an existing person.'); setSaving(false); return; }
        await updateIou(entry.id, {
          personId: lines[0].person.personId, date, amount: sign * parsed[0].cents / 100,
          description, notes, transactionId: entry.transaction?.id ?? null,
          paidOffDate: paidOff ? paidOffDate : null, paidOffManually: paidOff,
        });
      } else {
        await createIous({
          date, description, notes, transactionId: transaction?.id,
          lines: parsed.map(l => ({ personId: l.person.personId, newPersonName: l.person.newName, amount: sign * l.cents / 100 })),
        });
      }
      onSaved();
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Failed to save.');
      setSaving(false);
    }
  };

  const sumCents = lines.reduce((s, l) => s + (toCents(l.amount) || 0), 0);

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) close(); }}>
      <div className={styles.modal} style={{ width: 520 }} role="dialog" aria-label={editing ? 'Edit IOU' : 'New IOU'}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>{editing ? 'Edit IOU' : transaction ? 'Tag people on this purchase' : 'New IOU'}</h2>
          <button type="button" className={styles.closeBtn} onClick={close} aria-label="Close">✕</button>
        </div>
        <form className={styles.modalBody} onSubmit={e => { e.preventDefault(); save(); }}>
          {transaction && (
            <p className={styles.label} style={{ fontWeight: 400 }}>
              {transaction.date} · {transaction.payee ?? '—'} · {transaction.accountName} ·{' '}
              <strong>{transaction.amount.toLocaleString('en-US', { style: 'currency', currency: 'USD' })}</strong>
            </p>
          )}

          <div className={styles.field}>
            <span className={styles.label}>Direction</span>
            <div style={{ display: 'flex', gap: 16 }}>
              <label className={styles.checkLabel}>
                <input type="radio" checked={theyOwe} onChange={() => touch(setTheyOwe)(true)} /> They owe me
              </label>
              <label className={styles.checkLabel}>
                <input type="radio" checked={!theyOwe} onChange={() => touch(setTheyOwe)(false)} /> I owe them
              </label>
            </div>
          </div>

          <div style={{ display: 'flex', gap: 12 }}>
            <div className={styles.field} style={{ flex: '0 0 150px' }}>
              <label className={styles.label} htmlFor="iou-date">Date</label>
              <input id="iou-date" type="date" className={styles.input} value={date} onChange={e => touch(setDate)(e.target.value)} required />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="iou-desc">Description</label>
              <input id="iou-desc" className={styles.input} value={description} onChange={e => touch(setDescription)(e.target.value)} placeholder="e.g. Dinner at Nopa" />
            </div>
          </div>

          {!editing && (
            <div className={styles.field} style={{ background: 'var(--c-f5f7fa)', padding: 10, borderRadius: 4 }}>
              <span className={styles.label}>Split helper</span>
              <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
                <input className={styles.input} style={{ width: 110 }} inputMode="decimal" aria-label="Total to split"
                  value={total} onChange={e => setTotal(e.target.value)} placeholder="Total" />
                <label className={styles.checkLabel}>
                  <input type="checkbox" checked={includeMe} onChange={e => setIncludeMe(e.target.checked)} /> Include my share
                </label>
                <button type="button" className={styles.btnSecondary} onClick={splitEvenly}>Split evenly</button>
              </div>
            </div>
          )}

          <div className={styles.field}>
            <span className={styles.label}>{editing ? 'Person and amount' : 'People and amounts'}</span>
            {lines.map((l, i) => (
              <div key={l.key} style={{ display: 'flex', gap: 8, marginBottom: 6, alignItems: 'center' }}>
                <div style={{ flex: 1 }}>
                  <PersonInput
                    people={people}
                    value={l.person}
                    onChange={v => setLine(l.key, { person: v })}
                    autoFocus={i === 0 && !editing}
                    ariaLabel={`Person ${i + 1}`}
                  />
                </div>
                <input className={styles.input} style={{ width: 110 }} inputMode="decimal" aria-label={`Amount ${i + 1}`}
                  value={l.amount} onChange={e => setLine(l.key, { amount: e.target.value })} placeholder="0.00" />
                {!editing && lines.length > 1 && (
                  <button type="button" className={styles.btnLink} aria-label={`Remove person ${i + 1}`}
                    onClick={() => { setLines(prev => prev.filter(x => x.key !== l.key)); setDirty(true); }}>✕</button>
                )}
              </div>
            ))}
            {!editing && (
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <button type="button" className={styles.btnLink} onClick={() => { setLines(prev => [...prev, newLine()]); setDirty(true); }}>
                  + Add person
                </button>
                {lines.length > 1 && (
                  <span className={styles.label} style={{ fontWeight: 400 }}>
                    Total {theyOwe ? 'owed to you' : 'you owe'}: <strong>{(sumCents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' })}</strong>
                  </span>
                )}
              </div>
            )}
            {lines.some(l => l.person.newName) && (
              <span className={styles.label} style={{ fontWeight: 400 }}>New people are added when you save.</span>
            )}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="iou-notes">Notes</label>
            <textarea id="iou-notes" className={styles.textarea} rows={2} value={notes} onChange={e => touch(setNotes)(e.target.value)} />
          </div>

          {editing && (
            <div className={styles.field}>
              <label className={styles.checkLabel}>
                <input type="checkbox" checked={paidOff} onChange={e => touch(setPaidOff)(e.target.checked)} />
                Settled outside the app (or forgiven) on
              </label>
              {paidOff && (
                <input type="date" className={styles.input} style={{ width: 170 }} value={paidOffDate}
                  onChange={e => touch(setPaidOffDate)(e.target.value)} aria-label="Paid off date" />
              )}
              {!paidOff && entry && entry.covered > 0 && (
                <span className={styles.label} style={{ fontWeight: 400 }}>
                  Payments have covered {entry.covered.toLocaleString('en-US', { style: 'currency', currency: 'USD' })} so far.
                </span>
              )}
            </div>
          )}

          {error && <div className={styles.error}>{error}</div>}
          <div className={styles.actions}>
            <button type="submit" className={styles.btnPrimary} disabled={saving}>{saving ? 'Saving…' : 'Save'}</button>
            <button type="button" className={styles.btnSecondary} onClick={close} disabled={saving}>Cancel</button>
          </div>
        </form>
      </div>
    </div>
  );
}
