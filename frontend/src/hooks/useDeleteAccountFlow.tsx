import { useState } from 'react';
import { deleteAccount } from '../api/accounts';
import ReauthModal from '../components/ReauthModal';
import type { Account } from '../types';

// Shared delete flow used by both the account edit modal and the register's
// settings menu: destructive confirm, then password/TOTP re-authentication
// (required — deleting an account is permanent), then an optional backup
// note, then the delete itself.
export function useDeleteAccountFlow(onDeleted: () => void) {
  const [pendingAccount, setPendingAccount] = useState<Account | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState('');

  const requestDelete = (account: Account) => {
    setError('');
    if (!confirm(`Delete "${account.name}"? This permanently deletes the account and all its transactions. This cannot be undone. A backup is saved on the server first.`)) return;
    setPendingAccount(account);
  };

  const handleVerified = async (exportToken: string) => {
    const account = pendingAccount;
    setPendingAccount(null);
    if (!account) return;

    const note = prompt('Optional note for the backup (why you\'re deleting this account):') ?? undefined;
    setDeleting(true);
    try {
      await deleteAccount(account.id, exportToken, note || undefined);
      onDeleted();
    } catch {
      setError('Failed to delete account.');
    } finally {
      setDeleting(false);
    }
  };

  const modal = pendingAccount ? (
    <ReauthModal
      title="Confirm Identity"
      hint={`Confirm your identity to delete "${pendingAccount.name}".`}
      onVerified={handleVerified}
      onClose={() => setPendingAccount(null)}
    />
  ) : null;

  return { requestDelete, deleting, error, modal };
}
