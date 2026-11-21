import { Fragment, useEffect, useState } from 'react';
import {
  getBackups, saveBackupSettings, backUpNow, getBackupSummary, restoreBackup, downloadBackup,
  type BackupsOverview, type BackupInfo, type BackupSummary, type RestoreAccountChoice, type RestoreResult,
} from '../api/backups';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import ReauthModal from './ReauthModal';
import styles from '../pages/Settings.module.css';
import modal from './ExportModal.module.css';

type ApiError = { response?: { data?: { message?: string } | Blob } };
const msg = async (err: unknown, fallback: string) => {
  const data = (err as ApiError)?.response?.data;
  if (data instanceof Blob) { try { return (JSON.parse(await data.text()) as { message?: string }).message ?? fallback; } catch { return fallback; } }
  return data?.message ?? fallback;
};
const when = (iso: string) => new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });
const size = (b: number) => (b > 1024 * 1024 ? `${(b / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const KIND: Record<string, string> = { daily: 'Daily', manual: 'Manual', 'before-restore': 'Before a restore' };
const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });

/**
 * Settings → Backups & Restore: the daily encrypted backups, what they
 * include, "Back up now", restoring (everything or part) and downloading.
 */
export default function DailyBackups() {
  const [data, setData] = useState<BackupsOverview | null>(null);
  const [included, setIncluded] = useState<string[]>([]);
  const [busy, setBusy] = useState<'save' | 'backup' | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [restoring, setRestoring] = useState<BackupInfo | null>(null);
  const [downloading, setDownloading] = useState<BackupInfo | null>(null);
  const [open, setOpen] = useState<string | null>(null);   // backup whose manifest is shown

  const apply = (d: BackupsOverview) => { setData(d); setIncluded(d.sections.filter(s => s.included).map(s => s.id)); };
  const load = () => getBackups().then(apply).catch(() => setError('Failed to load your backups.'));
  useEffect(() => { void load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const dirty = !!data && data.sections.some(s => s.included !== included.includes(s.id));
  useUnsavedChanges(dirty && busy === null);

  if (!data) return <p className={styles.hint}>{error || 'Loading…'}</p>;

  const save = async () => {
    setBusy('save'); setError(''); setNotice('');
    try { apply(await saveBackupSettings(included)); setNotice('Saved.'); }
    catch (err) { setError(await msg(err, 'Failed to save.')); }
    finally { setBusy(null); }
  };
  const now = async () => {
    setBusy('backup'); setError(''); setNotice('');
    try { await backUpNow(); await load(); setNotice('Backup made and checked.'); }
    catch (err) { setError(await msg(err, 'The backup failed.')); }
    finally { setBusy(null); }
  };

  const all = data.sections.every(s => included.includes(s.id));
  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Money Tracker backs up your data automatically every day at {data.dailyAt} — just before the nightly bank sync — and
        keeps the newest {data.keep}. Backups are compressed 7z files encrypted with AES-256 and kept on the server. Each one
        is read back and checked when it's made. You can restore everything or just part of it, and download a copy.
      </p>
      {!data.available && <div className={styles.error}>Backups aren't available: 7-Zip isn't installed on the server.</div>}
      {data.lastError && <div className={styles.error}>The last backup didn't work: {data.lastError}</div>}

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>What's backed up</p>
        {data.sections.map(s => (
          <label key={s.id} style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '4px 0' }}>
            <input type="checkbox" checked={included.includes(s.id)}
              onChange={e => setIncluded(e.target.checked ? [...included, s.id] : included.filter(x => x !== s.id))} />
            {s.label}
          </label>
        ))}
        {!all && <p className={styles.hint} style={{ margin: '6px 0 0' }}>Unticked items won't be in new backups, so they can't be restored from them.</p>}
        <button className={styles.btnSecondary} onClick={() => void save()} disabled={!dirty || busy !== null} style={{ marginTop: 8 }}>
          {busy === 'save' ? 'Saving…' : 'Save'}
        </button>
      </div>

      {error && <div className={styles.error}>{error}</div>}
      {notice && <div className={styles.success}>{notice}</div>}

      <div className={styles.prefGroup}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
          <p className={styles.prefGroupTitle} style={{ margin: 0 }}>Your backups</p>
          <span style={{ flex: 1 }} />
          <button className={styles.btnPrimary} onClick={() => void now()} disabled={busy !== null || !data.available}>
            {busy === 'backup' ? 'Backing up…' : 'Back up now'}
          </button>
        </div>
        {data.backups.length === 0 ? (
          <p className={styles.hint}>No backups yet — the first one is made tonight, or press Back up now.</p>
        ) : (
          <table className={`${styles.dupTable} ${styles.stackOnPhone}`} style={{ marginTop: 10 }}>
            <thead><tr><th>When</th><th>Kind</th><th>Contains</th><th>Size</th><th></th></tr></thead>
            <tbody>
              {data.backups.map(b => (
                <Fragment key={b.fileName}>
                <tr>
                  <td data-label="When" style={{ whiteSpace: 'nowrap' }}>{when(b.createdAt)}</td>
                  <td data-label="Kind">{KIND[b.kind] ?? b.kind}</td>
                  <td data-label="Contains">
                    <span className={b.full ? styles.badgeFull : styles.badgePartial}
                      title={b.full ? 'Everything in your account' : `Only ${b.manifest.filter(m => m.included).map(m => m.label).join(', ')}`}>
                      {b.full ? 'Full' : 'Partial'}
                    </span>
                    {b.counts.transactions != null && <span className={styles.hint} style={{ margin: 0 }}> {b.counts.accounts} accounts, {b.counts.transactions} transactions</span>}
                    {' '}<button className={styles.btnLink} aria-expanded={open === b.fileName}
                      onClick={() => setOpen(open === b.fileName ? null : b.fileName)}>{open === b.fileName ? 'Hide' : 'Details'}</button>
                  </td>
                  <td data-label="Size">{size(b.sizeBytes)}</td>
                  <td style={{ whiteSpace: 'nowrap', textAlign: 'right' }}>
                    <button className={styles.btnSecondary} onClick={() => setRestoring(b)}>Restore…</button>{' '}
                    <button className={styles.btnSecondary} onClick={() => setDownloading(b)}>Download…</button>
                  </td>
                </tr>
                {open === b.fileName && (
                  <tr>
                    <td colSpan={5}>
                      <ul className={styles.manifest} aria-label="What's in this backup">
                        {b.manifest.map(m => (
                          <li key={m.id} className={m.included ? undefined : styles.manifestOff}>
                            <span aria-hidden="true">{m.included ? '✓' : '—'}</span> <strong>{m.label}</strong>
                            {m.included ? <> — {m.detail}</> : <> — not included</>}
                          </li>
                        ))}
                      </ul>
                    </td>
                  </tr>
                )}
                </Fragment>
              ))}
            </tbody>
          </table>
        )}
        <p className={styles.hint} style={{ margin: '8px 0 0' }}>
          Before any restore, everything is backed up automatically first (shown as "Before a restore"), so a restore can be undone.
        </p>
      </div>

      {restoring && <RestoreModal backup={restoring} onClose={() => setRestoring(null)} onDone={() => void load()} />}
      {downloading && <DownloadModal backup={downloading} onClose={() => setDownloading(null)} />}
    </div>
  );
}

type Mode = 'skip' | 'replace' | 'copy';

function RestoreModal({ backup, onClose, onDone }: { backup: BackupInfo; onClose: () => void; onDone: () => void }) {
  const [summary, setSummary] = useState<BackupSummary | null>(null);
  const [error, setError] = useState('');
  const [whole, setWhole] = useState(true);
  const [sections, setSections] = useState<string[]>([]);
  const [modes, setModes] = useState<Record<number, Mode>>({});
  const [removeOthers, setRemoveOthers] = useState(true);
  const [reauth, setReauth] = useState(false);
  const [working, setWorking] = useState(false);
  const [result, setResult] = useState<RestoreResult | null>(null);

  useEffect(() => {
    getBackupSummary(backup.fileName)
      .then(s => { setSummary(s); setSections(s.sections); setModes(Object.fromEntries(s.accounts.map(a => [a.id, 'replace' as Mode]))); })
      .catch(async err => setError(await msg(err, 'This backup couldn\'t be opened.')));
  }, [backup.fileName]);

  const LABEL: Record<string, string> = {
    accounts: 'Accounts & transactions', categories: 'Categories', payees: 'Payees & mapping rules', bills: 'Bills & reminders',
    ious: 'IOUs', reports: 'Saved reports', preferences: 'Settings & preferences',
  };

  const plan = () => {
    if (!summary) return null;
    const secs = whole ? summary.sections : sections;
    const accounts: RestoreAccountChoice[] = !secs.includes('accounts') ? []
      : summary.accounts.filter(a => (whole ? 'replace' : modes[a.id]) !== 'skip')
        .map(a => ({ backupAccountId: a.id, mode: (whole ? 'replace' : modes[a.id]) as 'replace' | 'copy' }));
    return { sections: secs, accounts, removeOtherAccounts: secs.includes('accounts') && (whole || removeOthers) && summary.notInBackup.length > 0 };
  };

  const start = async () => {
    const p = plan();
    if (!p || !summary) return;
    if (p.sections.length === 0) { setError('Choose what to restore.'); return; }
    if (p.sections.includes('accounts') && p.accounts.length === 0 && !p.removeOtherAccounts) { setError('Choose at least one account, or untick Accounts & transactions.'); return; }
    const replaced = p.accounts.filter(a => a.mode === 'replace').length, copied = p.accounts.length - replaced;
    const lines = [
      `Restore from the backup of ${when(summary.createdAt)}:`,
      ...p.sections.filter(s => s !== 'accounts').map(s => `• ${LABEL[s] ?? s}`),
      ...(replaced ? [`• ${replaced} account${replaced === 1 ? '' : 's'} put back exactly as ${replaced === 1 ? 'it was' : 'they were'} — changes since are lost`] : []),
      ...(copied ? [`• ${copied} account${copied === 1 ? '' : 's'} added as ${copied === 1 ? 'a copy' : 'copies'}`] : []),
      ...(p.removeOtherAccounts ? [`• Remove accounts not in the backup: ${summary.notInBackup.join(', ')}`] : []),
      '', 'Everything is backed up first, so you can undo this by restoring that backup.',
    ];
    if (!(await confirmDialog(lines.join('\n'), { title: 'Restore backup', confirmLabel: 'Restore', danger: true }))) return;
    setReauth(true);
  };

  const run = async (token: string) => {
    setReauth(false);
    const p = plan();
    if (!p) return;
    setWorking(true); setError('');
    try { setResult(await restoreBackup(backup.fileName, p, token)); onDone(); }
    catch (err) { setError(await msg(err, 'The restore failed. Nothing was changed.')); }
    finally { setWorking(false); }
  };

  return (
    <div className={modal.overlay} onClick={e => { if (e.target === e.currentTarget && !working) onClose(); }}>
      <div className={`${modal.modal} ${modal.modalWide}`} role="dialog" aria-labelledby="restore-title" style={{ width: 640, maxHeight: '90vh', overflowY: 'auto' }}>
        <div className={modal.modalHeader}>
          <h2 className={modal.modalTitle} id="restore-title">Restore Backup — {when(backup.createdAt)}</h2>
          <button className={modal.closeBtn} onClick={onClose} disabled={working} aria-label="Close">x</button>
        </div>
        <div className={modal.modalBody}>
          {!summary && !error && <p className={modal.authHint}>Opening the backup…</p>}
          {result ? (
            <>
              <p style={{ fontWeight: 700, color: 'var(--c-1a7a40)' }}>✓ Restored.</p>
              <ul style={{ paddingLeft: 20, margin: '0 0 10px' }}>{result.lines.map(l => <li key={l}>{l}</li>)}</ul>
              {result.warnings.length > 0 && <ul style={{ paddingLeft: 20, color: 'var(--c-b8860b)' }}>{result.warnings.map(w => <li key={w}>{w}</li>)}</ul>}
              <p className={modal.authHint}>Your data from just before this restore was saved as a "Before a restore" backup, if you need to undo it.</p>
              <button className={modal.btnPrimary} onClick={onClose}>Done</button>
            </>
          ) : summary && (
            <>
              <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', margin: '4px 0' }}>
                <input type="radio" name="rs" checked={whole} onChange={() => setWhole(true)} style={{ marginTop: 3 }} />
                <span><strong>Everything</strong><br /><span className={modal.authHint} style={{ margin: 0 }}>
                  Put your whole Money Tracker account back as it was{summary.notInBackup.length > 0 && <> (removes accounts added since: {summary.notInBackup.join(', ')})</>}.
                  Sign-in methods and bank sync connection are never changed.</span></span>
              </label>
              <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', margin: '8px 0' }}>
                <input type="radio" name="rs" checked={!whole} onChange={() => setWhole(false)} style={{ marginTop: 3 }} />
                <span><strong>Choose what to restore</strong></span>
              </label>
              {!whole && (
                <div style={{ marginLeft: 24 }}>
                  {summary.sections.map(s => (
                    <label key={s} style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '3px 0' }}>
                      <input type="checkbox" checked={sections.includes(s)}
                        onChange={e => setSections(e.target.checked ? [...sections, s] : sections.filter(x => x !== s))} />
                      {LABEL[s] ?? s}
                    </label>
                  ))}
                  {sections.includes('accounts') && (
                    <table className={styles.dupTable} style={{ margin: '8px 0' }}>
                      <thead><tr><th>Account in backup</th><th>Transactions</th><th>Restore</th></tr></thead>
                      <tbody>
                        {summary.accounts.map(a => (
                          <tr key={a.id}>
                            <td>{a.name}{!a.current && <span className={modal.authHint} style={{ margin: 0 }}> (deleted since)</span>}
                              <div className={modal.authHint} style={{ margin: 0 }}>{a.type} · balance {money(a.balance)}</div></td>
                            <td>{a.transactions}</td>
                            <td>
                              <select value={modes[a.id]} onChange={e => setModes({ ...modes, [a.id]: e.target.value as Mode })} aria-label={`How to restore ${a.name}`}>
                                <option value="replace">{a.current ? 'Replace current' : 'Bring it back'}</option>
                                <option value="copy">Add as a copy</option>
                                <option value="skip">Don't restore</option>
                              </select>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  {sections.includes('accounts') && summary.notInBackup.length > 0 && (
                    <label style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '4px 0' }}>
                      <input type="checkbox" checked={removeOthers} onChange={e => setRemoveOthers(e.target.checked)} />
                      Remove accounts not in this backup ({summary.notInBackup.join(', ')})
                    </label>
                  )}
                  <p className={modal.authHint}>
                    <strong>Replace current</strong> puts the account back exactly as it was, including transfers with your other accounts.{' '}
                    <strong>Add as a copy</strong> keeps the current account and adds the backup's as a new one. Categories and payees are
                    brought back by name; none are deleted.
                  </p>
                </div>
              )}
            </>
          )}
          {error && <div className={modal.error}>{error}</div>}
          {summary && !result && (
            <button className={modal.btnPrimary} onClick={() => void start()} disabled={working} style={{ marginTop: 8 }}>
              {working ? 'Restoring…' : 'Restore…'}
            </button>
          )}
        </div>
      </div>
      {reauth && <ReauthModal title="Confirm Identity" hint="Confirm your identity to restore this backup." onVerified={t => void run(t)} onClose={() => setReauth(false)} />}
    </div>
  );
}

function DownloadModal({ backup, onClose }: { backup: BackupInfo; onClose: () => void }) {
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [none, setNone] = useState(false);
  const [error, setError] = useState('');
  const [reauth, setReauth] = useState(false);
  const [working, setWorking] = useState(false);

  const start = () => {
    setError('');
    if (!none && pw.length < 8) { setError('Use a password of at least 8 characters.'); return; }
    if (!none && pw !== pw2) { setError('The passwords don\'t match.'); return; }
    setReauth(true);
  };
  const run = async (token: string) => {
    setReauth(false); setWorking(true);
    try {
      const blob = await downloadBackup(backup.fileName, none ? null : pw, token);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url; a.download = backup.fileName.replace(/^mt-/, 'money-tracker-'); a.click();
      URL.revokeObjectURL(url);
      onClose();
    } catch (err) { setError(await msg(err, 'The download failed.')); }
    finally { setWorking(false); }
  };

  return (
    <div className={modal.overlay} onClick={e => { if (e.target === e.currentTarget && !working) onClose(); }}>
      <div className={modal.modal} role="dialog" aria-labelledby="dl-title" style={{ width: 440 }}>
        <div className={modal.modalHeader}>
          <h2 className={modal.modalTitle} id="dl-title">Download Backup</h2>
          <button className={modal.closeBtn} onClick={onClose} aria-label="Close">x</button>
        </div>
        <form className={modal.modalBody} onSubmit={e => { e.preventDefault(); start(); }}>
          <p className={modal.authHint}>
            A copy of the backup from {when(backup.createdAt)} as a 7z file. Choose a password to protect it — you'll need it to open
            the file (e.g. with 7-Zip). It isn't stored anywhere.
          </p>
          {!none && (
            <>
              <div className={modal.field}>
                <label className={modal.label} htmlFor="dl-pw">Password</label>
                <input id="dl-pw" className={modal.input} type="password" value={pw} onChange={e => setPw(e.target.value)} autoComplete="new-password" autoFocus style={{ width: '100%', boxSizing: 'border-box' }} />
              </div>
              <div className={modal.field}>
                <label className={modal.label} htmlFor="dl-pw2">Password again</label>
                <input id="dl-pw2" className={modal.input} type="password" value={pw2} onChange={e => setPw2(e.target.value)} autoComplete="new-password" style={{ width: '100%', boxSizing: 'border-box' }} />
              </div>
            </>
          )}
          <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', margin: '6px 0 10px' }}>
            <input type="checkbox" checked={none} onChange={e => setNone(e.target.checked)} style={{ marginTop: 3 }} />
            <span>No password<br /><span className={modal.authHint} style={{ margin: 0 }}>The file won't be encrypted — anyone who gets it can read all your financial data.</span></span>
          </label>
          {error && <div className={modal.error}>{error}</div>}
          <button type="submit" className={modal.btnPrimary} disabled={working}>{working ? 'Preparing…' : 'Download'}</button>
        </form>
      </div>
      {reauth && <ReauthModal title="Confirm Identity" hint="Confirm your identity to download this backup." onVerified={t => void run(t)} onClose={() => setReauth(false)} />}
    </div>
  );
}
