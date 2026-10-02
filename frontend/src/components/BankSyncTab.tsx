import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  getSimpleFinStatus, connectSimpleFin, disconnectSimpleFin, linkSimpleFinAccount,
  setSimpleFinBalanceOnly, setSimpleFinDailyUpdate,
  type SimpleFinStatus, type SimpleFinAccount,
} from '../api/simplefin';
import { getAccounts } from '../api/accounts';
import { useAuth } from '../contexts/auth-context';
import { formatDateTime } from '../utils/timezone';
import type { Account } from '../types';
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

function formatDay(d?: string | null) {
  if (!d) return '—';
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

/**
 * Settings → Bank Sync: connecting SimpleFIN Bridge and choosing which account
 * each bank account feeds. Syncing itself happens on the Bank Sync screen
 * (pages/BankSync.tsx), which stages new transactions as import drafts —
 * nothing reaches a register until it's reviewed and committed.
 */
export default function BankSyncTab() {
  const navigate = useNavigate();
  const { user } = useAuth();
  const [status, setStatus] = useState<SimpleFinStatus | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [token, setToken] = useState('');
  const [busy, setBusy] = useState<'connect' | 'disconnect' | number | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    Promise.all([getSimpleFinStatus(), getAccounts()])
      .then(([s, accs]) => { setStatus(s); setAccounts(accs); })
      .catch(() => setError('Failed to load bank sync status.'));
  }, []);

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

  const handleLink = async (sf: SimpleFinAccount, value: string) => {
    const accountId = value ? Number(value) : null;
    if (accountId === (sf.linkedAccountId ?? null)) return;
    // Re-pointing (or unlinking) resets this bank account's sync progress and
    // discards any staged-but-unreviewed transactions for it.
    if (sf.linkedAccountId != null || sf.pendingDraftId != null) {
      const msg = accountId == null
        ? `Stop syncing "${sf.name}"?`
        : `Sync "${sf.name}" into a different account?`;
      const detail = sf.pendingDraftId != null
        ? ` Its ${sf.pendingDraftRows ?? ''} unreviewed transaction(s) will be discarded, and the next sync starts fresh for the new account.`
        : ' The next sync starts fresh for the new account.';
      if (!confirm(msg + detail)) return;
    }
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

  const handleDailyHour = async (value: string) => {
    setError('');
    try {
      setStatus(await setSimpleFinDailyUpdate(value === '' ? null : Number(value)));
    } catch (err) {
      setError(apiMsg(err, 'Failed to save the daily update time.'));
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
            style={{ width: '100%', fontFamily: 'monospace', fontSize: 12 }}
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
                <select
                  value={sf.linkedAccountId ?? ''}
                  onChange={e => handleLink(sf, e.target.value)}
                  disabled={busy !== null}
                >
                  <option value="">— Don't sync —</option>
                  {accounts
                    .filter(a => a.isActive || a.id === sf.linkedAccountId)
                    .sort((a, b) => a.name.localeCompare(b.name))
                    .map(a => {
                      const taken = takenBy.has(a.id) && takenBy.get(a.id) !== sf.id;
                      return (
                        <option key={a.id} value={a.id} disabled={taken}>
                          {a.name}{taken ? ' (already linked)' : ''}
                        </option>
                      );
                    })}
                </select>
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

      <p className={styles.hint}>
        <strong>Balance only</strong> suits investment and retirement accounts: no transactions are imported,
        and the value your bank reports becomes the account's balance, with a history kept day by day.
        Linking an Investment account picks it automatically.
      </p>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Daily balance update</p>
        <p className={styles.hint} style={{ marginBottom: 8 }}>
          Records the value of every balance-only account once a day, without you having to sync, so its
          history has no gaps. Runs in your time zone; if the server is down at that time, it catches up
          before midnight.
        </p>
        <select
          value={status.dailyUpdateHour ?? ''}
          onChange={e => handleDailyHour(e.target.value)}
          aria-label="Daily balance update time"
        >
          <option value="">Off</option>
          {Array.from({ length: 24 }, (_, h) => (
            <option key={h} value={h}>
              Every day after {h === 0 ? '12 am' : h < 12 ? `${h} am` : h === 12 ? '12 pm' : `${h - 12} pm`}
            </option>
          ))}
        </select>
        {status.lastAutoUpdateDate && (
          <span className={styles.hint} style={{ marginLeft: 10 }}>Last ran {formatDay(status.lastAutoUpdateDate)}.</span>
        )}
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
