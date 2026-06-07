import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { createAccount } from '../api/accounts';
import { getInstitutions, createInstitution } from '../api/institutions';
import type { AccountType, Institution } from '../types';
import styles from './AccountForm.module.css';

interface Props {
  onCreated?: () => void;
}

const ACCOUNT_TYPES: AccountType[] = [
  'Checking', 'Savings', 'CreditCard', 'Cash', 'Loan', 'Investment', 'Other',
];

export default function AccountForm({ onCreated }: Props) {
  usePageTitle('Add Account');
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [type, setType] = useState<AccountType>('Checking');
  const [openingBalance, setOpeningBalance] = useState('0');
  const [institutionId, setInstitutionId] = useState<number | ''>('');
  const [accountNumber, setAccountNumber] = useState('');
  const [notes, setNotes] = useState('');
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  const [addingInstitution, setAddingInstitution] = useState(false);
  const [newInstitutionName, setNewInstitutionName] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    getInstitutions().then(setInstitutions).catch(console.error);
  }, []);

  const handleAddInstitution = async () => {
    if (!newInstitutionName.trim()) return;
    try {
      const inst = await createInstitution(newInstitutionName.trim());
      setInstitutions(prev => [...prev, inst]);
      setInstitutionId(inst.id);
      setAddingInstitution(false);
      setNewInstitutionName('');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to create institution.');
    }
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) { setError('Name is required.'); return; }
    setLoading(true);
    setError('');
    try {
      const account = await createAccount({
        name: name.trim(),
        type,
        openingBalance: parseFloat(openingBalance) || 0,
        institutionId: institutionId !== '' ? institutionId : undefined,
        accountNumber: accountNumber.trim() || undefined,
        notes: notes.trim() || undefined,
        isActive: true,
      });
      onCreated?.();
      navigate(`/accounts/${account.id}`);
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to create account.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Add Account</h2>
      </div>
      <form className={styles.form} onSubmit={handleSubmit}>
        <div className={styles.field}>
          <label className={styles.label}>Name *</label>
          <input
            className={styles.input}
            value={name}
            onChange={e => setName(e.target.value)}
            autoFocus
            placeholder="e.g. Chase Checking"
          />
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Type</label>
          <select className={styles.select} value={type} onChange={e => setType(e.target.value as AccountType)}>
            {ACCOUNT_TYPES.map(t => (
              <option key={t} value={t}>{t === 'CreditCard' ? 'Credit Card' : t}</option>
            ))}
          </select>
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Opening Balance</label>
          <input
            className={styles.input}
            type="number"
            step="0.01"
            value={openingBalance}
            onChange={e => setOpeningBalance(e.target.value)}
          />
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Institution</label>
          {!addingInstitution ? (
            <div className={styles.institutionRow}>
              <select
                className={styles.select}
                value={institutionId}
                onChange={e => setInstitutionId(e.target.value === '' ? '' : Number(e.target.value))}
              >
                <option value="">— None —</option>
                {institutions.map(i => (
                  <option key={i.id} value={i.id}>{i.name}</option>
                ))}
              </select>
              <button type="button" className={styles.btnLink} onClick={() => setAddingInstitution(true)}>
                + Add new
              </button>
            </div>
          ) : (
            <div className={styles.institutionRow}>
              <input
                className={styles.input}
                value={newInstitutionName}
                onChange={e => setNewInstitutionName(e.target.value)}
                placeholder="Institution name"
                autoFocus
              />
              <button type="button" className={styles.btnSecondary} onClick={handleAddInstitution}>Save</button>
              <button type="button" className={styles.btnLink} onClick={() => setAddingInstitution(false)}>Cancel</button>
            </div>
          )}
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Account Number</label>
          <input
            className={styles.input}
            value={accountNumber}
            onChange={e => setAccountNumber(e.target.value)}
            placeholder="Optional"
          />
        </div>

        <div className={styles.field}>
          <label className={styles.label}>Notes</label>
          <textarea
            className={styles.textarea}
            value={notes}
            onChange={e => setNotes(e.target.value)}
            rows={3}
            placeholder="Optional"
          />
        </div>

        {error && <div className={styles.error}>{error}</div>}

        <div className={styles.actions}>
          <button type="submit" className={styles.btnPrimary} disabled={loading}>
            {loading ? 'Creating…' : 'Create Account'}
          </button>
          <button type="button" className={styles.btnSecondary} onClick={() => navigate('/')}>
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}
