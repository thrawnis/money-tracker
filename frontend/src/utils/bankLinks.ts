import type { SimpleFinAccount } from '../api/simplefin';
import type { Account } from '../types';

/**
 * Asks before any change to which account a bank account feeds — linking,
 * relinking or unlinking all change where future transactions land and
 * restart that bank account's sync history. Returns true to go ahead.
 */
export function confirmLinkChange(sf: SimpleFinAccount, target: Account | null): boolean {
  const pending = sf.pendingDraftId != null
    ? ` Its ${sf.pendingDraftRows ?? ''} unreviewed synced transaction(s) will be discarded.`
    : '';
  if (target == null) {
    return confirm(
      `Unlink "${sf.name}"${sf.linkedAccountName ? ` from "${sf.linkedAccountName}"` : ''}?\n\n`
      + `Bank sync will stop updating that account.${pending} Transactions already imported stay in the register. `
      + 'Linking again later starts its sync history fresh.',
    );
  }
  if (sf.linkedAccountId != null) {
    return confirm(
      `Move "${sf.name}" from "${sf.linkedAccountName}" to "${target.name}"?\n\n`
      + `Future transactions will go into "${target.name}" instead, and its sync history starts fresh.${pending}`,
    );
  }
  return confirm(
    `Link bank account "${sf.name}" to "${target.name}"?\n\n`
    + `Syncing will ${target.type === 'Investment' ? `record its balance in "${target.name}" (balance only)` : `import its transactions into "${target.name}" for your review`}.`,
  );
}
