import { useState, type FormEvent } from 'react';
import { updateAccount } from '../api/accounts';
import { createInstitution } from '../api/institutions';
import { useDeleteAccountFlow } from '../hooks/useDeleteAccountFlow';
import type { Account, AccountType, Institution } from '../types';
import styles from './AccountEditModal.module.css';

const ACCOUNT_TYPES: AccountType[] = [
  'Checking', 'Savings', 'CreditCard', 'Cash', 'Loan', 'Investment', 'Other',
];

interface Props {
  account: Account;
  institutions: Institution[];
  onSaved: (updated: Account) => void;
  onDeleted: () => void;
  onClose: () => void;
}

export default function AccountEditModal({ account, institutions: initialInstitutions, onSaved, onDeleted, onClose }: Props) {
  const [name, setName] = useState(account.name);
  const [type, setType] = useState<AccountType>(account.type);
  const [isActive, setIsActive] = useState(account.isActive);
  const [institutionId, setInstitutionId] = useState<number | ''>(account.institutionId ?? '');
  const [accountNumber, setAccountNumber] = useState(account.accountNumber ?? '');
  const [notes, setNotes] = useState(account.notes ?? '');
  const [institutions, setInstitutions] = useState(initialInstitutions);
  const [addingInstitution, setAddingInstitution] = useState(false);
  const [newInstitutionName, setNewInstitutionName] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const { requestDelete, deleting, error: deleteError, modal: deleteModal } = useDeleteAccountFlow(onDeleted);

  const handleSave = async (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) { setError('Name is required.'); return; }
    setSaving(true); setError('');
    try {
      const updated = await updateAccount(account.id, {
        name: name.trim(), type, isActive,
        notes: notes.trim() || undefined,
        institutionId: institutionId !== '' ? institutionId : undefined,
        accountNumber: accountNumber.trim() || undefined,
      });
      onSaved(updated);
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to save account.');
      setSaving(false);
    }
  };

  const handleAddInstitution = async () => {
    if (!newInstitutionName.trim()) return;
    try {
      const inst = await createInstitution(newInstitutionName.trim());
      setInstitutions(prev => [...prev, inst]);
      setInstitutionId(inst.id);
      setAddingInstitution(false);
      setNewInstitutionName('');
    } catch { setError('Failed to create institution.'); }
  };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={styles.modal}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>Edit Account</h2>
          <button className={styles.closeBtn} onClick={onClose} aria-label="Close">✕</button>
        </div>
        <form className={styles.modalBody} onSubmit={handleSave}>
          <div className={styles.field}>
            <label className={styles.label}>Name *</label>
            <input className={styles.input} value={name} onChange={e => setName(e.target.value)} autoFocus />
          </div>
          <div className={styles.field}>
            <label className={styles.label}>Type</label>
            <select className={styles.select} value={type} onChange={e => setType(e.target.value as AccountType)}>
              {ACCOUNT_TYPES.map(t => <option key={t} value={t}>{t === 'CreditCard' ? 'Credit Card' : t}</option>)}
            </select>
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
                  {institutions.map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
                </select>
                <button type="button" className={styles.btnLink} onClick={() => setAddingInstitution(true)}>+ New</button>
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
                <button type="button" className={styles.btnLink} onClick={() => { setAddingInstitution(false); setNewInstitutionName(''); }}>Cancel</button>
              </div>
            )}
          </div>
          <div className={styles.field}>
            <label className={styles.label}>Account Number</label>
            <input className={styles.input} value={accountNumber} onChange={e => setAccountNumber(e.target.value)} placeholder="Optional" />
          </div>
          <div className={styles.field}>
            <label className={styles.label}>Notes</label>
            <textarea className={styles.textarea} rows={3} value={notes} onChange={e => setNotes(e.target.value)} placeholder="Optional" />
          </div>
          <div className={styles.field}>
            <label className={styles.checkLabel}>
              <input type="checkbox" checked={isActive} onChange={e => setIsActive(e.target.checked)} />
              Active
            </label>
          </div>
          {(error || deleteError) && <div className={styles.error}>{error || deleteError}</div>}
          <div className={styles.actions}>
            <button type="submit" className={styles.btnPrimary} disabled={saving || deleting}>
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button type="button" className={styles.btnSecondary} onClick={onClose} disabled={saving || deleting}>Cancel</button>
            <button type="button" className={styles.btnDanger} onClick={() => requestDelete(account)} disabled={saving || deleting}>
              {deleting ? 'Deleting…' : 'Delete Account'}
            </button>
          </div>
        </form>
      </div>
      {deleteModal}
    </div>
  );
}
