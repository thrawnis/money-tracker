import { useState } from 'react';
import { savePaymentDetails, type PaymentDetails, type PaymentMethod, type PaymentType } from '../api/ious';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import styles from './ExportModal.module.css';

type ApiError = { response?: { data?: { message?: string } } };

const KINDS: { type: PaymentType; name: string; placeholder: string; hint: string }[] = [
  { type: 'zelle', name: 'Zelle', placeholder: 'you@example.com or 555-123-4567', hint: 'Shown with a Copy button (Zelle has no pay link).' },
  { type: 'venmo', name: 'Venmo', placeholder: '@your-username', hint: 'Opens Venmo with the amount filled in.' },
  { type: 'paypal', name: 'PayPal', placeholder: 'PayPal.Me name, e.g. JaneDoe', hint: 'Opens paypal.me with the amount filled in.' },
  { type: 'cashapp', name: 'Cash App', placeholder: '$Cashtag', hint: 'Opens Cash App with the amount filled in.' },
  { type: 'applecash', name: 'Apple Cash', placeholder: 'Phone or email', hint: 'Shown with a Copy button.' },
  { type: 'googlepay', name: 'Google Pay', placeholder: 'Phone or email', hint: 'Shown with a Copy button.' },
];

/**
 * How people pay you back: shown as one-tap buttons on IOU payment pages.
 * Blank fields are left off. "Other" rows take any name plus a link or details.
 */
export default function PaymentDetailsModal({ details, onSaved, onClose }: {
  details: PaymentDetails; onSaved: (d: PaymentDetails) => void; onClose: () => void;
}) {
  const initial = () => {
    const byType = new Map(details.methods.filter(m => m.type !== 'other').map(m => [m.type, m.value]));
    return {
      name: details.displayName ?? '',
      values: Object.fromEntries(KINDS.map(k => [k.type, byType.get(k.type) ?? ''])) as Record<string, string>,
      others: details.methods.filter(m => m.type === 'other').map(m => ({ label: m.label ?? '', value: m.value })),
    };
  };
  const [form, setForm] = useState(initial);
  const [baseline] = useState(() => JSON.stringify(initial()));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const dirty = JSON.stringify(form) !== baseline;
  useUnsavedChanges(dirty && !saving);

  const close = async () => { if (!dirty || await confirmDialog('Discard your changes to your payment details?')) onClose(); };

  const save = async () => {
    setSaving(true); setError('');
    const methods: PaymentMethod[] = [
      ...KINDS.map(k => ({ type: k.type, value: form.values[k.type].trim() })).filter(m => m.value),
      ...form.others.filter(o => o.label.trim() || o.value.trim()).map(o => ({ type: 'other' as const, label: o.label.trim(), value: o.value.trim() })),
    ];
    try { onSaved(await savePaymentDetails({ displayName: form.name.trim() || null, methods })); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to save.'); setSaving(false); }
  };

  const field = { width: '100%', boxSizing: 'border-box' as const };
  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) void close(); }}>
      <div className={`${styles.modal} ${styles.modalWide}`} role="dialog" aria-labelledby="pay-details-title" style={{ maxHeight: '90vh', overflowY: 'auto' }}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle} id="pay-details-title">Your Payment Details</h2>
          <button className={styles.closeBtn} onClick={() => void close()} aria-label="Close">x</button>
        </div>
        <form className={styles.modalBody} onSubmit={e => { e.preventDefault(); void save(); }}>
          <p className={styles.authHint}>
            Shown on the payment pages you send people, so they can pay you back in a tap. Fill in the ones you use; leave the rest blank.
          </p>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="pd-name">Your name on the page</label>
            <input id="pd-name" className={styles.input} style={field} value={form.name} maxLength={60}
              onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Jane" />
          </div>
          {KINDS.map(k => (
            <div className={styles.field} key={k.type}>
              <label className={styles.label} htmlFor={`pd-${k.type}`}>{k.name}</label>
              <input id={`pd-${k.type}`} className={styles.input} style={field} value={form.values[k.type]}
                placeholder={k.placeholder} autoComplete="off" spellCheck={false}
                onChange={e => setForm({ ...form, values: { ...form.values, [k.type]: e.target.value } })} />
              <div className={styles.authHint} style={{ margin: '3px 0 0' }}>{k.hint}</div>
            </div>
          ))}
          <div className={styles.field}>
            <span className={styles.label}>Other ways to pay</span>
            {form.others.map((o, i) => (
              <div key={i} style={{ display: 'flex', gap: 6, marginBottom: 6 }}>
                <input className={styles.input} style={{ flex: '0 0 32%', minWidth: 0 }} value={o.label} placeholder="Name, e.g. Chime" maxLength={40}
                  aria-label="Payment method name"
                  onChange={e => setForm({ ...form, others: form.others.map((x, j) => j === i ? { ...x, label: e.target.value } : x) })} />
                <input className={styles.input} style={{ flex: 1, minWidth: 0 }} value={o.value} placeholder="https:// link, or handle / details" maxLength={200}
                  aria-label="Payment details"
                  onChange={e => setForm({ ...form, others: form.others.map((x, j) => j === i ? { ...x, value: e.target.value } : x) })} />
                <button type="button" className={styles.closeBtn} style={{ color: 'var(--c-cc0000)' }} aria-label="Remove"
                  onClick={() => setForm({ ...form, others: form.others.filter((_, j) => j !== i) })}>×</button>
              </div>
            ))}
            <button type="button" style={{ background: 'none', border: 'none', color: 'var(--c-336699)', cursor: 'pointer', padding: 0, textDecoration: 'underline' }}
              onClick={() => setForm({ ...form, others: [...form.others, { label: '', value: '' }] })}>+ Add another</button>
          </div>
          {error && <div className={styles.error}>{error}</div>}
          <button type="submit" className={styles.btnPrimary} disabled={saving}>{saving ? 'Saving…' : 'Save'}</button>
        </form>
      </div>
    </div>
  );
}
