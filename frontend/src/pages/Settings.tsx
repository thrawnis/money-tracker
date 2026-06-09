import { useState, useEffect, type FormEvent } from 'react';
import { usePageTitle } from '../hooks/usePageTitle';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import api from '../api/client';
import { getDemoInfo, resetDemo } from '../api/demo';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { getTemplate, previewImport, importWithDuplicates } from '../api/import';
import { getAuditLog, type AuditEntry, type GetAuditParams } from '../api/audit';
import { getAccounts, createAccount, updateAccount, deleteAccount } from '../api/accounts';
import { getInstitutions, createInstitution } from '../api/institutions';
import type { Account, AccountType, Institution } from '../types';
import styles from './Settings.module.css';

type Tab = 'accounts' | 'password' | 'export' | 'import' | 'audit';
type ExportStep = 'auth' | 'format';
type AuthMethod = 'password' | 'totp';
type ExportFormat = 'qif' | 'ofx' | 'csv' | 'xlsx' | 'json';

const FORMATS: { id: ExportFormat; label: string }[] = [
  { id: 'qif', label: 'QIF' },
  { id: 'ofx', label: 'OFX' },
  { id: 'csv', label: 'CSV' },
  { id: 'xlsx', label: 'XLSX' },
  { id: 'json', label: 'JSON' },
];

// ── Change Password ──────────────────────────────────────────────────────────

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

// ── Export Data ──────────────────────────────────────────────────────────────

