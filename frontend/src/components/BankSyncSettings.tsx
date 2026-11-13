import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  connectSimpleFin, disconnectSimpleFin, linkSimpleFinAccount,
  setSimpleFinBalanceOnly, getSimpleFinStatus, syncSimpleFin, restoreSkippedTransaction,
  type SimpleFinStatus, type SimpleFinAccount, type SimpleFinSyncResult,
} from '../api/simplefin';
import { useAuth } from '../contexts/auth-context';
import { formatDateTime } from '../utils/timezone';
import type { Account } from '../types';
import NewLinkedAccountModal from './NewLinkedAccountModal';
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

function formatDay(d?: string | null) {
  if (!d) return '—';
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

/**
 * Bank sync, on the Accounts page (/accounts#bank-sync; /bank-sync redirects
 * here): connecting SimpleFIN Bridge, choosing which account each bank account
 * feeds, and syncing — all linked accounts are ticked; untick any to leave them
 * out of one sync. A sync stages new transactions as import drafts: nothing
 * reaches a register until it's reviewed and committed. Also shows the manual
 * sync limits, the last sync's results, and transactions skipped on earlier
 * reviews (which can be restored).
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
  // Sync selection: unticked for this visit only; every other linked account syncs.
  const [unticked, setUnticked] = useState<Set<number>>(new Set());
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [syncing, setSyncing] = useState(false);
  const [restoringId, setRestoringId] = useState<number | null>(null);
  const [syncResult, setSyncResult] = useState<SimpleFinSyncResult | null>(null);
  // Ticks so the "next sync available" wait counts down on its own.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 20_000);
    return () => clearInterval(t);
  }, []);
  // The bank account whose "+ New account…" popup is open.
  const [newAccountFor, setNewAccountFor] = useState<SimpleFinAccount | null>(null);

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

  // The popup created the account; link the bank account to it.
  const linkNewAccount = async (sf: SimpleFinAccount, created: Account) => {
    setNewAccountFor(null);
    onAccountCreated();
    setBusy(sf.id);
    setError('');
    try {
      setStatus(await linkSimpleFinAccount(sf.id, created.id));
    } catch (err) {
      setError(apiMsg(err, `Created "${created.name}", but couldn't link it. Choose it from the list to link it.`));
    } finally {
      setBusy(null);
    }
  };

  const handleLink = async (sf: SimpleFinAccount, value: string) => {
    if (value === 'new') { setError(''); setNewAccountFor(sf); return; }
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
    navigate('/settings?tab=import', { state: { resumeDraftId: draftId } });

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
  const isLinked = (a: SimpleFinAccount) => a.linkedAccountId != null;
  const linked = status.accounts.filter(isLinked);

  // Group by bank, keeping the server's bank-then-account order.
  const banks: { org: string; accounts: SimpleFinAccount[] }[] = [];
  for (const a of status.accounts) {
    const org = a.orgName || 'Other';
    const group = banks.find(b => b.org === org);
    if (group) group.accounts.push(a); else banks.push({ org, accounts: [a] });
  }

  // Manual syncs are held back briefly after one another, and capped per day.
  const heldUntil = status.nextManualSyncAt ? new Date(status.nextManualSyncAt).getTime() : 0;
  const held = heldUntil > now;
  const atDailyCap = status.requestsToday >= status.dailyRequestLimit;
  const waitMinutes = Math.max(1, Math.ceil((heldUntil - now) / 60_000));
  const selected = linked.filter(a => !unticked.has(a.id));
  const allSelected = selected.length === linked.length;
  const working = busy !== null || syncing || restoringId !== null;

  const setTicked = (ids: number[], ticked: boolean) => {
    setUnticked(prev => {
      const next = new Set(prev);
      for (const id of ids) { if (ticked) next.delete(id); else next.add(id); }
      return next;
    });
  };

  const toggleExpanded = (id: number) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  const handleSync = async () => {
    setSyncing(true);
    setError('');
    setSyncResult(null);
    try {
      setSyncResult(await syncSimpleFin(selected.map(a => a.id)));
      setStatus(await getSimpleFinStatus());
    } catch (err) {
      setError(apiMsg(err, 'Sync failed.'));
      getSimpleFinStatus().then(setStatus).catch(() => {});
    } finally {
      setSyncing(false);
    }
  };

  const handleRestore = async (skippedId: number) => {
    setRestoringId(skippedId);
    setError('');
    try {
      setStatus(await restoreSkippedTransaction(skippedId));
    } catch (err) {
      setError(apiMsg(err, 'Failed to restore that transaction.'));
    } finally {
      setRestoringId(null);
    }
  };

  const checkbox = (checked: boolean, indeterminate: boolean, onChange: () => void, label: string, disabled = false) => (
    <input
      type="checkbox"
      aria-label={label}
      checked={checked}
      disabled={disabled || working}
      ref={el => { if (el) el.indeterminate = indeterminate; }}
      onChange={onChange}
    />
  );

  const hourLabel = (h: number) => (h === 0 ? '12 am' : h < 12 ? `${h} am` : h === 12 ? '12 pm' : `${h - 12} pm`);

  return (
    <div className={styles.tabSection}>
      {newAccountFor && (
        <NewLinkedAccountModal
          bankAccount={newAccountFor}
          existingNames={accounts.map(a => a.name)}
          onCreated={created => linkNewAccount(newAccountFor, created)}
          onClose={() => setNewAccountFor(null)}
        />
      )}
      <p className={styles.hint}>
        Connected to SimpleFIN since {formatDateTime(status.createdAt, user?.timeZoneId)}
        {status.lastSyncAt ? <> · last synced {formatDateTime(status.lastSyncAt, user?.timeZoneId)}</> : <> · not synced yet</>}.
        Choose which Money Tracker account each bank account syncs into, then press Sync (untick any account to leave
        it out of one sync; it picks up where it left off next time). Each sync fetches posted transactions since the
        last one you imported (re-checking the previous two weeks, since banks often post late), skips any already in
        the register, and stages the rest for your review — nothing is imported until you approve it. Pending
        transactions are left until they post.
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
            <th style={{ width: 28 }}>
              {checkbox(allSelected && linked.length > 0, selected.length > 0 && !allSelected,
                () => setTicked(linked.map(a => a.id), !allSelected), 'Select all accounts to sync', linked.length === 0)}
            </th>
            <th>Bank account</th>
            <th style={{ textAlign: 'right' }}>Bank balance</th>
            <th>Sync into</th>
            <th>Records</th>
            <th>Last synced</th>
            <th></th>
          </tr>
        </thead>
        {banks.map(bank => {
          const bankLinked = bank.accounts.filter(isLinked);
          const bankSelected = bankLinked.filter(a => !unticked.has(a.id));
          const bankAll = bankLinked.length > 0 && bankSelected.length === bankLinked.length;
          return (
            <tbody key={bank.org}>
              <tr>
                <th>
                  {checkbox(bankAll, bankSelected.length > 0 && !bankAll,
                    () => setTicked(bankLinked.map(a => a.id), !bankAll), `Select all ${bank.org} accounts to sync`,
                    bankLinked.length === 0)}
                </th>
                <th colSpan={6}>{bank.org}</th>
              </tr>
              {bank.accounts.map(sf => {
                const open = expanded.has(sf.id);
                return [
                  <tr key={sf.id}>
                    <td>
                      {checkbox(isLinked(sf) && !unticked.has(sf.id), false,
                        () => setTicked([sf.id], unticked.has(sf.id)), `Sync ${sf.name}`, !isLinked(sf))}
                    </td>
                    <td>{sf.name}</td>
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
                            disabled={working}
                            aria-label={`Unlink ${sf.name} from ${sf.linkedAccountName}`}
                          >
                            Unlink
                          </button>
                        </span>
                      ) : (
                        <select
                          value=""
                          onChange={e => handleLink(sf, e.target.value)}
                          disabled={working}
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
                          disabled={working}
                          aria-label={`What to record for ${sf.name}`}
                        >
                          <option value="transactions">Transactions</option>
                          <option value="balance">Balance only</option>
                        </select>
                      )}
                    </td>
                    <td>
                      {sf.linkedAccountId == null ? '—' : (
                        <>
                          {sf.lastSyncedAt ? formatDateTime(sf.lastSyncedAt, user?.timeZoneId) : 'Not yet'}
                          {sf.balanceOnly && (
                            <div className={styles.hint} style={{ margin: 0 }}>
                              {sf.valueDate ? <>{formatMoney(sf.valueRecorded, sf.currency)} on {formatDay(sf.valueDate)}</> : 'No value yet'}
                            </div>
                          )}
                        </>
                      )}
                    </td>
                    <td style={{ whiteSpace: 'nowrap' }}>
                      {sf.pendingDraftId != null && (
                        <button className={styles.btnSecondary} onClick={() => review(sf.pendingDraftId!)}>
                          Review {sf.pendingDraftRows} new
                        </button>
                      )}
                      {sf.skipped.length > 0 && (
                        <button className={styles.btnLink} style={{ marginLeft: 8 }} onClick={() => toggleExpanded(sf.id)}>
                          {open ? 'Hide' : 'Show'} {sf.skipped.length} skipped
                        </button>
                      )}
                    </td>
                  </tr>,
                  open && sf.skipped.length > 0 && (
                    <tr key={`${sf.id}-skipped`}>
                      <td></td>
                      <td colSpan={6}>
                        <p className={styles.hint} style={{ marginBottom: 6 }}>
                          You chose not to import these, so syncs leave them out. Restore one to have the next
                          sync of this account offer it again.
                        </p>
                        <table className={styles.dupTable}>
                          <tbody>
                            {sf.skipped.map(sk => (
                              <tr key={sk.id}>
                                <td style={{ whiteSpace: 'nowrap' }}>{formatDay(sk.date)}</td>
                                <td>{sk.payee || '—'}</td>
                                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{formatMoney(sk.amount, sf.currency)}</td>
                                <td>
                                  <button className={styles.btnLink} disabled={working} onClick={() => handleRestore(sk.id)}>
                                    {restoringId === sk.id ? 'Restoring…' : 'Restore'}
                                  </button>
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </td>
                    </tr>
                  ),
                ];
              })}
            </tbody>
          );
        })}
      </table>
      </div>

      <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginTop: 10 }}>
        <button className={styles.btnPrimary} onClick={handleSync} disabled={working || selected.length === 0 || held}>
          {syncing
            ? 'Syncing…'
            : allSelected ? 'Sync all' : `Sync ${selected.length} account${selected.length === 1 ? '' : 's'}`}
        </button>
        {linked.length === 0 && (
          <span className={styles.hint} style={{ margin: 0 }}>Choose where at least one bank account should sync into.</span>
        )}
        {linked.length > 0 && selected.length === 0 && (
          <span className={styles.hint} style={{ margin: 0 }}>Tick at least one account.</span>
        )}
        {held && (
          <span className={styles.hint} style={{ margin: 0 }}>
            {atDailyCap
              ? <>Today's {status.dailyRequestLimit} syncs are used up; more are available after midnight Pacific. The nightly sync still runs.</>
              : <>Synced just now. You can sync again in {waitMinutes} minute{waitMinutes === 1 ? '' : 's'}.</>}
          </span>
        )}
      </div>
      <p className={styles.hint}>
        SimpleFIN refreshes bank data about once a day and limits how often apps can ask for it, so manual syncs
        wait 15 minutes between them and are capped at {status.dailyRequestLimit} a day ({status.requestsToday} used today).
      </p>

      {syncResult && (
        <div className={styles.prefGroup}>
          <p className={styles.prefGroupTitle}>Sync results</p>
          {syncResult.accounts.map(r => (
            <div key={r.simpleFinAccountId} style={{ marginBottom: 6 }}>
              <strong>{r.name}</strong> → {r.linkedAccountName}:{' '}
              {r.skipped ? 'not synced' : r.valueRecorded != null ? (
                <>balance recorded: {formatMoney(r.valueRecorded)}{r.valueDate && <> for {formatDay(r.valueDate)}</>}</>
              ) : r.newTransactions > 0 ? (
                <>
                  {r.newTransactions} new transaction{r.newTransactions === 1 ? '' : 's'} to review{' '}
                  {r.draftId != null && (
                    <button className={styles.btnLink} onClick={() => review(r.draftId!)}>Review</button>
                  )}
                </>
              ) : 'no new transactions'}
              {r.note && <div className={styles.hint} style={{ margin: '2px 0 0' }}>{r.note}</div>}
            </div>
          ))}
        </div>
      )}

      <p className={styles.hint}>
        <strong>Balance only</strong> suits investment and retirement accounts: no transactions are imported,
        and the value your bank reports becomes the account's balance, with a history kept day by day.
        Linking an Investment account picks it automatically.
      </p>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Nightly sync</p>
        <p className={styles.hint} style={{ marginBottom: 8 }}>
          Syncs every linked account once a day without you pressing Sync. New transactions wait here for your
          approval (the Review buttons above); if you don't get to them, the next nights add to the same review
          (keeping the choices you've made) rather than starting over. Balance-only accounts get the day's value
          recorded. A review you've worked on in the last hour is left alone until the next night. If the server is
          down at the scheduled time, it catches up before midnight.
        </p>
        <p style={{ margin: 0, fontSize: 14 }}>
          <strong>Every day after {hourLabel(status.dailyUpdateHour ?? 20)} Pacific</strong>
          {status.lastAutoUpdateDate && (
            <span className={styles.hint} style={{ marginLeft: 10 }}>Last ran {formatDay(status.lastAutoUpdateDate)}.</span>
          )}
        </p>
      </div>

      <div style={{ display: 'flex', gap: 10, marginTop: 14, alignItems: 'center' }}>
        <span style={{ flex: 1 }} />
        <button className={styles.btnDanger} onClick={handleDisconnect} disabled={working}>
          {busy === 'disconnect' ? 'Disconnecting…' : 'Disconnect'}
        </button>
      </div>
    </div>
  );
}
