import { useState, useEffect, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { QRCodeSVG } from 'qrcode.react';
import { usePageTitle } from '../hooks/usePageTitle';
import { useAuth } from '../contexts/AuthContext';
import api from '../api/client';
import { resetTotpSetup, resetTotpEnroll, getMfaStatus } from '../api/auth';
import { getDemoInfo, resetDemo } from '../api/demo';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { getTemplate, previewImport, importWithDuplicates, type PreviewResult } from '../api/import';
import { getAuditLog, type AuditEntry, type GetAuditParams } from '../api/audit';
import { getAccounts } from '../api/accounts';
import { listAccountBackups, downloadAccountBackup, type AccountBackupSummary } from '../api/accountBackups';
import { getDuplicates, ignoreDuplicateGroup, unignoreDuplicateGroup, type DuplicateGroup, type DuplicateTransaction } from '../api/duplicates';
import { deleteTransaction } from '../api/transactions';
import { getInstitutions, updateInstitution, deleteInstitution } from '../api/institutions';
import { getPreferences, updatePreferences } from '../api/preferences';
import type { Account, Institution } from '../types';
import ExportModal from '../components/ExportModal';
import ReauthModal from '../components/ReauthModal';
import styles from './Settings.module.css';

type Tab = 'preferences' | 'security' | 'export' | 'import' | 'backups' | 'duplicates' | 'institutions' | 'audit' | 'password';

// ── Change Password ──

function ChangePasswordTab() {
  const { logout } = useAuth();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [signingOutAll, setSigningOutAll] = useState(false);
  const [resettingDemo, setResettingDemo] = useState(false);
  const [isDemoMode, setIsDemoMode] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  useEffect(() => {
    getDemoInfo().then(info => setIsDemoMode(info.isDemoMode)).catch(() => {});
  }, []);

  useUnsavedChanges(!loading && (currentPassword !== '' || newPassword !== '' || confirmPassword !== ''));

  const validate = () => {
    if (!currentPassword) return 'Current password is required.';
    if (newPassword.length < 12) return 'New password must be at least 12 characters.';
    if (!/[A-Z]/.test(newPassword)) return 'New password must contain an uppercase letter.';
    if (!/[a-z]/.test(newPassword)) return 'New password must contain a lowercase letter.';
    if (!/[0-9]/.test(newPassword)) return 'New password must contain a digit.';
    if (newPassword !== confirmPassword) return 'Passwords do not match.';
    return null;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const err = validate();
    if (err) { setError(err); return; }
    setLoading(true);
    setError('');
    setSuccess('');
    try {
      const res = await api.post<{ message: string }>('/auth/change-password', { currentPassword, newPassword });
      setSuccess(res.data.message ?? 'Password changed successfully.');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to change password.');
    } finally {
      setLoading(false);
    }
  };

  const handleSignOutAll = async () => {
    if (!confirm('Sign out of all sessions? You will be signed out on all devices.')) return;
    setSigningOutAll(true);
    try {
      await api.post('/auth/logout-all');
      logout();
    } catch {
      setError('Failed to sign out all sessions.');
      setSigningOutAll(false);
    }
  };

  return (
    <div className={styles.tabSection}>
      {isDemoMode && (
        <div className={styles.hint} style={{ marginBottom: 16 }}>
          Password changes are disabled for the demo account.
        </div>
      )}
      <form className={styles.tabForm} onSubmit={handleSubmit} style={isDemoMode ? { display: 'none' } : undefined}>
        <div className={styles.field}>
          <label className={styles.label}>Current Password</label>
          <input
            type="password"
            className={styles.input}
            value={currentPassword}
            onChange={e => setCurrentPassword(e.target.value)}
            autoComplete="current-password"
          />
        </div>
        <div className={styles.field}>
          <label className={styles.label}>New Password (≥12 chars, upper + lower + digit)</label>
          <input
            type="password"
            className={styles.input}
            value={newPassword}
            onChange={e => setNewPassword(e.target.value)}
            autoComplete="new-password"
          />
        </div>
        <div className={styles.field}>
          <label className={styles.label}>Confirm New Password</label>
          <input
            type="password"
            className={styles.input}
            value={confirmPassword}
            onChange={e => setConfirmPassword(e.target.value)}
            autoComplete="new-password"
          />
        </div>
        {error && <div className={styles.error}>{error}</div>}
        {success && <div className={styles.success}>{success}</div>}
        <button type="submit" className={styles.btnPrimary} disabled={loading}>
          {loading ? 'Saving…' : 'Change Password'}
        </button>
      </form>

      <hr className={styles.divider} />

      <div>
        <div className={styles.sectionTitle}>Sessions</div>
        <p className={styles.hint}>Sign out of all devices and browsers, including this one.</p>
        <button className={styles.btnDanger} onClick={handleSignOutAll} disabled={signingOutAll}>
          {signingOutAll ? 'Signing out…' : 'Sign Out All Sessions'}
        </button>
      </div>

      {isDemoMode && (
        <>
          <hr className={styles.divider} />
          <div>
            <div className={styles.sectionTitle}>Demo Data</div>
            <p className={styles.hint}>Reset all demo data back to its original state. This cannot be undone.</p>
            {error && <div className={styles.error}>{error}</div>}
            {success && <div className={styles.success}>{success}</div>}
            <button
              className={styles.btnDanger}
              disabled={resettingDemo}
              onClick={async () => {
                if (!confirm('Reset all demo data to its original state?')) return;
                setResettingDemo(true);
                setError('');
                setSuccess('');
                try {
                  await resetDemo();
                  setSuccess('Demo data has been reset successfully.');
                } catch {
                  setError('Failed to reset demo data.');
                } finally {
                  setResettingDemo(false);
                }
              }}
            >
              {resettingDemo ? 'Resetting…' : 'Reset Demo Data'}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

// ── Preferences ──

const SORT_FIELD_OPTIONS: { value: string; label: string }[] = [
  { value: 'date',     label: 'Date' },
  { value: 'payee',    label: 'Payee' },
  { value: 'category', label: 'Category' },
  { value: 'memo',     label: 'Memo' },
  { value: 'amount',   label: 'Amount' },
  { value: 'status',   label: 'Status' },
];

const DEFAULT_FUTURE_DAYS = 31;

function PreferencesTab() {
  const [sortBy, setSortBy] = useState('date');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');
  const [futureDays, setFutureDays] = useState(String(DEFAULT_FUTURE_DAYS));
  const [autoCreate, setAutoCreate] = useState(false);
  const [autoCreateDays, setAutoCreateDays] = useState(String(DEFAULT_FUTURE_DAYS));
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    getPreferences()
      .then(p => {
        setSortBy(p.defaultRegisterSortBy ?? 'date');
        setSortDir(p.defaultRegisterSortDir ?? 'desc');
        setFutureDays(String(p.defaultFutureDays ?? DEFAULT_FUTURE_DAYS));
        setAutoCreate(!!p.autoCreateFutureTransactions);
        setAutoCreateDays(String(p.autoCreateFutureDays ?? DEFAULT_FUTURE_DAYS));
      })
      .catch(() => setError('Failed to load preferences.'))
      .finally(() => setLoading(false));
  }, []);

  const handleSave = async () => {
    const days = Math.max(1, Math.min(3650, Number(futureDays) || DEFAULT_FUTURE_DAYS));
    const autoDays = Math.max(1, Math.min(3650, Number(autoCreateDays) || DEFAULT_FUTURE_DAYS));
    setSaving(true);
    setError('');
    setSaved(false);
    try {
      await updatePreferences({
        defaultRegisterSortBy: sortBy,
        defaultRegisterSortDir: sortDir,
        defaultFutureDays: days,
        autoCreateFutureTransactions: autoCreate,
        autoCreateFutureDays: autoDays,
      });
      setFutureDays(String(days));
      setAutoCreateDays(String(autoDays));
      setSaved(true);
    } catch {
      setError('Failed to save preferences.');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setSaving(true);
    setError('');
    setSaved(false);
    try {
      await updatePreferences({
        defaultRegisterSortBy: null,
        defaultRegisterSortDir: null,
        defaultFutureDays: null,
        autoCreateFutureTransactions: false,
        autoCreateFutureDays: null,
      });
      setSortBy('date');
      setSortDir('desc');
      setFutureDays(String(DEFAULT_FUTURE_DAYS));
      setAutoCreate(false);
      setAutoCreateDays(String(DEFAULT_FUTURE_DAYS));
      setSaved(true);
    } catch {
      setError('Failed to reset preferences.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className={styles.tabSection}><p className={styles.hint}>Loading…</p></div>;

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Choose the sort field and direction the account register uses when you first open it. Manually changing
        the sort while viewing a register only applies for that visit — it doesn't change this default.
      </p>

      {error && <div className={styles.error}>{error}</div>}

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Register default sort</p>
        <p className={styles.hint}>
          Choose the sort field and direction the account register uses when you first open it. Manually changing
          the sort while viewing a register only applies for that visit — it doesn't change this default.
        </p>
        <div style={{ display: 'flex', gap: 20, alignItems: 'flex-end' }}>
          <div>
            <label style={{ display: 'block', fontSize: 12, fontWeight: 700, marginBottom: 4 }}>Sort field</label>
            <select value={sortBy} onChange={e => { setSortBy(e.target.value); setSaved(false); }}>
              {SORT_FIELD_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </div>
          <div>
            <label style={{ display: 'block', fontSize: 12, fontWeight: 700, marginBottom: 4 }}>Sort direction</label>
            <select value={sortDir} onChange={e => { setSortDir(e.target.value as 'asc' | 'desc'); setSaved(false); }}>
              <option value="desc">Descending (newest / highest first)</option>
              <option value="asc">Ascending (oldest / lowest first)</option>
            </select>
          </div>
        </div>
      </div>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Upcoming transactions preview</p>
        <p className={styles.hint}>
          How many days ahead the register's "upcoming scheduled transactions" preview section looks. These are
          just a projected preview, not real transactions, until they're actually due.
        </p>
        <div>
          <label style={{ display: 'block', fontSize: 12, fontWeight: 700, marginBottom: 4 }}>Days ahead (preview)</label>
          <input
            type="number"
            min={1}
            max={3650}
            value={futureDays}
            onChange={e => { setFutureDays(e.target.value); setSaved(false); }}
            style={{ width: 90 }}
          />
        </div>
      </div>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>Auto-create upcoming recurring transactions</p>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6, cursor: 'pointer' }}>
          <input
            type="checkbox"
            checked={autoCreate}
            onChange={e => { setAutoCreate(e.target.checked); setSaved(false); }}
          />
          <span style={{ fontSize: 13, fontWeight: 700 }}>Enable auto-create</span>
        </label>
        <p className={styles.hint}>
          When enabled, recurring transactions are created as real, future-dated transactions — editable in the
          register — once they're within the window below, instead of only appearing when actually due. This is
          separate from the preview window above: a recurring transaction can show in the preview well before it's
          auto-created, or auto-create can be set further out than the preview shows.
        </p>
        <div>
          <label style={{ display: 'block', fontSize: 12, fontWeight: 700, marginBottom: 4 }}>Days ahead (auto-create)</label>
          <input
            type="number"
            min={1}
            max={3650}
            value={autoCreateDays}
            disabled={!autoCreate}
            onChange={e => { setAutoCreateDays(e.target.value); setSaved(false); }}
            style={{ width: 90 }}
          />
        </div>
      </div>

      <div style={{ display: 'flex', gap: 10, alignItems: 'center' }}>
        <button className={styles.btnPrimary} onClick={handleSave} disabled={saving}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button className={styles.btnSecondary} onClick={handleReset} disabled={saving}>
          Reset to Default
        </button>
        {saved && <span style={{ color: '#1a7a40', fontSize: 12 }}>Saved.</span>}
      </div>
    </div>
  );
}

// ── Security ──

function SecurityTab() {
  const [mfaEnrolled, setMfaEnrolled] = useState<boolean | null>(null);
  const [showReauth, setShowReauth] = useState(false);
  const [resetting, setResetting] = useState(false);
  const [sharedKey, setSharedKey] = useState('');
  const [authenticatorUri, setAuthenticatorUri] = useState('');
  const [code, setCode] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState(false);

  // Live server-side state, not the auth context's login-time snapshot: an
  // abandoned reset leaves MFA disabled server-side, and the stale context
  // would show "Enabled" on an actually-unprotected account.
  useEffect(() => {
    getMfaStatus()
      .then(s => setMfaEnrolled(s.mfaEnrolled))
      .catch(() => setError('Failed to load two-factor status.'));
  }, []);

  const inProgress = !!authenticatorUri;

  // A reset in progress means MFA is currently DISABLED server-side — warn
  // before the user navigates away and leaves the account unprotected.
  useUnsavedChanges(inProgress);

  const handleVerified = async (exportToken: string) => {
    setShowReauth(false);
    setResetting(true);
    setError('');
    try {
      const res = await resetTotpSetup(exportToken);
      setSharedKey(res.sharedKey);
      setAuthenticatorUri(res.authenticatorUri);
      // The backend has already invalidated the old code and disabled MFA
      // until the new one is confirmed below.
      setMfaEnrolled(false);
    } catch {
      setError('Failed to start authenticator reset. Your identity check may have expired — try again.');
    } finally {
      setResetting(false);
    }
  };

  const handleConfirm = async () => {
    if (!code || code.length !== 6) {
      setError('Please enter a 6-digit code.');
      return;
    }
    setConfirming(true);
    setError('');
    try {
      await resetTotpEnroll(code);
      setMfaEnrolled(true);
      setAuthenticatorUri('');
      setSharedKey('');
      setCode('');
      setSuccess(true);
    } catch (err) {
      const msg = (err as { response?: { data?: string } })?.response?.data;
      setError(typeof msg === 'string' ? msg : 'Invalid code. Please try again.');
    } finally {
      setConfirming(false);
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Two-factor authentication (TOTP) status:{' '}
        <strong>{mfaEnrolled === null ? 'Checking…' : mfaEnrolled ? 'Enabled' : 'Not enabled'}</strong>
      </p>

      {mfaEnrolled === false && !inProgress && !success && (
        <div className={styles.error}>
          Two-factor authentication is currently disabled — your account is protected by password only.
          {' '}If you started an authenticator reset and didn't finish it, set up a new authenticator below.
        </div>
      )}

      {error && <div className={styles.error}>{error}</div>}
      {success && <p style={{ color: '#1a7a40', fontSize: 12 }}>Authenticator reset. Your new code is now active.</p>}

      {!inProgress && (
        <>
          <p className={styles.hint}>
            Lost your device, or want to move to a new one? Resetting invalidates your current authenticator
            code immediately and generates a new one — you'll need to confirm a code from the new one before
            it takes effect. Requires confirming your password or current authenticator code first.
          </p>
          <button className={styles.btnSecondary} onClick={() => setShowReauth(true)} disabled={resetting || mfaEnrolled === null}>
            {resetting ? 'Starting…' : mfaEnrolled ? 'Reset Authenticator App' : 'Set Up Authenticator App'}
          </button>
        </>
      )}

      {inProgress && (
        <div>
          <p className={styles.hint}>
            <strong>Your previous code no longer works, and two-factor auth stays disabled until you finish.</strong>
            {' '}Scan this into your authenticator app and enter a fresh code to finish.
          </p>
          <div style={{ margin: '12px 0' }}>
            <QRCodeSVG value={authenticatorUri} size={160} />
          </div>
          <p className={styles.hint}>Or enter this key manually: <code>{sharedKey}</code></p>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 10 }}>
            <input
              type="text"
              inputMode="numeric"
              pattern="[0-9]{6}"
              maxLength={6}
              placeholder="000000"
              value={code}
              onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
              autoFocus
            />
            <button className={styles.btnPrimary} onClick={handleConfirm} disabled={confirming}>
              {confirming ? 'Confirming…' : 'Confirm'}
            </button>
          </div>
        </div>
      )}

      {showReauth && (
        <ReauthModal
          title="Confirm Identity"
          hint="Confirm your identity to reset your authenticator app."
          onVerified={handleVerified}
          onClose={() => setShowReauth(false)}
        />
      )}
    </div>
  );
}

// ── Export Data ──

function ExportTab() {
  const [open, setOpen] = useState(false);
  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Export all your data in a variety of formats (QIF, OFX, CSV, XLSX, JSON).
        You will be asked to confirm your identity before downloading.
      </p>
      <button className={styles.btnPrimary} onClick={() => setOpen(true)}>
        Export Data…
      </button>
      {open && <ExportModal onClose={() => setOpen(false)} />}
    </div>
  );
}

// ── Import Data ──

function ImportTab() {
  const [dragging, setDragging] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [step, setStep] = useState<'select' | 'review' | 'done'>('select');
  const [preview, setPreview] = useState<PreviewResult | null>(null);
  const [checkedDups, setCheckedDups] = useState<Set<number>>(new Set());
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<{ imported: number; transfersLinked: number; errors?: string[] } | null>(null);

  // QIF files often don't embed an account name (Money Sunset exports one
  // account at a time) — the user picks the destination account up front.
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [qifAccountId, setQifAccountId] = useState<number | ''>('');
  const isQif = !!file?.name.toLowerCase().endsWith('.qif');

  useEffect(() => {
    getAccounts().then(setAccounts).catch(() => {});
  }, []);

  const handleDrop = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setDragging(false);
    const f = e.dataTransfer.files[0];
    if (f) { setFile(f); setError(''); setQifAccountId(''); }
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0] ?? null;
    if (f) { setFile(f); setError(''); setQifAccountId(''); }
  };

  const handlePreview = async () => {
    if (!file) return;
    setLoading(true);
    setError('');
    try {
      const res = await previewImport(file, isQif && qifAccountId !== '' ? qifAccountId : undefined);
      if (res.error) { setError(res.error); setLoading(false); return; }
      setPreview(res);
      setCheckedDups(new Set());
      setStep('review');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Preview failed.');
    } finally {
      setLoading(false);
    }
  };

  const handleImport = async (includeChecked: boolean) => {
    if (!file || !preview) return;
    setLoading(true);
    setError('');
    try {
      const includeDuplicateIds = includeChecked
        ? Array.from(checkedDups)
        : [];
      const res = await importWithDuplicates(file, includeDuplicateIds, isQif && qifAccountId !== '' ? qifAccountId : undefined);
      setResult(res);
      setStep('done');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Import failed.');
    } finally {
      setLoading(false);
    }
  };

  const handleDownloadTemplate = async (format: 'csv' | 'xlsx') => {
    setError('');
    try {
      const blob = await getTemplate(format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `template.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setError('Failed to download template.');
    }
  };

  const toggleDup = (id: number) => {
    setCheckedDups(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  if (step === 'done' && result) {
    return (
      <div className={styles.tabSection}>
        <div className={styles.resultSuccess}>
          {result.imported} transaction{result.imported !== 1 ? 's' : ''} imported successfully.
          {result.transfersLinked > 0 && (
            <> {result.transfersLinked} linked as transfer{result.transfersLinked !== 1 ? 's' : ''} to existing transactions.</>
          )}
        </div>
        {result.errors && result.errors.length > 0 && (
          <div className={styles.resultErrors}>
            <div className={styles.resultErrorTitle}>Warnings:</div>
            <ul>{result.errors.map((e, i) => <li key={i}>{e}</li>)}</ul>
          </div>
        )}
        <button className={styles.btnSecondary} onClick={() => { setStep('select'); setFile(null); setResult(null); }}>
          Import Another File
        </button>
      </div>
    );
  }

  if (step === 'review' && preview) {
    return (
      <div className={styles.tabSection}>
        <p className={styles.hint}>
          <strong>{preview.newTransactions}</strong> new transaction{preview.newTransactions !== 1 ? 's' : ''} and{' '}
          <strong>{preview.duplicates.length}</strong> potential duplicate{preview.duplicates.length !== 1 ? 's' : ''} found.
          {preview.transferMatches > 0 && (
            <> <strong>{preview.transferMatches}</strong> of the new transactions look like transfer{preview.transferMatches !== 1 ? 's' : ''} to existing accounts and will be linked automatically.</>
          )}
        </p>

        {preview.warnings && preview.warnings.length > 0 && (
          <div className={styles.resultErrors}>
            <div className={styles.resultErrorTitle}>Warnings:</div>
            <ul>{preview.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
          </div>
        )}

        {preview.duplicates.length > 0 && (
          <>
            <p className={styles.hint}>Check the duplicates you want to include:</p>
            <table className={styles.dupTable}>
              <thead>
                <tr>
                  <th>Include</th>
                  <th>Date</th>
                  <th>Payee</th>
                  <th>Amount</th>
                  <th>Memo</th>
                </tr>
              </thead>
              <tbody>
                {preview.duplicates.map(d => (
                  <tr key={d.matchedTransactionId}>
                    <td>
                      <input
                        type="checkbox"
                        checked={checkedDups.has(d.matchedTransactionId)}
                        onChange={() => toggleDup(d.matchedTransactionId)}
                      />
                    </td>
                    <td>{d.date}</td>
                    <td>{d.payee}</td>
                    <td style={{ color: d.amount < 0 ? '#cc0000' : '#006600' }}>
                      {d.amount.toFixed(2)}
                    </td>
                    <td>{d.memo}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        )}

        {error && <div className={styles.error}>{error}</div>}

        <div className={styles.actions}>
          <button
            className={styles.btnPrimary}
            onClick={() => handleImport(true)}
            disabled={loading}
          >
            {loading ? 'Importing…' : 'Import All'}
          </button>
          <button
            className={styles.btnSecondary}
            onClick={() => handleImport(false)}
            disabled={loading}
          >
            Import New Only
          </button>
          <button className={styles.btnLink} onClick={() => setStep('select')}>
            ← Back
          </button>
        </div>
      </div>
    );
  }

  // step === 'select'
  return (
    <div className={styles.tabSection}>
      <div
        className={`${styles.dropZone} ${dragging ? styles.dragging : ''}`}
        onDragOver={e => { e.preventDefault(); setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        onClick={() => document.getElementById('importFileInput')?.click()}
      >
        <input
          id="importFileInput"
          type="file"
          accept=".qif,.ofx,.qfx,.csv,.xlsx,.json"
          style={{ display: 'none' }}
          onChange={handleFileChange}
        />
        {file ? (
          <div>
            <div className={styles.fileName}>{file.name}</div>
            <div className={styles.fileSize}>{(file.size / 1024).toFixed(1)} KB</div>
          </div>
        ) : (
          <div className={styles.dropPrompt}>
            <div className={styles.dropIcon}>+</div>
            <div>Drag &amp; drop a file here, or click to browse</div>
            <div className={styles.hint}>Supported: QIF, OFX, QFX, CSV, XLSX, JSON (this app's own export format)</div>
          </div>
        )}
      </div>

      {isQif && (
        <div className={styles.field} style={{ maxWidth: 320 }}>
          <label className={styles.label}>
            Import into account
            <span className={styles.hint}> (only needed if the QIF file doesn't already specify one)</span>
          </label>
          <select
            className={styles.input}
            value={qifAccountId}
            onChange={e => setQifAccountId(e.target.value ? Number(e.target.value) : '')}
          >
            <option value="">— Use account from file, if present —</option>
            {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
        </div>
      )}

      {file && (
        <button className={styles.btnPrimary} onClick={handlePreview} disabled={loading}>
          {loading ? 'Analyzing…' : `Preview ${file.name}`}
        </button>
      )}

      {error && <div className={styles.error}>{error}</div>}

      <div className={styles.templateSection}>
        <div className={styles.sectionTitle}>Download Templates</div>
        <div className={styles.templateBtns}>
          <button className={styles.btnSecondary} onClick={() => handleDownloadTemplate('csv')}>
            CSV Template
          </button>
          <button className={styles.btnSecondary} onClick={() => handleDownloadTemplate('xlsx')}>
            XLSX Template
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Account Backups ──

function formatBytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function BackupsTab() {
  const [backups, setBackups] = useState<AccountBackupSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [downloadingFile, setDownloadingFile] = useState<string | null>(null);
  const [reauthFile, setReauthFile] = useState<string | null>(null);

  useEffect(() => {
    listAccountBackups()
      .then(setBackups)
      .catch(() => setError('Failed to load backups.'))
      .finally(() => setLoading(false));
  }, []);

  const handleVerified = async (fileName: string, exportToken: string) => {
    setReauthFile(null);
    setDownloadingFile(fileName);
    setError('');
    try {
      const blob = await downloadAccountBackup(fileName, exportToken);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setError('Failed to download backup. Your identity check may have expired — try again.');
    } finally {
      setDownloadingFile(null);
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        A full backup (account details, transactions, and splits) is automatically saved on the server before an
        account is deleted. Up to 3 backups are kept per account; the oldest is removed once a new one is created
        for that account.
      </p>

      {loading && <p className={styles.hint}>Loading…</p>}
      {error && <div className={styles.error}>{error}</div>}

      {!loading && !error && backups.length === 0 && (
        <p className={styles.hint}>No account backups yet.</p>
      )}

      {backups.length > 0 && (
        <table className={styles.dupTable}>
          <thead>
            <tr>
              <th>Account</th>
              <th>Backed Up</th>
              <th>Note</th>
              <th>Size</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {backups.map(b => (
              <tr key={b.fileName}>
                <td>{b.accountName}</td>
                <td>{new Date(b.backedUpAt).toLocaleString()}</td>
                <td>{b.note ?? ''}</td>
                <td>{formatBytes(b.sizeBytes)}</td>
                <td>
                  <button
                    className={styles.btnSecondary}
                    onClick={() => setReauthFile(b.fileName)}
                    disabled={downloadingFile === b.fileName}
                  >
                    {downloadingFile === b.fileName ? 'Downloading…' : 'Download'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {reauthFile && (
        <ReauthModal
          title="Confirm Identity"
          hint="Confirm your identity to download this backup."
          onVerified={token => handleVerified(reauthFile, token)}
          onClose={() => setReauthFile(null)}
        />
      )}
    </div>
  );
}

// ── Find Duplicates ──

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function DuplicatesTab() {
  const [groups, setGroups] = useState<DuplicateGroup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [includeMemo, setIncludeMemo] = useState(false);
  const [includeCategory, setIncludeCategory] = useState(false);
  const [showIgnored, setShowIgnored] = useState(false);
  const [togglingKey, setTogglingKey] = useState('');

  const load = () => {
    setLoading(true);
    setError('');
    getDuplicates({ includeMemo, includeCategory, showIgnored })
      .then(setGroups)
      .catch(() => setError('Failed to load duplicate transactions.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, [includeMemo, includeCategory, showIgnored]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleDelete = async (accountId: number, id: number) => {
    if (!confirm('Delete this transaction? This cannot be undone.')) return;
    setDeletingId(id);
    try {
      await deleteTransaction(accountId, id);
      // Re-fetch rather than patch in place: deleting one row out of a group of
      // two collapses it below the ">1 transactions" threshold and should
      // disappear entirely, not linger as a single-row "duplicate".
      load();
    } catch {
      setError('Failed to delete transaction.');
    } finally {
      setDeletingId(null);
    }
  };

  const groupKeyOf = (g: DuplicateGroup) => `${g.accountId}-${g.date}-${g.amount}`;

  const handleIgnore = async (g: DuplicateGroup) => {
    const key = groupKeyOf(g);
    setTogglingKey(key);
    try {
      await ignoreDuplicateGroup({ accountId: g.accountId, date: g.date, amount: g.amount });
      load();
    } catch {
      setError('Failed to ignore this group.');
    } finally {
      setTogglingKey('');
    }
  };

  const handleUnignore = async (g: DuplicateGroup) => {
    const key = groupKeyOf(g);
    setTogglingKey(key);
    try {
      await unignoreDuplicateGroup({ accountId: g.accountId, date: g.date, amount: g.amount });
      load();
    } catch {
      setError('Failed to un-ignore this group.');
    } finally {
      setTogglingKey('');
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Transactions in the same account, on the same date, for the same amount are grouped below — a common
        symptom of a file that got imported twice, or genuinely repeated transactions. Review each group and
        delete anything that shouldn't be there; transfers are excluded since linked transfer legs naturally
        share a date and amount. If a group isn't actually a duplicate, you can ignore it — it stays hidden as
        long as nothing about it changes, and reappears automatically if a member transaction is edited or
        another transaction joins the group.
      </p>

      <div style={{ display: 'flex', gap: 20, marginBottom: 14, flexWrap: 'wrap' }}>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={includeMemo} onChange={e => setIncludeMemo(e.target.checked)} />
          Also match memo
        </label>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={includeCategory} onChange={e => setIncludeCategory(e.target.checked)} />
          Also match category
        </label>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={showIgnored} onChange={e => setShowIgnored(e.target.checked)} />
          Show ignored groups
        </label>
      </div>

      {loading && <p className={styles.hint}>Loading…</p>}
      {error && <div className={styles.error}>{error}</div>}

      {!loading && !error && groups.length === 0 && (
        <p className={styles.hint}>No likely duplicates found.</p>
      )}

      {groups.map((g, i) => {
        // A field only helps distinguish these rows if the transactions in
        // this group actually disagree on it — highlight just those cells.
        const fields: Array<keyof DuplicateTransaction> = ['payee', 'category', 'memo', 'checkNumber', 'status'];
        const varies = Object.fromEntries(
          fields.map(f => [f, new Set(g.transactions.map(t => t[f] ?? '')).size > 1])
        ) as Record<keyof DuplicateTransaction, boolean>;
        const key = groupKeyOf(g);

        return (
          <table
            className={`${styles.dupTable} ${g.ignored ? styles.dupTableIgnored : ''}`}
            key={`${key}-${i}`}
            style={{ marginBottom: 16 }}
          >
            <thead>
              <tr>
                <th colSpan={5}>
                  {g.accountName} — {formatDate(g.date)} — {formatCurrency(g.amount)}
                  {g.ignored && <span className={styles.dupIgnoredBadge}>Ignored</span>}
                </th>
                <th>
                  {g.ignored ? (
                    <button
                      className={styles.btnSecondary}
                      onClick={() => handleUnignore(g)}
                      disabled={togglingKey === key}
                    >
                      {togglingKey === key ? 'Un-ignoring…' : 'Un-ignore'}
                    </button>
                  ) : (
                    <button
                      className={styles.btnSecondary}
                      onClick={() => handleIgnore(g)}
                      disabled={togglingKey === key}
                    >
                      {togglingKey === key ? 'Ignoring…' : 'Ignore'}
                    </button>
                  )}
                </th>
              </tr>
              <tr>
                <th>Payee</th>
                <th>Category</th>
                <th>Memo</th>
                <th>Check #</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {g.transactions.map(t => (
                <tr key={t.id}>
                  <td className={varies.payee ? styles.dupDiffCell : undefined}>
                    {t.payee ?? <span className={styles.hint}>—</span>}
                  </td>
                  <td className={varies.category ? styles.dupDiffCell : undefined}>
                    {t.category ?? <span className={styles.hint}>—</span>}
                  </td>
                  <td className={varies.memo ? styles.dupDiffCell : undefined}>{t.memo ?? ''}</td>
                  <td className={varies.checkNumber ? styles.dupDiffCell : undefined}>{t.checkNumber ?? ''}</td>
                  <td className={varies.status ? styles.dupDiffCell : undefined}>{t.status}</td>
                  <td style={{ display: 'flex', gap: 8 }}>
                    <Link className={styles.btnSecondary} to={`/accounts/${g.accountId}?tx=${t.id}`}>
                      View
                    </Link>
                    <button
                      className={styles.btnDanger}
                      onClick={() => handleDelete(g.accountId, t.id)}
                      disabled={deletingId === t.id}
                    >
                      {deletingId === t.id ? 'Deleting…' : 'Delete'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        );
      })}
    </div>
  );
}

// ── Institutions ──

function InstitutionsTab() {
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [renamingId, setRenamingId] = useState<number | null>(null);
  const [renameValue, setRenameValue] = useState('');

  const load = () => {
    setLoading(true);
    setError('');
    getInstitutions()
      .then(list => setInstitutions([...list].sort((a, b) => a.name.localeCompare(b.name))))
      .catch(() => setError('Failed to load institutions.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const handleRename = async (id: number) => {
    if (!renameValue.trim()) return;
    const inst = institutions.find(i => i.id === id);
    if (!confirm(`Rename "${inst?.name}" to "${renameValue.trim()}"?`)) return;
    try {
      await updateInstitution(id, renameValue.trim());
      setRenamingId(null);
      load();
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to rename institution.');
    }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
    try {
      await deleteInstitution(id);
      setInstitutions(prev => prev.filter(i => i.id !== id));
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to delete institution. It may still be assigned to an account.');
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Banks and institutions available when creating or editing an account. Rename or delete one here; deleting
        is blocked while it's still assigned to any account.
      </p>

      {loading && <p className={styles.hint}>Loading…</p>}
      {error && <div className={styles.error}>{error}</div>}

      {!loading && !error && institutions.length === 0 && (
        <p className={styles.hint}>No institutions yet — add one from the account creation form.</p>
      )}

      {institutions.length > 0 && (
        <table className={styles.dupTable}>
          <thead>
            <tr>
              <th>Name</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {institutions.map(inst => (
              <tr key={inst.id}>
                <td>
                  {renamingId === inst.id ? (
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <input
                        value={renameValue}
                        onChange={e => setRenameValue(e.target.value)}
                        onKeyDown={e => { if (e.key === 'Enter') handleRename(inst.id); if (e.key === 'Escape') setRenamingId(null); }}
                        autoFocus
                      />
                      <button className={styles.btnSecondary} onClick={() => handleRename(inst.id)}>Save</button>
                      <button className={styles.btnSecondary} onClick={() => setRenamingId(null)}>Cancel</button>
                    </div>
                  ) : (
                    inst.name
                  )}
                </td>
                <td>
                  {renamingId !== inst.id && (
                    <div style={{ display: 'flex', gap: 8 }}>
                      <button
                        className={styles.btnSecondary}
                        onClick={() => { setRenamingId(inst.id); setRenameValue(inst.name); }}
                      >
                        Rename
                      </button>
                      <button className={styles.btnDanger} onClick={() => handleDelete(inst.id, inst.name)}>Delete</button>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ── Audit Log ──

const ENTITY_TYPES = ['All', 'Transaction', 'Account', 'Category', 'Payee', 'User'];

const ACTION_BADGE_STYLES: Record<string, React.CSSProperties> = {
  CREATE:   { background: '#e8f8e8', color: '#006600', border: '1px solid #006600' },
  UPDATE:   { background: '#e8f0ff', color: '#003399', border: '1px solid #003399' },
  DELETE:   { background: '#ffe8e8', color: '#cc0000', border: '1px solid #cc0000' },
  LOGIN:    { background: '#f0f0f0', color: '#555',    border: '1px solid #999' },
  LOGOUT:   { background: '#f0f0f0', color: '#555',    border: '1px solid #999' },
  REGISTER: { background: '#f0f0f0', color: '#555',    border: '1px solid #999' },
  STARTUP:  { background: '#f0e8ff', color: '#6600cc', border: '1px solid #6600cc' },
};

function actionBadgeStyle(action: string, isSystem: boolean): React.CSSProperties {
  if (isSystem) return { background: '#f0e8ff', color: '#6600cc', border: '1px solid #6600cc' };
  return ACTION_BADGE_STYLES[action] ?? { background: '#f8f8e8', color: '#666', border: '1px solid #ccc' };
}

function parseDetails(raw?: string): string {
  if (!raw) return '';
  try {
    const obj = JSON.parse(raw) as Record<string, unknown>;
    return Object.entries(obj)
      .map(([k, v]) => {
        if (typeof v === 'number' && (k === 'amount')) {
          return `${k}: ${v < 0 ? '-' : ''}$${Math.abs(v).toFixed(2)}`;
        }
        return `${k}: ${String(v ?? '')}`;
      })
      .join(', ');
  } catch {
    return raw;
  }
}

function AuditLogTab() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const [filterAction, setFilterAction] = useState('');
  const [filterEntityType, setFilterEntityType] = useState('');
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [filterSearch, setFilterSearch] = useState('');

  const pageSize = 50;

  const fetchPage = async (pageNum: number, replace: boolean) => {
    setLoading(true);
    setError('');
    try {
      const params: GetAuditParams = { page: pageNum, pageSize };
      if (filterAction.trim()) params.action = filterAction.trim();
      if (filterEntityType && filterEntityType !== 'All') params.entityType = filterEntityType;
      if (filterFrom) params.from = filterFrom;
      if (filterTo) params.to = filterTo;
      if (filterSearch.trim()) params.search = filterSearch.trim();

      const data = await getAuditLog(params);
      setTotal(data.total);
      setEntries(prev => replace ? data.items : [...prev, ...data.items]);
      setPage(pageNum);
    } catch {
      setError('Failed to load audit log.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchPage(1, true); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const handleApply = (e: FormEvent) => {
    e.preventDefault();
    fetchPage(1, true);
  };

  return (
    <div className={styles.tabSection}>
      <form onSubmit={handleApply} className={styles.auditFilters}>
        <div className={styles.field}>
          <label className={styles.label}>Action</label>
          <input
            className={styles.input}
            placeholder="e.g. CREATE"
            value={filterAction}
            onChange={e => setFilterAction(e.target.value)}
          />
        </div>
        <div className={styles.field}>
          <label className={styles.label}>Entity Type</label>
          <select
            className={styles.input}
            value={filterEntityType}
            onChange={e => setFilterEntityType(e.target.value)}
          >
            {ENTITY_TYPES.map(t => <option key={t} value={t === 'All' ? '' : t}>{t}</option>)}
          </select>
        </div>
        <div className={styles.field}>
          <label className={styles.label}>From</label>
          <input
            type="date"
            className={styles.input}
            value={filterFrom}
            onChange={e => setFilterFrom(e.target.value)}
          />
        </div>
        <div className={styles.field}>
          <label className={styles.label}>To</label>
          <input
            type="date"
            className={styles.input}
            value={filterTo}
            onChange={e => setFilterTo(e.target.value)}
          />
        </div>
        <div className={styles.field} style={{ flex: '1 1 160px' }}>
          <label className={styles.label}>Search</label>
          <input
            className={styles.input}
            placeholder="Search details or email"
            value={filterSearch}
            onChange={e => setFilterSearch(e.target.value)}
          />
        </div>
        <button type="submit" className={styles.btnPrimary} disabled={loading} style={{ alignSelf: 'flex-end' }}>
          Apply
        </button>
      </form>

      {error && <div className={styles.error}>{error}</div>}

      <div className={styles.auditTableWrap}>
        <table className={styles.auditTable}>
          <thead>
            <tr>
              <th>Timestamp</th>
              <th>Action</th>
              <th>Entity</th>
              <th>Details</th>
              <th>User</th>
              <th>IP</th>
            </tr>
          </thead>
          <tbody>
            {entries.length === 0 && !loading && (
              <tr><td colSpan={6} style={{ textAlign: 'center', color: '#888', padding: '16px' }}>No entries found.</td></tr>
            )}
            {entries.map(e => (
              <tr key={e.id}>
                <td className={styles.auditTs}>{new Date(e.timestamp).toLocaleString()}</td>
                <td>
                  <span className={styles.auditBadge} style={actionBadgeStyle(e.action, e.isSystem)}>
                    {e.action}
                  </span>
                </td>
                <td className={styles.auditEntity}>
                  {e.entityType ?? '—'}
                  {e.entityId != null ? ` #${e.entityId}` : ''}
                </td>
                <td className={styles.auditDetails}>{parseDetails(e.details)}</td>
                <td className={styles.auditUser}>
                  {e.isSystem ? <span className={styles.auditSystem}>System</span> : (e.userEmail ?? e.userId ?? '—')}
                </td>
                <td className={styles.auditIp}>{e.ipAddress ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {entries.length < total && (
        <button
          className={styles.btnSecondary}
          onClick={() => fetchPage(page + 1, false)}
          disabled={loading}
          style={{ alignSelf: 'flex-start' }}
        >
          {loading ? 'Loading…' : `Load More (${entries.length} / ${total})`}
        </button>
      )}
    </div>
  );
}

// ── Settings page ──

export default function Settings() {
  usePageTitle('Settings');
  const [tab, setTab] = useState<Tab>('preferences');

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Settings</h2>
      </div>
      <div className={styles.tabs}>
        <button
          className={`${styles.tab} ${tab === 'preferences' ? styles.tabActive : ''}`}
          onClick={() => setTab('preferences')}
        >
          Preferences
        </button>
        <button
          className={`${styles.tab} ${tab === 'security' ? styles.tabActive : ''}`}
          onClick={() => setTab('security')}
        >
          Security
        </button>
        <button
          className={`${styles.tab} ${tab === 'export' ? styles.tabActive : ''}`}
          onClick={() => setTab('export')}
        >
          Export Data
        </button>
        <button
          className={`${styles.tab} ${tab === 'import' ? styles.tabActive : ''}`}
          onClick={() => setTab('import')}
        >
          Import Data
        </button>
        <button
          className={`${styles.tab} ${tab === 'backups' ? styles.tabActive : ''}`}
          onClick={() => setTab('backups')}
        >
          Account Backups
        </button>
        <button
          className={`${styles.tab} ${tab === 'duplicates' ? styles.tabActive : ''}`}
          onClick={() => setTab('duplicates')}
        >
          Find Duplicates
        </button>
        <button
          className={`${styles.tab} ${tab === 'institutions' ? styles.tabActive : ''}`}
          onClick={() => setTab('institutions')}
        >
          Institutions
        </button>
        <button
          className={`${styles.tab} ${tab === 'audit' ? styles.tabActive : ''}`}
          onClick={() => setTab('audit')}
        >
          Audit Log
        </button>
        <button
          className={`${styles.tab} ${tab === 'password' ? styles.tabActive : ''}`}
          onClick={() => setTab('password')}
        >
          Change Password
        </button>
      </div>
      <div className={styles.tabContent}>
        {tab === 'preferences' && <PreferencesTab />}
        {tab === 'security' && <SecurityTab />}
        {tab === 'export' && <ExportTab />}
        {tab === 'import' && <ImportTab />}
        {tab === 'backups' && <BackupsTab />}
        {tab === 'duplicates' && <DuplicatesTab />}
        {tab === 'institutions' && <InstitutionsTab />}
        {tab === 'audit' && <AuditLogTab />}
        {tab === 'password' && <ChangePasswordTab />}
      </div>
    </div>
  );
}