function ExportTab() {
  const [step, setStep] = useState<ExportStep>('auth');
  const [authMethod, setAuthMethod] = useState<AuthMethod>('password');
  const [password, setPassword] = useState('');
  const [totpCode, setTotpCode] = useState('');
  const [exportToken, setExportToken] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);
  const [downloadError, setDownloadError] = useState('');

  const handleAuth = async (e: FormEvent) => {
    e.preventDefault();
    setAuthError('');
    setAuthLoading(true);
    try {
      const payload = authMethod === 'password' ? { password } : { totpCode };
      const res = await api.post<{ exportToken: string }>('/export/confirm-identity', payload);
      setExportToken(res.data.exportToken);
      setStep('format');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setAuthError(msg ?? 'Authentication failed.');
    } finally {
      setAuthLoading(false);
    }
  };

  const handleExport = async (format: ExportFormat) => {
    setDownloadError('');
    try {
      const res = await api.get(`/export/${format}`, {
        params: { exportToken },
        responseType: 'blob',
      });
      const url = URL.createObjectURL(res.data as Blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `export.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      setDownloadError('Export failed. Your export token may have expired.');
    }
  };

  return (
    <div className={styles.tabSection}>
      {step === 'auth' && (
        <form onSubmit={handleAuth} className={styles.tabForm}>
          <p className={styles.hint}>Confirm your identity to export data.</p>
          <div className={styles.toggleRow}>
            <button
              type="button"
              className={`${styles.toggleBtn} ${authMethod === 'password' ? styles.toggleActive : ''}`}
              onClick={() => setAuthMethod('password')}
            >
              Password
            </button>
            <button
              type="button"
              className={`${styles.toggleBtn} ${authMethod === 'totp' ? styles.toggleActive : ''}`}
              onClick={() => setAuthMethod('totp')}
            >
              Authenticator Code
            </button>
          </div>
          {authMethod === 'password' ? (
            <div className={styles.field}>
              <label className={styles.label}>Password</label>
              <input
                type="password"
                className={styles.input}
                value={password}
                onChange={e => setPassword(e.target.value)}
                autoComplete="current-password"
              />
            </div>
          ) : (
            <div className={styles.field}>
              <label className={styles.label}>6-digit code</label>
              <input
                type="text"
                inputMode="numeric"
                maxLength={6}
                className={styles.input}
                value={totpCode}
                onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                autoComplete="one-time-code"
                placeholder="000000"
              />
            </div>
          )}
          {authError && <div className={styles.error}>{authError}</div>}
          <button type="submit" className={styles.btnPrimary} disabled={authLoading}>
            {authLoading ? 'Verifying…' : 'Continue'}
          </button>
        </form>
      )}

      {step === 'format' && (
        <div className={styles.tabForm}>
          <p className={styles.hint}>Choose an export format:</p>
          <div className={styles.formatBtns}>
            {FORMATS.map(f => (
              <button key={f.id} className={styles.formatBtn} onClick={() => handleExport(f.id)}>
                {f.label}
              </button>
            ))}
          </div>
          {downloadError && <div className={styles.error}>{downloadError}</div>}
          <button type="button" className={styles.btnLink} onClick={() => setStep('auth')}>
            ← Re-authenticate
          </button>
        </div>
      )}
    </div>
  );
}

// ── Import Data ──────────────────────────────────────────────────────────────

interface DuplicateRow {
  date: string;
  payee: string;
  amount: number;
  memo?: string;
  matchedTransactionId: number;
}

interface PreviewResult {
  total: number;
  duplicates: DuplicateRow[];
  newTransactions: number;
  error?: string;
}

function ImportTab() {
  const [dragging, setDragging] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [step, setStep] = useState<'select' | 'review' | 'done'>('select');
  const [preview, setPreview] = useState<PreviewResult | null>(null);
  const [checkedDups, setCheckedDups] = useState<Set<number>>(new Set());
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<{ imported: number; errors?: string[] } | null>(null);

  const handleDrop = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setDragging(false);
    const f = e.dataTransfer.files[0];
    if (f) { setFile(f); setError(''); }
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0] ?? null;
    if (f) { setFile(f); setError(''); }
  };

  const handlePreview = async () => {
    if (!file) return;
    setLoading(true);
    setError('');
    try {
      const res = await previewImport(file);
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
      const res = await importWithDuplicates(file, includeDuplicateIds);
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
    try {
      const blob = await getTemplate(format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `template.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      alert('Failed to download template.');
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
        </p>

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
          accept=".qif,.ofx,.qfx,.csv,.xlsx"
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
            <div className={styles.hint}>Supported: QIF, OFX, QFX, CSV, XLSX</div>
          </div>
        )}
      </div>

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

// ── Audit Log ────────────────────────────────────────────────────────────────

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

// ── Accounts Tab ─────────────────────────────────────────────────────────────

const ACCOUNT_TYPES: AccountType[] = [
  'Checking', 'Savings', 'CreditCard', 'Cash', 'Loan', 'Investment', 'Other',
];

interface EditState {
  name: string;
  type: AccountType;
  isActive: boolean;
  notes: string;
  institutionId: number | '';
  accountNumber: string;
}

interface AddState {
  name: string;
  type: AccountType;
  openingBalance: string;
  institutionId: number | '';
  accountNumber: string;
  notes: string;
  addingInstitution: boolean;
  newInstitutionName: string;
}

const BLANK_ADD: AddState = {
  name: '', type: 'Checking', openingBalance: '0',
  institutionId: '', accountNumber: '', notes: '',
  addingInstitution: false, newInstitutionName: '',
};

function AccountsTab() {
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editState, setEditState] = useState<EditState | null>(null);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<number | null>(null);
  const [error, setError] = useState('');
  const [showAdd, setShowAdd] = useState(false);
  const [addState, setAddState] = useState<AddState>(BLANK_ADD);
  const [adding, setAdding] = useState(false);

  const accountsDirty = !saving && !adding && (
    editState !== null ||
    (showAdd && (addState.name !== '' || addState.accountNumber !== '' || addState.notes !== '' || addState.openingBalance !== '0'))
  );
  useUnsavedChanges(accountsDirty);

  useEffect(() => {
    Promise.all([getAccounts(true), getInstitutions()])
      .then(([accs, insts]) => { setAccounts(accs); setInstitutions(insts); })
      .catch(() => setError('Failed to load accounts.'))
      .finally(() => setLoading(false));
  }, []);

  const startEdit = (acc: Account) => {
    setShowAdd(false);
    setEditingId(acc.id);
    setEditState({
      name: acc.name, type: acc.type, isActive: acc.isActive,
      notes: acc.notes ?? '', institutionId: acc.institutionId ?? '',
      accountNumber: acc.accountNumber ?? '',
    });
    setError('');
  };

  const cancelEdit = () => { setEditingId(null); setEditState(null); setError(''); };

  const handleSave = async (acc: Account) => {
    if (!editState || !editState.name.trim()) { setError('Name is required.'); return; }
    setSaving(true); setError('');
    try {
      const updated = await updateAccount(acc.id, {
        name: editState.name.trim(), type: editState.type, isActive: editState.isActive,
        notes: editState.notes.trim() || undefined,
        institutionId: editState.institutionId !== '' ? editState.institutionId : undefined,
        accountNumber: editState.accountNumber.trim() || undefined,
      });
      setAccounts(prev => prev.map(a => a.id === acc.id ? updated : a));
      setEditingId(null); setEditState(null);
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to save account.');
    } finally { setSaving(false); }
  };

  const handleDelete = async (acc: Account) => {
    if (!confirm(`Delete "${acc.name}"? This will permanently delete the account and all its transactions. This cannot be undone.`)) return;
    setDeleting(acc.id); setError('');
    try {
      await deleteAccount(acc.id);
      setAccounts(prev => prev.filter(a => a.id !== acc.id));
    } catch { setError('Failed to delete account.'); }
    finally { setDeleting(null); }
  };

  const handleAddInstitution = async () => {
    if (!addState.newInstitutionName.trim()) return;
    try {
      const inst = await createInstitution(addState.newInstitutionName.trim());
      setInstitutions(prev => [...prev, inst]);
      setAddState(s => ({ ...s, institutionId: inst.id, addingInstitution: false, newInstitutionName: '' }));
    } catch { setError('Failed to create institution.'); }
  };

  const handleAdd = async () => {
    if (!addState.name.trim()) { setError('Name is required.'); return; }
    setAdding(true); setError('');
    try {
      const created = await createAccount({
        name: addState.name.trim(), type: addState.type, isActive: true,
        openingBalance: parseFloat(addState.openingBalance) || 0,
        institutionId: addState.institutionId !== '' ? addState.institutionId : undefined,
        accountNumber: addState.accountNumber.trim() || undefined,
        notes: addState.notes.trim() || undefined,
      });
      setAccounts(prev => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)));
      setAddState(BLANK_ADD); setShowAdd(false);
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to create account.');
    } finally { setAdding(false); }
  };

  if (loading) return <div className={styles.hint}>Loading…</div>;

  return (
    <div className={styles.tabSection}>
      {error && <div className={styles.error}>{error}</div>}

      {/* ── Add Account form ── */}
      {showAdd ? (
        <div className={`${styles.accountRow} ${styles.accountRowAdd}`}>
          <div className={styles.accountEditForm}>
            <div className={styles.addFormTitle}>New Account</div>
            <div className={styles.accountEditGrid}>
              <div className={styles.field}>
                <label className={styles.label}>Name *</label>
                <input className={styles.input} value={addState.name} onChange={e => setAddState(s => ({ ...s, name: e.target.value }))} autoFocus placeholder="e.g. Chase Checking" />
              </div>
              <div className={styles.field}>
                <label className={styles.label}>Type</label>
                <select className={styles.input} value={addState.type} onChange={e => setAddState(s => ({ ...s, type: e.target.value as AccountType }))}>
                  {ACCOUNT_TYPES.map(t => <option key={t} value={t}>{t === 'CreditCard' ? 'Credit Card' : t}</option>)}
                </select>
              </div>
              <div className={styles.field}>
                <label className={styles.label}>Opening Balance</label>
                <input className={styles.input} type="number" step="0.01" value={addState.openingBalance} onChange={e => setAddState(s => ({ ...s, openingBalance: e.target.value }))} />
              </div>
              <div className={styles.field}>
                <label className={styles.label}>Institution</label>
                {!addState.addingInstitution ? (
                  <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                    <select className={styles.input} value={addState.institutionId} onChange={e => setAddState(s => ({ ...s, institutionId: e.target.value === '' ? '' : Number(e.target.value) }))}>
                      <option value="">— None —</option>
                      {institutions.map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
                    </select>
                    <button type="button" className={styles.btnLink} onClick={() => setAddState(s => ({ ...s, addingInstitution: true }))}>+ New</button>
                  </div>
                ) : (
                  <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                    <input className={styles.input} value={addState.newInstitutionName} onChange={e => setAddState(s => ({ ...s, newInstitutionName: e.target.value }))} placeholder="Institution name" autoFocus />
                    <button type="button" className={styles.btnSecondary} onClick={handleAddInstitution}>Save</button>
                    <button type="button" className={styles.btnLink} onClick={() => setAddState(s => ({ ...s, addingInstitution: false, newInstitutionName: '' }))}>Cancel</button>
                  </div>
                )}
              </div>
              <div className={styles.field}>
                <label className={styles.label}>Account Number</label>
                <input className={styles.input} value={addState.accountNumber} onChange={e => setAddState(s => ({ ...s, accountNumber: e.target.value }))} placeholder="Optional" />
              </div>
              <div className={styles.field} style={{ gridColumn: '1 / -1' }}>
                <label className={styles.label}>Notes</label>
                <textarea className={styles.input} rows={2} value={addState.notes} onChange={e => setAddState(s => ({ ...s, notes: e.target.value }))} placeholder="Optional" />
              </div>
            </div>
            <div className={styles.accountEditActions}>
              <button className={styles.btnPrimary} onClick={handleAdd} disabled={adding}>{adding ? 'Creating…' : 'Create Account'}</button>
              <button className={styles.btnSecondary} onClick={() => { setShowAdd(false); setAddState(BLANK_ADD); setError(''); }}>Cancel</button>
            </div>
          </div>
        </div>
      ) : (
        <button className={styles.btnPrimary} onClick={() => { setEditingId(null); setShowAdd(true); setError(''); }}>
          + Add Account
        </button>
      )}

      {/* ── Account list ── */}
      <div className={styles.accountList}>
        {accounts.map(acc => {
          const isEditing = editingId === acc.id;
          return (
            <div key={acc.id} className={`${styles.accountRow} ${!acc.isActive ? styles.accountRowInactive : ''}`}>
              {isEditing && editState ? (
                <div className={styles.accountEditForm}>
                  <div className={styles.accountEditGrid}>
                    <div className={styles.field}>
                      <label className={styles.label}>Name *</label>
                      <input className={styles.input} value={editState.name} onChange={e => setEditState(s => s ? { ...s, name: e.target.value } : s)} autoFocus />
                    </div>
                    <div className={styles.field}>
                      <label className={styles.label}>Type</label>
                      <select className={styles.input} value={editState.type} onChange={e => setEditState(s => s ? { ...s, type: e.target.value as AccountType } : s)}>
                        {ACCOUNT_TYPES.map(t => <option key={t} value={t}>{t === 'CreditCard' ? 'Credit Card' : t}</option>)}
                      </select>
                    </div>
                    <div className={styles.field}>
                      <label className={styles.label}>Institution</label>
                      <select className={styles.input} value={editState.institutionId} onChange={e => setEditState(s => s ? { ...s, institutionId: e.target.value === '' ? '' : Number(e.target.value) } : s)}>
                        <option value="">— None —</option>
                        {institutions.map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
                      </select>
                    </div>
                    <div className={styles.field}>
                      <label className={styles.label}>Account Number</label>
                      <input className={styles.input} value={editState.accountNumber} onChange={e => setEditState(s => s ? { ...s, accountNumber: e.target.value } : s)} placeholder="Optional" />
                    </div>
                    <div className={styles.field} style={{ gridColumn: '1 / -1' }}>
                      <label className={styles.label}>Notes</label>
                      <textarea className={styles.input} rows={2} value={editState.notes} onChange={e => setEditState(s => s ? { ...s, notes: e.target.value } : s)} placeholder="Optional" />
                    </div>
                    <div className={styles.field} style={{ gridColumn: '1 / -1' }}>
                      <label className={styles.checkLabel}>
                        <input type="checkbox" checked={editState.isActive} onChange={e => setEditState(s => s ? { ...s, isActive: e.target.checked } : s)} />
                        Active
                      </label>
                    </div>
                  </div>
                  <div className={styles.accountEditActions}>
                    <button className={styles.btnPrimary} onClick={() => handleSave(acc)} disabled={saving}>{saving ? 'Saving…' : 'Save'}</button>
                    <button className={styles.btnSecondary} onClick={cancelEdit} disabled={saving}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <div className={styles.accountInfo}>
                    <div className={styles.accountName}>
                      {acc.name}
                      {!acc.isActive && <span className={styles.inactiveBadge}>inactive</span>}
                    </div>
                    <div className={styles.accountMeta}>
                      {acc.type === 'CreditCard' ? 'Credit Card' : acc.type}
                      {acc.institution?.name ? ` · ${acc.institution.name}` : ''}
                    </div>
                  </div>
                  <div className={styles.accountActions}>
                    <button className={styles.btnSecondary} onClick={() => navigate(`/accounts/${acc.id}`)}>Transactions</button>
                    <button className={styles.btnSecondary} onClick={() => startEdit(acc)}>Edit</button>
                    <button className={styles.btnDanger} onClick={() => handleDelete(acc)} disabled={deleting === acc.id}>
                      {deleting === acc.id ? 'Deleting…' : 'Delete'}
                    </button>
                  </div>
                </>
              )}
            </div>
          );
        })}
        {accounts.length === 0 && <div className={styles.hint}>No accounts found.</div>}
      </div>
    </div>
  );
}

// ── Settings page ────────────────────────────────────────────────────────────

export default function Settings() {
  usePageTitle('Settings');
  const [tab, setTab] = useState<Tab>('accounts');

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Settings</h2>
      </div>
      <div className={styles.tabs}>
        <button
          className={`${styles.tab} ${tab === 'accounts' ? styles.tabActive : ''}`}
          onClick={() => setTab('accounts')}
        >
          Accounts
        </button>
        <button
          className={`${styles.tab} ${tab === 'password' ? styles.tabActive : ''}`}
          onClick={() => setTab('password')}
        >
          Change Password
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
          className={`${styles.tab} ${tab === 'audit' ? styles.tabActive : ''}`}
          onClick={() => setTab('audit')}
        >
          Audit Log
        </button>
      </div>
      <div className={styles.tabContent}>
        {tab === 'accounts' && <AccountsTab />}
        {tab === 'password' && <ChangePasswordTab />}
        {tab === 'export' && <ExportTab />}
        {tab === 'import' && <ImportTab />}
        {tab === 'audit' && <AuditLogTab />}
      </div>
    </div>
  );
}
