import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  connectSimpleFin, disconnectSimpleFin, linkSimpleFinAccount,
  setSimpleFinBalanceOnly,
  type SimpleFinStatus, type SimpleFinAccount,
} from '../api/simplefin';
import { createAccount } from '../api/accounts';
import { getInstitutions, createInstitution } from '../api/institutions';
import { useAuth } from '../contexts/auth-context';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { formatDateTime } from '../utils/timezone';
import type { Account, AccountType } from '../types';
import { confirmLinkChange } from '../utils/bankLinks';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const apiMsg = (err: unknown, fallback: string) =>
  (err as ApiError)?.response?.data?.message ?? fallback;

function formatMoney(amount?: number | null, currency?: string | null) {
  if (amount == null) return '—';
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: currency || 'USD' }).format(amount);
  } catch {
    return amount.toFixed(2);
  }
}

const ACCOUNT_TYPES: { value: AccountType; label: string }[] = [
  { value: 'Checking', label: 'Checking' },
  { value: 'Savings', label: 'Savings' },
  { value: 'CreditCard', label: 'Credit Card' },
  { value: 'Investment', label: 'Investment' },
  { value: 'Loan', label: 'Loan' },
  { value: 'Cash', label: 'Cash' },
  { value: 'Other', label: 'Other' },
];

/** A best guess at the account type from the bank's account name. */
function guessType(name: string): AccountType {
  const n = name.toLowerCase();
  if (/credit|card|visa|mastercard|amex|discover/.test(n)) return 'CreditCard';
  if (/401|403b|ira\b|roth|brokerage|invest|retire|hsa|529|pension/.test(n)) return 'Investment';
  if (/loan|mortgage|heloc|auto ?loan/.test(n)) return 'Loan';
  if (/saving|money market/.test(n)) return 'Savings';
  return 'Checking';
}

interface NewAccountDraft { sfId: number; name: string; type: AccountType; openingBalance: string }

