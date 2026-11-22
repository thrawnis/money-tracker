import { useState } from 'react';
import { updateIouPerson, deleteIouPerson, type IouPerson } from '../api/ious';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import styles from './AccountEditModal.module.css';

type ApiError = { response?: { data?: { message?: string } } };

interface Props {
  person: IouPerson;
  /** Called after a save ('saved') or delete ('deleted'). */
  onDone: (what: 'saved' | 'deleted') => void;
  onClose: () => void;
}

/** Edit a person on the IOUs page (name, email for payment links, notes), or delete them. */
export default function IouPersonDialog({ person, onDone, onClose }: Props) {
  const [name, setName] = useState(person.name);
  const [email, setEmail] = useState(person.email ?? '');
  const [notes, setNotes] = useState(person.notes ?? '');
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useUnsavedChanges(dirty && !busy);

  const touch = (set: (v: string) => void) => (v: string) => { set(v); setDirty(true); };

  const close = async () => {
    if (dirty && !(await confirmDialog('Discard these changes?'))) return;
    onClose();
  };

  const save = async () => {
    if (!name.trim()) { setError('Enter a name.'); return; }
    setBusy(true); setError('');
    try {
      // An empty email clears it (the server treats "" as "no email").
      await updateIouPerson(person.id, name.trim(), notes.trim() || undefined, email.trim());
      setDirty(false);
      onDone('saved');
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Failed to save.');
    } finally { setBusy(false); }
  };

  const remove = async () => {
    if (!(await confirmDialog(`Delete ${person.name} and all of their IOUs and payments? This can't be undone.`,
      { title: 'Delete person', confirmLabel: 'Delete', danger: true }))) return;
    setBusy(true); setError('');
    try { await deleteIouPerson(person.id); setDirty(false); onDone('deleted'); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to delete.'); setBusy(false); }
  };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) void close(); }}>
      <div className={styles.modal} role="dialog" aria-label={`Edit ${person.name}`}>
        <div className={styles.modalHeader}>
          <span className={styles.modalTitle}>Edit person</span>
          <button type="button" className={styles.closeBtn} onClick={() => void close()} aria-label="Close">✕</button>
        </div>
        <form className={styles.modalBody} onSubmit={e => { e.preventDefault(); void save(); }}>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="person-name">Name</label>
            <input id="person-name" className={styles.input} value={name} onChange={e => touch(setName)(e.target.value)} autoFocus required />
          </div>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="person-email">Email (for payment links)</label>
            <input id="person-email" type="email" className={styles.input} value={email} onChange={e => touch(setEmail)(e.target.value)}
              placeholder="name@example.com" autoComplete="off" />
          </div>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="person-notes">Notes</label>
            <textarea id="person-notes" className={styles.textarea} rows={2} value={notes} onChange={e => touch(setNotes)(e.target.value)} />
          </div>
          {error && <div className={styles.error}>{error}</div>}
          <div className={styles.actions}>
            <button type="submit" className={styles.btnPrimary} disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
            <button type="button" className={styles.btnSecondary} onClick={() => void close()} disabled={busy}>Cancel</button>
            <span style={{ flex: 1 }} />
            <button type="button" className={styles.btnDanger} onClick={() => void remove()} disabled={busy}>Delete person</button>
          </div>
        </form>
      </div>
    </div>
  );
}
