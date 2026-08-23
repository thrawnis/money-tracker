import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getTransaction, updateTransaction, deleteTransaction } from '../api/transactions';
import type { Account, Transaction } from '../types';
import TransactionForm, { type SplitInput } from './TransactionForm';
import styles from './TransactionDetailPanel.module.css';

interface Props {
  accountId: number;
  transactionId: number;
  accountName: string;
  accounts: Account[];
  onClose: () => void;
  onChanged: () => void;
  /** Reports the embedded edit form's dirty state so the parent list can
   *  guard row-switching the same way close/Escape are guarded here. */
  onDirtyChange?: (dirty: boolean) => void;
}

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' });
}

export default function TransactionDetailPanel({ accountId, transactionId, accountName, accounts, onClose, onChanged, onDirtyChange }: Props) {
  const navigate = useNavigate();
  const [tx, setTx] = useState<Transaction | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [mode, setMode] = useState<'view' | 'edit'>('view');
  const [deleting, setDeleting] = useState(false);
  const [formDirty, setFormDirty] = useState(false);

  // Same guard as the register: closing (X, Escape, overlay click) while the
  // edit form has unsaved changes must ask first, not silently discard.
  const confirmDiscardIfDirty = () =>
    !formDirty || confirm('You have unsaved changes to this transaction. Discard them?');
  const guardedClose = () => { if (confirmDiscardIfDirty()) onClose(); };

  const handleDirtyChange = (dirty: boolean) => {
    setFormDirty(dirty);
    onDirtyChange?.(dirty);
  };

  useEffect(() => {
    setLoading(true);
    setError('');
    setMode('view');
    getTransaction(accountId, transactionId)
      .then(setTx)
      .catch(() => setError('Failed to load transaction.'))
      .finally(() => setLoading(false));
  }, [accountId, transactionId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      if (!formDirty || confirm('You have unsaved changes to this transaction. Discard them?')) onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose, formDirty]);

  const handleSave = async (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits' | 'isVoided'> & { targetAccountId?: number; transferDestAccountId?: number; splits?: SplitInput[] }) => {
    if (tx && (tx.status === 'Cleared' || tx.status === 'Reconciled')) {
      const proceed = confirm(
        `This transaction is marked ${tx.status}. Editing it may affect your reconciled balance. Save changes anyway?`
      );
      if (!proceed) return;
    }
    await updateTransaction(accountId, transactionId, data);
    const updated = await getTransaction(data.targetAccountId ?? accountId, transactionId);
    setTx(updated);
    setMode('view');
    onChanged();
  };

  const handleDelete = async () => {
    if (!confirm('Delete this transaction?')) return;
    setDeleting(true);
    try {
      await deleteTransaction(accountId, transactionId);
      onChanged();
      onClose();
    } catch {
      setError('Failed to delete transaction.');
      setDeleting(false);
    }
  };

  const otherAccountName = (transferAccountId?: number) =>
    accounts.find(a => a.id === transferAccountId)?.name ?? 'account';

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) guardedClose(); }}>
      <div className={styles.panel}>
        <div className={styles.header}>
          <h3 className={styles.title}>{mode === 'edit' ? 'Edit Transaction' : 'Transaction Details'}</h3>
          <button className={styles.closeBtn} onClick={guardedClose} aria-label="Close">✕</button>
        </div>

        <div className={styles.body}>
          {loading && <p className={styles.hint}>Loading…</p>}
          {error && <p className={styles.error}>{error}</p>}

          {!loading && tx && mode === 'view' && (
            <>
              <div className={styles.field}>
                <span className={styles.label}>Account</span>
                <button
                  className={styles.accountLink}
                  onClick={() => navigate(`/accounts/${accountId}?tx=${transactionId}`)}
                  title="Open in register"
                >
                  {accountName}
                </button>
              </div>

              <div className={styles.field}>
                <span className={styles.label}>Date</span>
                <span>{formatDate(tx.date)}</span>
              </div>

              {tx.transferTransactionId ? (
                <div className={styles.field}>
                  <span className={styles.label}>Transfer</span>
                  <span className={styles.transferValue}>
                    {tx.amount < 0 ? '→ To ' : '← From '}
                    <button
                      className={styles.accountLink}
                      onClick={() => navigate(`/accounts/${tx.transferAccountId}?tx=${tx.transferTransactionId}`)}
                    >
                      {otherAccountName(tx.transferAccountId)}
                    </button>
                  </span>
                </div>
              ) : (
                <>
                  <div className={styles.field}>
                    <span className={styles.label}>Payee</span>
                    <span>{tx.payee?.name ?? '—'}</span>
                  </div>
                  {tx.splits && tx.splits.length > 0 ? (
                    <div className={styles.field}>
                      <span className={styles.label}>Categories (Split)</span>
                      <div className={styles.splitList}>
                        {tx.splits.map(s => (
                          <div key={s.id} className={styles.splitItem}>
                            <span className={styles.splitCategory}>{s.category?.name ?? 'Uncategorized'}</span>
                            {s.memo && <span className={styles.splitMemo}>{s.memo}</span>}
                            <span className={`${styles.splitAmount} ${s.amount < 0 ? styles.debit : styles.credit}`}>
                              {formatCurrency(s.amount)}
                            </span>
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : (
                    <div className={styles.field}>
                      <span className={styles.label}>Category</span>
                      <span>{tx.category?.name ?? '—'}</span>
                    </div>
                  )}
                </>
              )}

              <div className={styles.field}>
                <span className={styles.label}>Memo</span>
                <span>{tx.memo || '—'}</span>
              </div>

              {tx.checkNumber && (
                <div className={styles.field}>
                  <span className={styles.label}>Check #</span>
                  <span>{tx.checkNumber}</span>
                </div>
              )}

              <div className={styles.field}>
                <span className={styles.label}>Status</span>
                <span>{tx.status}{tx.isVoided ? ' (Voided)' : ''}</span>
              </div>

              <div className={styles.field}>
                <span className={styles.label}>Amount</span>
                <span className={`${styles.amount} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
                  {formatCurrency(tx.amount)}
                </span>
              </div>

              <div className={styles.actions}>
                <button className={styles.btnPrimary} onClick={() => setMode('edit')}>Edit</button>
                <button className={styles.btnDanger} onClick={handleDelete} disabled={deleting}>
                  {deleting ? 'Deleting…' : 'Delete'}
                </button>
                <button
                  className={styles.btnSecondary}
                  onClick={() => navigate(`/accounts/${accountId}?tx=${transactionId}`)}
                >
                  Open in Register
                </button>
              </div>
            </>
          )}

          {!loading && tx && mode === 'edit' && (
            <TransactionForm
              accountId={accountId}
              accounts={accounts}
              initial={tx}
              onSave={handleSave}
              onCancel={() => { handleDirtyChange(false); setMode('view'); }}
              onDirtyChange={handleDirtyChange}
            />
          )}
        </div>
      </div>
    </div>
  );
}
