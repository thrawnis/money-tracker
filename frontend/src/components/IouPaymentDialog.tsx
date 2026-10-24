import { useState } from 'react';
import {
  createIouPayment, updateIouPayment, IOU_METHODS, methodLabel,
  type IouDirection, type IouEntry, type IouLinkedTransaction, type IouMethod, type IouPayment, type IouPerson,
} from '../api/ious';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import styles from './AccountEditModal.module.css';

type ApiError = { response?: { data?: { message?: string } } };

interface Props {
  people: IouPerson[];
  /** Open IOUs, offered as "apply to" targets for the chosen person. */
  openEntries: IouEntry[];
  /** Edit this payment instead of recording a new one. */
  payment?: IouPayment;
  /** Prefills for a new payment. */
  personId?: number;
  appliesToEntryId?: number;
  /** The bank transaction the money arrived/left in (e.g. from the register). */
  transaction?: IouLinkedTransaction;
  onSaved: () => void;
  onClose: () => void;
}

const today = () => new Date().toLocaleDateString('en-CA');
const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });

/**
 * Records money received from, or paid to, a person. It pays down their
 * open IOUs (a chosen one first, then the oldest); any excess is a credit.
 */
export default function IouPaymentDialog({ people, openEntries, payment, personId, appliesToEntryId, transaction, onSaved, onClose }: Props) {
  const linked = payment?.transaction ?? transaction ?? null;
  const [person, setPerson] = useState<number | ''>(payment?.personId ?? personId ?? '');
  const [direction, setDirection] = useState<IouDirection>(
    payment?.direction ?? (transaction ? (transaction.amount >= 0 ? 'Received' : 'Paid') : 'Received'));
  const [date, setDate] = useState(payment?.date ?? transaction?.date ?? today());
  const [amount, setAmount] = useState(payment ? payment.amount.toFixed(2) : transaction ? Math.abs(transaction.amount).toFixed(2) : '');
  const [method, setMethod] = useState<IouMethod>(payment?.method ?? (transaction ? 'BankTransfer' : 'Cash'));
  const [notes, setNotes] = useState(payment?.notes ?? '');
  const [appliesTo, setAppliesTo] = useState<number | ''>(payment?.appliesToEntryId ?? appliesToEntryId ?? '');
  const [keepLink, setKeepLink] = useState(true);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  useUnsavedChanges(dirty && !saving);

  const touch = <T,>(set: (v: T) => void) => (v: T) => { set(v); setDirty(true); };

  // IOUs this payment can go to: the person's open ones in the matching
  // direction (money received pays what they owe; money paid, what you owe).
  const targets = openEntries.filter(e => e.personId === person && (direction === 'Received' ? e.amount > 0 : e.amount < 0));

  const close = () => {
    if (dirty && !confirm('Discard these changes?')) return;
    onClose();
  };

  const save = async () => {
    setError('');
    const value = Math.round(parseFloat(amount) * 100) / 100;
    if (person === '') { setError('Choose who the payment is from or to.'); return; }
    if (!value || value <= 0) { setError('Enter an amount greater than zero.'); return; }
    setSaving(true);
    const data = {
      personId: person, date, amount: value, direction, method, notes,
      transactionId: keepLink ? linked?.id ?? null : null,
      appliesToEntryId: appliesTo === '' ? null : appliesTo,
    };
    try {
      if (payment) await updateIouPayment(payment.id, data);
      else await createIouPayment(data);
      onSaved();
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Failed to save.');
      setSaving(false);
    }
  };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) close(); }}>
      <div className={styles.modal} style={{ width: 480 }} role="dialog" aria-label={payment ? 'Edit payment' : 'Record payment'}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>{payment ? 'Edit payment' : 'Record payment'}</h2>
          <button type="button" className={styles.closeBtn} onClick={close} aria-label="Close">✕</button>
        </div>
        <form className={styles.modalBody} onSubmit={e => { e.preventDefault(); save(); }}>
          <div className={styles.field}>
            <span className={styles.label}>Direction</span>
            <div style={{ display: 'flex', gap: 16 }}>
              <label className={styles.checkLabel}>
                <input type="radio" checked={direction === 'Received'} onChange={() => { touch(setDirection)('Received'); setAppliesTo(''); }} /> I received money
              </label>
              <label className={styles.checkLabel}>
                <input type="radio" checked={direction === 'Paid'} onChange={() => { touch(setDirection)('Paid'); setAppliesTo(''); }} /> I paid money
              </label>
            </div>
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="pay-person">{direction === 'Received' ? 'From' : 'To'}</label>
            <select id="pay-person" className={styles.select} value={person}
              onChange={e => { touch(setPerson)(e.target.value === '' ? '' : Number(e.target.value)); setAppliesTo(''); }}>
              <option value="">Choose a person…</option>
              {people.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </div>

          <div style={{ display: 'flex', gap: 12 }}>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="pay-date">Date</label>
              <input id="pay-date" type="date" className={styles.input} value={date} onChange={e => touch(setDate)(e.target.value)} required />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="pay-amount">Amount</label>
              <input id="pay-amount" className={styles.input} inputMode="decimal" value={amount} onChange={e => touch(setAmount)(e.target.value)} placeholder="0.00" />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="pay-method">Method</label>
              <select id="pay-method" className={styles.select} value={method} onChange={e => touch(setMethod)(e.target.value as IouMethod)}>
                {IOU_METHODS.map(m => <option key={m} value={m}>{methodLabel(m)}</option>)}
              </select>
            </div>
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="pay-applies">Apply to</label>
            <select id="pay-applies" className={styles.select} value={appliesTo}
              onChange={e => touch(setAppliesTo)(e.target.value === '' ? '' : Number(e.target.value))} disabled={person === ''}>
              <option value="">Oldest open IOUs first (automatic)</option>
              {targets.map(e => (
                <option key={e.id} value={e.id}>
                  {e.date} · {e.description || 'IOU'} · {money(Math.abs(e.amount))} ({money(e.remaining)} left)
                </option>
              ))}
            </select>
          </div>

          {linked && (
            <div className={styles.field}>
              <label className={styles.checkLabel}>
                <input type="checkbox" checked={keepLink} onChange={e => touch(setKeepLink)(e.target.checked)} />
                Linked to {linked.date} · {linked.payee ?? '—'} · {money(linked.amount)} ({linked.accountName})
              </label>
            </div>
          )}

          <div className={styles.field}>
            <label className={styles.label} htmlFor="pay-notes">Notes</label>
            <textarea id="pay-notes" className={styles.textarea} rows={2} value={notes} onChange={e => touch(setNotes)(e.target.value)} />
          </div>

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
