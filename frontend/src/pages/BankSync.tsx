import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import {
  getSimpleFinStatus, syncSimpleFin, restoreSkippedTransaction,
  type SimpleFinStatus, type SimpleFinAccount, type SimpleFinSyncResult,
} from '../api/simplefin';
import { useAuth } from '../contexts/auth-context';
import { formatDateTime } from '../utils/timezone';
// Same styling as Settings → Bank Sync, which this screen sits alongside.
import styles from './Settings.module.css';

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

const isLinked = (a: SimpleFinAccount) => a.linkedAccountId != null;

/**
 * The Sync screen: every bank and its accounts, all ticked by default. Untick
 * any to leave them out of this one sync — their links and sync progress are
 * untouched, and they're ticked again next time the screen opens.
 */
export default function BankSync() {
  usePageTitle('Bank Sync');
  const navigate = useNavigate();
  const { user } = useAuth();
  const [status, setStatus] = useState<SimpleFinStatus | null>(null);
  // Unticked for this visit only; everything else syncs.
  const [unticked, setUnticked] = useState<Set<number>>(new Set());
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [busy, setBusy] = useState<'sync' | number | null>(null);
  const [error, setError] = useState('');
  const [syncResult, setSyncResult] = useState<SimpleFinSyncResult | null>(null);

  useEffect(() => {
    getSimpleFinStatus()
      .then(setStatus)
      .catch(() => setError('Failed to load bank sync status.'));
  }, []);

  const review = (draftId: number) =>
    navigate('/settings', { state: { tab: 'import', resumeDraftId: draftId } });

  if (!status) {
    return (
      <div className={styles.page}>
        <div className={styles.pageHeader}><h2 className={styles.pageTitle}>Bank Sync</h2></div>
        {error ? <div className={styles.error}>{error}</div> : <p className={styles.hint}>Loading…</p>}
      </div>
    );
  }

  if (!status.connected) {
    return (
      <div className={styles.page}>
        <div className={styles.pageHeader}><h2 className={styles.pageTitle}>Bank Sync</h2></div>
        <p className={styles.hint}>
          Bank sync isn't set up yet. Connect SimpleFIN Bridge in{' '}
          <Link to="/settings" state={{ tab: 'banksync' }}>Settings → Bank Sync</Link> first.
        </p>
      </div>
    );
  }

  // Group by bank, keeping the server's bank-then-account order.
  const banks: { org: string; accounts: SimpleFinAccount[] }[] = [];
  for (const a of status.accounts) {
    const org = a.orgName || 'Other';
    const group = banks.find(b => b.org === org);
    if (group) group.accounts.push(a); else banks.push({ org, accounts: [a] });
  }

  const linked = status.accounts.filter(isLinked);
  const selected = linked.filter(a => !unticked.has(a.id));
  const allSelected = selected.length === linked.length;

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
    setBusy('sync');
    setError('');
    setSyncResult(null);
    try {
      setSyncResult(await syncSimpleFin(selected.map(a => a.id)));
      setStatus(await getSimpleFinStatus());
    } catch (err) {
      setError(apiMsg(err, 'Sync failed.'));
      getSimpleFinStatus().then(setStatus).catch(() => {});
    } finally {
      setBusy(null);
    }
  };

  const handleRestore = async (skippedId: number) => {
    setBusy(skippedId);
    setError('');
    try {
      setStatus(await restoreSkippedTransaction(skippedId));
    } catch (err) {
      setError(apiMsg(err, 'Failed to restore that transaction.'));
    } finally {
      setBusy(null);
    }
  };

  const checkbox = (checked: boolean, indeterminate: boolean, onChange: () => void, label: string, disabled = false) => (
    <input
      type="checkbox"
      aria-label={label}
      checked={checked}
      disabled={disabled || busy !== null}
      ref={el => { if (el) el.indeterminate = indeterminate; }}
      onChange={onChange}
    />
  );

  return (
    <div className={styles.page} style={{ maxWidth: 820 }}>
      <div className={styles.pageHeader}><h2 className={styles.pageTitle}>Bank Sync</h2></div>

      <div className={styles.tabSection}>
        <p className={styles.hint}>
          {status.lastSyncAt
            ? <>Last synced {formatDateTime(status.lastSyncAt, user?.timeZoneId)}. </>
            : <>Not synced yet. </>}
          Untick any account to leave it out of this sync; it picks up where it left off next time.
          New transactions wait for your review before anything is imported.
          To change which account a bank account feeds, use{' '}
          <Link to="/settings" state={{ tab: 'banksync' }}>Settings → Bank Sync</Link>.
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
                    () => setTicked(linked.map(a => a.id), !allSelected), 'Select all accounts', linked.length === 0)}
                </th>
                <th>Bank account</th>
                <th>Syncs into</th>
                <th style={{ textAlign: 'right' }}>Bank balance</th>
                <th>Imported through / value</th>
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
                        () => setTicked(bankLinked.map(a => a.id), !bankAll), `Select all ${bank.org} accounts`,
                        bankLinked.length === 0)}
                    </th>
                    <th colSpan={5}>{bank.org}</th>
                  </tr>
                  {bank.accounts.map(sf => {
                    const linkedHere = isLinked(sf);
                    const open = expanded.has(sf.id);
                    return [
                      <tr key={sf.id} style={linkedHere ? undefined : { opacity: 0.55 }}>
                        <td>
                          {checkbox(linkedHere && !unticked.has(sf.id), false,
                            () => setTicked([sf.id], unticked.has(sf.id)), `Sync ${sf.name}`, !linkedHere)}
                        </td>
                        <td>{sf.name}</td>
                        <td>
                          {linkedHere
                            ? sf.linkedAccountName
                            : <Link to="/settings" state={{ tab: 'banksync' }}>Not linked</Link>}
                        </td>
                        <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{formatMoney(sf.balance, sf.currency)}</td>
                        <td style={{ whiteSpace: 'nowrap' }}>
                          {!linkedHere ? '—'
                            : sf.balanceOnly
                              ? <>Balance only{sf.valueDate && <>: {formatMoney(sf.valueRecorded, sf.currency)} on {formatDay(sf.valueDate)}</>}</>
                              : formatDay(sf.syncedThrough)}
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
                          <td colSpan={5}>
                            <p className={styles.hint} style={{ marginBottom: 6 }}>
                              You chose not to import these, so syncs leave them out. Restore one to have the next
                              sync of this account offer it again.
                            </p>
                            <table className={styles.dupTable}>
                              <tbody>
                                {sf.skipped.map(s => (
                                  <tr key={s.id}>
                                    <td style={{ whiteSpace: 'nowrap' }}>{formatDay(s.date)}</td>
                                    <td>{s.payee || '—'}</td>
                                    <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{formatMoney(s.amount, sf.currency)}</td>
                                    <td>
                                      <button className={styles.btnLink} disabled={busy !== null} onClick={() => handleRestore(s.id)}>
                                        {busy === s.id ? 'Restoring…' : 'Restore'}
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

        <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <button className={styles.btnPrimary} onClick={handleSync} disabled={busy !== null || selected.length === 0}>
            {busy === 'sync'
              ? 'Syncing…'
              : allSelected ? 'Sync all' : `Sync ${selected.length} account${selected.length === 1 ? '' : 's'}`}
          </button>
          {linked.length === 0 && (
            <span className={styles.hint}>
              No bank account is linked yet. Choose where each one syncs in{' '}
              <Link to="/settings" state={{ tab: 'banksync' }}>Settings → Bank Sync</Link>.
            </span>
          )}
          {linked.length > 0 && selected.length === 0 && (
            <span className={styles.hint}>Tick at least one account.</span>
          )}
        </div>
        <p className={styles.hint}>
          SimpleFIN refreshes bank data about once a day and limits how often apps can ask for it, so
          syncing more than a few times a day won't find anything new.
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
      </div>
    </div>
  );
}
