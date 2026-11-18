import { useState } from 'react';
import CategoryAutocomplete from './CategoryAutocomplete';
import { saveLoan, removeLoan, type LoanDetails, type LoanInfo } from '../api/loans';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import { flattenCategories } from '../utils/categories';
import { loanPayment } from '../utils/loanMath';
import type { Category } from '../types';
import styles from './ExportModal.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });

/** Edit a loan's terms and automatic interest / escrow charges. */
export default function LoanDetailsModal({ accountId, accountName, info, categories, onSaved, onClose }: {
  accountId: number; accountName: string; info: LoanInfo; categories: Category[];
  onSaved: (i: LoanInfo | null) => void; onClose: () => void;
}) {
  const flat = flattenCategories(categories);
  const label = (id?: number | null) => flat.find(c => c.id === id)?.label ?? '';
  const d = info.details;
  const initial = {
    rate: d ? String(d.annualRate) : '',
    original: d?.originalAmount != null ? String(d.originalAmount) : '',
    term: d?.termMonths != null ? String(d.termMonths) : '',
    first: d?.firstPaymentDate ?? '',
    payment: d?.monthlyPayment != null ? String(d.monthlyPayment) : '',
    escrow: d && d.escrowAmount > 0 ? String(d.escrowAmount) : '',
    auto: d?.autoCharges ?? true,
    from: d?.autoChargesFrom ?? info.today,
    interestCat: label(d?.interestCategoryId),
    escrowCat: label(d?.escrowCategoryId),
  };
  const [f, setF] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const dirty = JSON.stringify(f) !== JSON.stringify(initial);
  useUnsavedChanges(dirty && !saving);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });

  const num = (s: string) => (s.trim() === '' ? null : Number(s.replace(/[$,]/g, '')));
  const implied = (num(f.original) ?? 0) > 0 && (num(f.term) ?? 0) > 0 && num(f.rate) != null
    ? loanPayment(num(f.original)!, num(f.rate)!, num(f.term)!) : null;

  const close = async () => { if (!dirty || await confirmDialog('Discard your changes to the loan details?')) onClose(); };

  const catId = (text: string, what: string) => {
    if (!text.trim()) return null;
    const m = flat.find(c => c.label.toLowerCase() === text.trim().toLowerCase());
    if (!m) throw new Error(`There's no category named "${text}" for ${what}. Pick one from the suggestions.`);
    return m.id;
  };

  const save = async () => {
    setError('');
    let body: LoanDetails;
    try {
      const rate = num(f.rate);
      if (rate == null || Number.isNaN(rate)) throw new Error('Enter the interest rate (e.g. 6.5).');
      for (const [v, n] of [[f.original, 'original amount'], [f.term, 'term'], [f.payment, 'monthly payment'], [f.escrow, 'escrow']] as const)
        if (v.trim() && Number.isNaN(num(v)!)) throw new Error(`The ${n} isn't a number.`);
      body = {
        annualRate: rate,
        originalAmount: num(f.original), termMonths: num(f.term) == null ? null : Math.round(num(f.term)!),
        firstPaymentDate: f.first || null, monthlyPayment: num(f.payment), escrowAmount: num(f.escrow) ?? 0,
        autoCharges: f.auto, autoChargesFrom: f.from || null,
        interestCategoryId: catId(f.interestCat, 'interest'), escrowCategoryId: catId(f.escrowCat, 'escrow'),
      };
    } catch (e) { setError((e as Error).message); return; }
    if (body.autoCharges && !(d?.autoCharges) && !(await confirmDialog(
      `Add interest${body.escrowAmount > 0 ? ' and escrow' : ''} charges to ${accountName} automatically for every payment from ${new Date(f.from + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })} on?\n\n`
      + 'Payments are transfers into this loan (e.g. from checking). The loan balance will then go down by the principal only, and interest shows up as spending.',
      { title: 'Automatic interest', confirmLabel: 'Turn on' }))) return;
    setSaving(true);
    try { onSaved(await saveLoan(accountId, body)); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to save.'); setSaving(false); }
  };

  const remove = async () => {
    if (!(await confirmDialog(`Remove the loan details for ${accountName}? Interest and escrow charges already added stay in the register, but no new ones will be added.`))) return;
    setSaving(true);
    try { await removeLoan(accountId); onSaved(null); }
    catch { setError('Failed to remove.'); setSaving(false); }
  };

  const field = { width: '100%', boxSizing: 'border-box' as const };
  const row = { display: 'flex', gap: 10 } as const;
  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) void close(); }}>
      <div className={`${styles.modal} ${styles.modalWide}`} role="dialog" aria-labelledby="loan-title" style={{ maxHeight: '90vh', overflowY: 'auto' }}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle} id="loan-title">Loan Details — {accountName}</h2>
          <button className={styles.closeBtn} onClick={() => void close()} aria-label="Close">x</button>
        </div>
        <form className={styles.modalBody} onSubmit={e => { e.preventDefault(); void save(); }}>
          <div style={row}>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-rate">Interest rate (% a year)</label>
              <input id="ln-rate" className={styles.input} style={field} inputMode="decimal" value={f.rate} onChange={set('rate')} placeholder="6.5" autoFocus />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-first">First payment date</label>
              <input id="ln-first" className={styles.input} style={field} type="date" value={f.first} onChange={set('first')} />
            </div>
          </div>
          <div style={row}>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-orig">Amount borrowed</label>
              <input id="ln-orig" className={styles.input} style={field} inputMode="decimal" value={f.original} onChange={set('original')} placeholder="320000" />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-term">Term (months)</label>
              <input id="ln-term" className={styles.input} style={field} inputMode="numeric" value={f.term} onChange={set('term')} placeholder="360" />
            </div>
          </div>
          <div style={row}>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-pay">Monthly payment (principal + interest)</label>
              <input id="ln-pay" className={styles.input} style={field} inputMode="decimal" value={f.payment} onChange={set('payment')}
                placeholder={implied != null ? `${implied.toFixed(2)} (worked out)` : 'e.g. 1970.38'} />
            </div>
            <div className={styles.field} style={{ flex: 1 }}>
              <label className={styles.label} htmlFor="ln-escrow">Monthly escrow (optional)</label>
              <input id="ln-escrow" className={styles.input} style={field} inputMode="decimal" value={f.escrow} onChange={set('escrow')} placeholder="0" />
            </div>
          </div>
          <p className={styles.authHint} style={{ marginTop: 0 }}>
            Leave the payment blank to work it out from the amount, rate and term{implied != null ? ` (${money(implied)})` : ''}. Escrow is the part of each
            payment for property tax and insurance.
          </p>

          <div className={styles.field}>
            <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
              <input type="checkbox" checked={f.auto} onChange={e => setF({ ...f, auto: e.target.checked })} style={{ marginTop: 3 }} />
              <span><strong>Add interest{num(f.escrow) ? ' and escrow' : ''} charges automatically</strong><br />
                <span className={styles.authHint} style={{ margin: 0 }}>
                  For each payment into this loan by transfer, the month's interest on what was owed is added as a charge (and escrow, if set),
                  so the balance goes down by the principal only. Not used when the loan is synced as "balance only" — the bank's balance is used then.
                </span></span>
            </label>
          </div>
          {f.auto && (
            <>
              <div className={styles.field}>
                <label className={styles.label} htmlFor="ln-from">Starting with payments on or after</label>
                <input id="ln-from" className={styles.input} style={field} type="date" value={f.from} onChange={set('from')} />
              </div>
              <div className={styles.field}>
                <label className={styles.label}>Interest category</label>
                <CategoryAutocomplete value={f.interestCat} onChange={v => setF({ ...f, interestCat: v })} categories={categories}
                  className={styles.input} placeholder="e.g. Housing: Mortgage Interest" />
              </div>
              {(num(f.escrow) ?? 0) > 0 && (
                <div className={styles.field}>
                  <label className={styles.label}>Escrow category</label>
                  <CategoryAutocomplete value={f.escrowCat} onChange={v => setF({ ...f, escrowCat: v })} categories={categories}
                    className={styles.input} placeholder="e.g. Housing: Property Tax & Insurance" />
                </div>
              )}
            </>
          )}
          {error && <div className={styles.error}>{error}</div>}
          <button type="submit" className={styles.btnPrimary} disabled={saving}>{saving ? 'Saving…' : 'Save'}</button>
          {d && (
            <button type="button" onClick={() => void remove()} disabled={saving}
              style={{ marginTop: 10, width: '100%', background: 'none', border: 'none', color: 'var(--c-cc0000)', cursor: 'pointer', textDecoration: 'underline' }}>
              Remove loan details
            </button>
          )}
        </form>
      </div>
    </div>
  );
}
