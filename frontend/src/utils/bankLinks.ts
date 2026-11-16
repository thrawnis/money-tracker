import type { SimpleFinAccount } from '../api/simplefin';
import type { Account } from '../types';
import { confirmDialog } from './dialogs';

/**
 * Asks before linking or unlinking a bank account — either changes where
 * future transactions land and restarts that bank account's sync history.
 * (A link can't be moved to another account directly: unlink first.)
 * Returns true to go ahead.
 */
export async function confirmLinkChange(sf: SimpleFinAccount, target: Account | null): Promise<boolean> {
  if (target == null) {
    const pending = sf.pendingDraftId != null
      ? ` Its ${sf.pendingDraftRows ?? ''} unreviewed synced transaction(s) will be discarded.`
      : '';
    return (await confirmDialog(
      `Unlink "${sf.name}"${sf.linkedAccountName ? ` from "${sf.linkedAccountName}"` : ''}?\n\n`
      + `Bank sync will stop updating that account.${pending} Transactions already imported stay in the register. `
      + 'Linking again later starts its sync history fresh.',
    ));
  }
  return (await confirmDialog(
    `Link bank account "${sf.name}" to "${target.name}"?\n\n`
    + `Syncing will ${target.type === 'Investment' ? `record its balance in "${target.name}" (balance only)` : `import its transactions into "${target.name}" for your review`}. `
    + 'To change it later you\'ll need to unlink it first.',
  ));
}
