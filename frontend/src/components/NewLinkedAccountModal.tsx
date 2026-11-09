import { useState, useEffect, type FormEvent } from 'react';
import { createAccount } from '../api/accounts';
import { getInstitutions, createInstitution } from '../api/institutions';
import type { SimpleFinAccount } from '../api/simplefin';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import type { Account, AccountType, Institution } from '../types';
import styles from './ExportModal.module.css';

const ACCOUNT_TYPES: { value: AccountType; label: string }[] = [
  { value: 'Checking', label: 'Checking' },
  { value: 'Savings', label: 'Savings' },
  { value: 'CreditCard', label: 'Credit Card' },
  { value: 'Investment', label: 'Investment' },
  { value: 'Loan', label: 'Loan' },
  { value: 'Cash', label: 'Cash' },
  { value: 'Other', label: 'Other' },
];

type ApiError = { response?: { data?: { message?: string } } };

interface Props {
  /** The bank account the new account will be linked to. */
  bankAccount: SimpleFinAccount;
  /** Existing account names, to avoid suggesting one that's taken. */
  existingNames: string[];
  /** Called with the created account; the caller links it. */
  onCreated: (account: Account) => void;
  onClose: () => void;
}

/**
 * Popup for Bank Sync's "+ New account…": the user fills in the new account's
 * details (nothing is guessed — the type must be chosen), and the caller links
 * the bank account to it once it's created.
 */
export default function NewLinkedAccountModal({ bankAccount: sf, existingNames, onCreated, onClose }: Props) {
  const org = sf.orgName?.trim() ?? '';
  const taken = existingNames.some(n => n.toLowerCase() === sf.name.trim().toLowerCase());
  const initialName = taken && org ? `${org} ${sf.name.trim()}` : sf.name.trim();

  const [name, setName] = useState(initialName);
  const [type, setType] = useState<AccountType | ''>('');
  const [openingBalance, setOpeningBalance] = useState('0');
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  // '' = none, 'new' = create one named newInstitution, else an existing id.
  const [institution, setInstitution] = useState<string>('');
  const [newInstitution, setNewInstitution] = useState(org);
  const [accountNumber, setAccountNumber] = useState('');
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const dirty = name !== initialName || type !== '' || openingBalance !== '0'
    || accountNumber !== '' || notes !== '' || newInstitution !== org;
  useUnsavedChanges(dirty && !saving);

  // Default the institution to the bank SimpleFIN reports: the existing one by
  // that name, or a new one with that name.
  useEffect(() => {
    getInstitutions().then(list => {
      setInstitutions(list);
      const match = org && list.find(i => i.name.toLowerCase() === org.toLowerCase());
      setInstitution(match ? String(match.id) : org ? 'new' : '');
    }).catch(() => setInstitution(org ? 'new' : ''));
  }, [org]);

  const close = () => {
    if (!dirty || confirm('Discard this new account?')) onClose();
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    if (!name.trim()) { setError('Enter a name for the account.'); return; }
    if (!type) { setError('Choose the account type.'); return; }
    const balance = Number(openingBalance || 0);
    if (!Number.isFinite(balance)) { setError('Enter a valid opening balance.'); return; }
    if (institution === 'new' && !newInstitution.trim()) { setError('Enter the institution name, or choose None.'); return; }

    setSaving(true);
    try {
      let institutionId: number | undefined = institution && institution !== 'new' ? Number(institution) : undefined;
      if (institution === 'new') institutionId = (await createInstitution(newInstitution.trim())).id;
      const account = await createAccount({
        name: name.trim(),
        type,
        openingBalance: balance,
        institutionId,
        accountNumber: accountNumber.trim() || undefined,
        notes: notes.trim() || undefined,
        isActive: true,
      });
      onCreated(account);
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Failed to create the account.');
      setSaving(false);
    }
  };

  const fieldStyle = { width: '100%', boxSizing: 'border-box' as const };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) close(); }}>
      <div className={styles.modal} style={{ maxHeight: '90vh', overflowY: 'auto' }} role="dialog" aria-labelledby="new-linked-account-title">
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle} id="new-linked-account-title">New Account for Bank Sync</h2>
          <button className={styles.closeBtn} onClick={close} aria-label="Close">x</button>
        </div>
        <form onSubmit={handleSubmit} className={styles.modalBody}>
          <p className={styles.authHint}>
            Creates a new account and syncs <strong>{sf.name}</strong>{org && <> ({org})</>} into it.
          </p>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-name">Account name</label>
            <input id="nla-name" className={styles.input} style={fieldStyle} value={name}
              onChange={e => setName(e.target.value)} autoFocus />
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-type">Account type</label>
            <select id="nla-type" className={styles.input} style={fieldStyle} value={type}
              onChange={e => setType(e.target.value as AccountType | '')}>
              <option value="">Choose a type…</option>
              {ACCOUNT_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
            {type === 'Investment' && (
              <div className={styles.authHint} style={{ margin: '4px 0 0' }}>
                Investment accounts sync their balance only (no transactions).
              </div>
            )}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-institution">Institution</label>
            <select id="nla-institution" className={styles.input} style={fieldStyle} value={institution}
              onChange={e => setInstitution(e.target.value)}>
              <option value="">None</option>
              {[...institutions].sort((a, b) => a.name.localeCompare(b.name))
                .map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
              <option value="new">+ New institution…</option>
            </select>
            {institution === 'new' && (
              <input className={styles.input} style={{ ...fieldStyle, marginTop: 6 }} value={newInstitution}
                onChange={e => setNewInstitution(e.target.value)} placeholder="Institution name"
                aria-label="New institution name" />
            )}
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-opening">Opening balance</label>
            <input id="nla-opening" className={styles.input} style={fieldStyle} type="number" step="0.01"
              value={openingBalance} onChange={e => setOpeningBalance(e.target.value)} />
            <div className={styles.authHint} style={{ margin: '4px 0 0' }}>
              The balance before the first transaction that will be synced.
            </div>
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-number">Account number (optional)</label>
            <input id="nla-number" className={styles.input} style={fieldStyle} value={accountNumber}
              onChange={e => setAccountNumber(e.target.value)} autoComplete="off" />
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="nla-notes">Notes (optional)</label>
            <textarea id="nla-notes" className={styles.input} style={fieldStyle} rows={2} value={notes}
              onChange={e => setNotes(e.target.value)} />
          </div>

          {error && <div className={styles.error}>{error}</div>}

          <button type="submit" className={styles.btnPrimary} disabled={saving}>
            {saving ? 'Creating…' : 'Create & Link'}
          </button>
        </form>
      </div>
    </div>
  );
}