function formatDay(d?: string | null) {
  if (!d) return '—';
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

/**
 * Bank sync settings, shown on the Accounts page: connecting SimpleFIN Bridge
 * and choosing which account each bank account feeds. Syncing itself happens on the Bank Sync screen
 * (pages/BankSync.tsx), which stages new transactions as import drafts —
 * nothing reaches a register until it's reviewed and committed.
 */
interface Props {
  /** Bank sync status, owned by the page so its account list can show links too. */
  status: SimpleFinStatus | null;
  onStatus: (status: SimpleFinStatus) => void;
  /** The user's accounts (active and inactive). */
  accounts: Account[];
  /** Called after an account is created here, so the page can reload its list. */
  onAccountCreated: () => void;
}

export default function BankSyncSettings({ status, onStatus: setStatus, accounts, onAccountCreated }: Props) {
  const navigate = useNavigate();
  const { user } = useAuth();
  const [token, setToken] = useState('');
  const [busy, setBusy] = useState<'connect' | 'disconnect' | number | null>(null);
  const [error, setError] = useState('');
  // The inline "new account" form, open on at most one bank account's row.
  const [newAccount, setNewAccount] = useState<NewAccountDraft | null>(null);
  useUnsavedChanges(newAccount !== null);

  const handleConnect = async () => {
    if (!token.trim()) return;
    setBusy('connect');
    setError('');
    try {
      setStatus(await connectSimpleFin(token.trim()));
      setToken('');
    } catch (err) {
      setError(apiMsg(err, 'Failed to connect to SimpleFIN.'));
    } finally {
      setBusy(null);
    }
  };

  const startNewAccount = (sf: SimpleFinAccount) => {
    setError('');
    const taken = new Set(accounts.map(a => a.name.toLowerCase()));
    // Prefer the bank's own name; add the bank if that name is already used here.
    let name = sf.name.trim();
    if (taken.has(name.toLowerCase()) && sf.orgName) name = `${sf.orgName} ${name}`;
    setNewAccount({ sfId: sf.id, name, type: guessType(sf.name), openingBalance: '0' });
  };

  // Creates the account (filed under the bank as its institution, created if
  // needed), then links the bank account to it.
  const handleCreateAndLink = async (sf: SimpleFinAccount) => {
    if (!newAccount) return;
    const name = newAccount.name.trim();
    if (!name) { setError('Enter a name for the new account.'); return; }
    const openingBalance = Number(newAccount.openingBalance || 0);
    if (!Number.isFinite(openingBalance)) { setError('Enter a valid opening balance.'); return; }
    setBusy(sf.id);
    setError('');
    let created: Account;
    try {
      let institutionId: number | undefined;
      if (sf.orgName?.trim()) {
        try {
          const org = sf.orgName.trim();
          const existing = (await getInstitutions()).find(i => i.name.toLowerCase() === org.toLowerCase());
          institutionId = existing?.id ?? (await createInstitution(org)).id;
        } catch {
          institutionId = undefined;   // not worth failing over; the account is what matters
        }
      }
      created = await createAccount({ name, type: newAccount.type, openingBalance, institutionId, isActive: true });
    } catch (err) {
      setError(apiMsg(err, 'Failed to create the account.'));
      setBusy(null);
      return;
    }
    onAccountCreated();
    setNewAccount(null);
    try {
      setStatus(await linkSimpleFinAccount(sf.id, created.id));
    } catch (err) {
      setError(apiMsg(err, `Created "${created.name}", but couldn't link it. Choose it from the list to link it.`));
    } finally {
      setBusy(null);
    }
  };

  const handleLink = async (sf: SimpleFinAccount, value: string) => {
    if (value === 'new') { startNewAccount(sf); return; }
    const accountId = value ? Number(value) : null;
    if (accountId === (sf.linkedAccountId ?? null)) return;
    if (!confirmLinkChange(sf, accountId == null ? null : accounts.find(a => a.id === accountId) ?? null)) return;
    setBusy(sf.id);
    setError('');
    try {
      setStatus(await linkSimpleFinAccount(sf.id, accountId));
    } catch (err) {
      setError(apiMsg(err, 'Failed to update the account link.'));
    } finally {
      setBusy(null);
    }
  };

  const handleMode = async (sf: SimpleFinAccount, balanceOnly: boolean) => {
    if (balanceOnly === sf.balanceOnly) return;
    if (sf.pendingDraftId != null && !confirm(
      `Switch "${sf.name}" to ${balanceOnly ? 'balance only' : 'transactions'}? `
      + `Its ${sf.pendingDraftRows ?? ''} unreviewed transaction(s) will be discarded.`,
    )) return;
    setBusy(sf.id);
    setError('');
    try {
      setStatus(await setSimpleFinBalanceOnly(sf.id, balanceOnly));
    } catch (err) {
      setError(apiMsg(err, 'Failed to change how this account syncs.'));
    } finally {
      setBusy(null);
    }
  };


  const handleDisconnect = async () => {
    if (!confirm(
      'Disconnect SimpleFIN? Money Tracker will delete its stored access key and stop syncing. '
      + 'Unreviewed synced transactions are discarded; anything already imported stays in your registers.\n\n'
      + 'To fully revoke access, also remove the Money Tracker connection in SimpleFIN Bridge.',
    )) return;
    setBusy('disconnect');
    setError('');
    try {
      await disconnectSimpleFin();
      setStatus({ connected: false });
    } catch (err) {
      setError(apiMsg(err, 'Failed to disconnect.'));
    } finally {
      setBusy(null);
    }
  };

  const review = (draftId: number) =>
    navigate('/settings', { state: { tab: 'import', resumeDraftId: draftId } });

  if (!status) {
    return (
      <div className={styles.tabSection}>
        {error ? <div className={styles.error}>{error}</div> : <p className={styles.hint}>Loading…</p>}
      </div>
    );
  }

  if (!status.connected) {
    return (
      <div className={styles.tabSection}>
        <p className={styles.hint}>
          Pull new transactions from your banks automatically through{' '}
          <a href="https://bridge.simplefin.org" target="_blank" rel="noopener noreferrer">SimpleFIN Bridge</a>,
          a read-only bank-data service (a paid subscription on their side). You link your banks on
          SimpleFIN's site — Money Tracker never sees your bank username or password, and SimpleFIN
          can't move money. Synced transactions wait for your review before anything is imported.
        </p>

        <div className={styles.prefGroup}>
          <p className={styles.prefGroupTitle}>Connect</p>
          <ol className={styles.hint} style={{ paddingLeft: 18, margin: '0 0 10px' }}>
            <li>In SimpleFIN Bridge, link your banks, then create a new <strong>Setup Token</strong> for an app.</li>
            <li>Paste it below. It can only be used once.</li>
          </ol>
          <textarea
            className={styles.input}
            value={token}
            onChange={e => setToken(e.target.value)}
            placeholder="Paste the setup token"
            rows={3}
            spellCheck={false}
            autoComplete="off"
            style={{ width: '100%', fontFamily: 'monospace', fontSize: 13 }}
          />
          {error && <div className={styles.error}>{error}</div>}
          <div style={{ marginTop: 10 }}>
            <button className={styles.btnPrimary} onClick={handleConnect} disabled={busy === 'connect' || !token.trim()}>
              {busy === 'connect' ? 'Connecting…' : 'Connect'}
            </button>
          </div>
        </div>
      </div>
    );
  }

  // A local account can receive at most one bank feed — offer only accounts
  // not already taken by another SimpleFIN account.
  const takenBy = new Map<number, number>();
  for (const a of status.accounts) if (a.linkedAccountId != null) takenBy.set(a.linkedAccountId, a.id);
  const hasLinks = status.accounts.some(a => a.linkedAccountId != null);

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Connected to SimpleFIN since {formatDateTime(status.createdAt, user?.timeZoneId)}
        {status.lastSyncAt ? <> · last synced {formatDateTime(status.lastSyncAt, user?.timeZoneId)}</> : <> · not synced yet</>}.
        Choose which Money Tracker account each bank account syncs into, then sync from the{' '}
        <Link to="/bank-sync">Bank Sync</Link> screen. Each sync fetches posted transactions since the last
        one you imported (re-checking the previous two weeks, since banks often post late), skips any already in
        the register, and stages the rest for review. Pending transactions are left until they post.
      </p>

      {status.lastErrors.length > 0 && (
        <div className={styles.error}>
          <strong>SimpleFIN reported:</strong>
          <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
            {status.lastErrors.map((e, i) => <li key={i}>{e}</li>)}
          </ul>
        </div>
      )}
      {error && <div className={styles.error}>{error}</div>}

      <div style={{ overflowX: 'auto' }}>
      <table className={styles.dupTable}>
        <thead>
          <tr>
            <th>Bank account</th>
            <th style={{ textAlign: 'right' }}>Bank balance</th>
            <th>Sync into</th>
            <th>Records</th>
            <th>Synced through</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {status.accounts.map(sf => (
            <tr key={sf.id}>
              <td>
                {sf.name}
                {sf.orgName && <div className={styles.hint} style={{ margin: 0 }}>{sf.orgName}</div>}
              </td>
              <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{formatMoney(sf.balance, sf.currency)}</td>
              <td>
                {/* Once linked, the link is fixed: to send this bank account
                    somewhere else, unlink it first, then link it again. */}
                {sf.linkedAccountId != null ? (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                    <span><span aria-hidden="true">🔗</span> {sf.linkedAccountName}</span>
                    <button
                      type="button"
                      className={styles.btnLink}
                      style={{ color: 'var(--c-cc0000)' }}
                      onClick={() => handleLink(sf, '')}
                      disabled={busy !== null}
                      aria-label={`Unlink ${sf.name} from ${sf.linkedAccountName}`}
                    >
                      Unlink
                    </button>
                  </span>
                ) : newAccount?.sfId === sf.id ? (
                  <form
                    onSubmit={e => { e.preventDefault(); handleCreateAndLink(sf); }}
                    style={{ display: 'flex', flexDirection: 'column', gap: 6, minWidth: 220 }}
                  >
                    <input
                      className={styles.input}
                      value={newAccount.name}
                      onChange={e => setNewAccount({ ...newAccount, name: e.target.value })}
                      aria-label="New account name"
                      placeholder="Account name"
                      autoFocus
                    />
                    <div style={{ display: 'flex', gap: 6, alignItems: 'flex-end' }}>
                      <label style={{ display: 'flex', flexDirection: 'column', fontSize: 12, gap: 2 }}>
                        Type
                        <select
                          value={newAccount.type}
                          onChange={e => setNewAccount({ ...newAccount, type: e.target.value as AccountType })}
                          aria-label="New account type"
                        >
                          {ACCOUNT_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
                        </select>
                      </label>
                      <label style={{ display: 'flex', flexDirection: 'column', fontSize: 12, gap: 2 }}>
                        Opening balance
                        <input
                          className={styles.input}
                          type="number"
                          step="0.01"
                          value={newAccount.openingBalance}
                          onChange={e => setNewAccount({ ...newAccount, openingBalance: e.target.value })}
                          aria-label="Opening balance"
                          title="The balance before the first transaction that will be synced"
                          style={{ width: 120 }}
                        />
                      </label>
                    </div>
                    <div style={{ display: 'flex', gap: 8 }}>
                      <button type="submit" className={styles.btnPrimary} disabled={busy !== null}>
                        {busy === sf.id ? 'Creating…' : 'Create & Link'}
                      </button>
                      <button type="button" className={styles.btnSecondary} onClick={() => { setNewAccount(null); setError(''); }} disabled={busy !== null}>
                        Cancel
                      </button>
                    </div>
                  </form>
                ) : (
                  <select
                    value=""
                    onChange={e => handleLink(sf, e.target.value)}
                    disabled={busy !== null || newAccount !== null}
                    aria-label={`Account to sync ${sf.name} into`}
                  >
                    <option value="">— Don't sync —</option>
                    <option value="new">+ New account…</option>
                    {accounts
                      .filter(a => a.isActive)
                      .sort((a, b) => a.name.localeCompare(b.name))
                      .map(a => {
                        const taken = takenBy.has(a.id);
                        return (
                          <option key={a.id} value={a.id} disabled={taken}>
                            {a.name}{taken ? ' (already linked)' : ''}
                          </option>
                        );
                      })}
                  </select>
                )}
              </td>
              <td>
                {sf.linkedAccountId != null && (
                  <select
                    value={sf.balanceOnly ? 'balance' : 'transactions'}
                    onChange={e => handleMode(sf, e.target.value === 'balance')}
                    disabled={busy !== null}
                    aria-label={`What to record for ${sf.name}`}
                  >
                    <option value="transactions">Transactions</option>
                    <option value="balance">Balance only</option>
                  </select>
                )}
              </td>
              <td>
                {sf.linkedAccountId == null ? '—'
                  : sf.balanceOnly
                    ? (sf.valueDate ? <>{formatMoney(sf.valueRecorded, sf.currency)} on {formatDay(sf.valueDate)}</> : 'No value yet')
                    : formatDay(sf.syncedThrough)}
              </td>
              <td>
                {sf.pendingDraftId != null && (
                  <button className={styles.btnSecondary} onClick={() => review(sf.pendingDraftId!)}>
                    Review {sf.pendingDraftRows} new
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>

      <p className={styles.hint}>
        <strong>Balance only</strong> suits investment and retirement accounts: no transactions are imported,
        and the value your bank reports becomes the account's balance, with a history kept day by day.
        Linking an Investment account picks it automatically.
      </p>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Nightly sync</p>
        <p className={styles.hint} style={{ marginBottom: 8 }}>
          Syncs every linked account once a day without you pressing Sync. New transactions wait on the Bank Sync
          screen for your approval; if you don't get to them, the next nights add to the same review (keeping the
          choices you've made) rather than starting over. Balance-only accounts get the day's value recorded. A
          review you've worked on in the last hour is left alone until the next night. If the server is down at 8 pm,
          it catches up before midnight.
        </p>
        <p style={{ margin: 0, fontSize: 14 }}>
          <strong>Every day after 8 pm Pacific</strong>
          {status.lastAutoUpdateDate && (
            <span className={styles.hint} style={{ marginLeft: 10 }}>Last ran {formatDay(status.lastAutoUpdateDate)}.</span>
          )}
        </p>
      </div>

      <div style={{ display: 'flex', gap: 10, marginTop: 14, alignItems: 'center' }}>
        <button className={styles.btnPrimary} onClick={() => navigate('/bank-sync')} disabled={busy !== null || !hasLinks}>
          Go to Bank Sync
        </button>
        {!hasLinks && <span className={styles.hint} style={{ margin: 0 }}>Choose where at least one bank account should sync into.</span>}
        <span style={{ flex: 1 }} />
        <button className={styles.btnDanger} onClick={handleDisconnect} disabled={busy !== null}>
          {busy === 'disconnect' ? 'Disconnecting…' : 'Disconnect'}
        </button>
      </div>
    </div>
  );
}
