import { useState, type FormEvent } from 'react';
import api from '../api/client';
import styles from './ExportModal.module.css';

interface Props {
  onClose: () => void;
}

type Step = 'auth' | 'format';
type AuthMethod = 'password' | 'totp';
type ExportFormat = 'qif' | 'ofx' | 'csv' | 'xlsx' | 'json';

interface FormatInfo {
  id: ExportFormat;
  label: string;
  preferred?: boolean;
  description: string;
  pros: string;
  cons: string;
}

// JSON first — it's the only format that fully round-trips through this
// app's own importer, so it's the one to reach for when backing up or
// restoring data (see the "Preferred" badge below).
const FORMATS: FormatInfo[] = [
  {
    id: 'json',
    label: 'JSON',
    preferred: true,
    description: "This app's own format.",
    pros: 'Preserves everything — split transactions, subcategories, transfer links. This app can re-import it exactly as exported.',
    cons: "Not readable in Quicken/Money or a spreadsheet. Meant for backing up or moving data between Money Tracker instances, not for viewing elsewhere.",
  },
  {
    id: 'qif',
    label: 'QIF',
    description: 'A classic finance-software interchange format.',
    pros: 'Opens in Quicken, Microsoft Money, and similar apps. This app can also re-import QIF.',
    cons: "Split transactions aren't preserved as splits (exported as a single line). Transfers are re-matched by date/amount/memo on import, not an exact link.",
  },
  {
    id: 'ofx',
    label: 'OFX',
    description: 'The standard bank/credit-card statement format.',
    pros: 'Widely supported by banks and financial software for statement-style imports.',
    cons: 'No categories, splits, or transfer info — just raw transaction lines. Not re-importable into this app.',
  },
  {
    id: 'csv',
    label: 'CSV',
    description: 'A plain spreadsheet format.',
    pros: 'Opens in Excel, Google Sheets, or Numbers for manual filtering and analysis. This app can also re-import CSV.',
    cons: 'One row per transaction — a split transaction collapses to a single category/amount, and category hierarchy is a flat Category/SubCategory column.',
  },
  {
    id: 'xlsx',
    label: 'XLSX',
    description: 'An Excel workbook with separate Accounts and Transactions sheets.',
    pros: 'Nicely formatted for spreadsheet review without a separate CSV import step.',
    cons: 'Not re-importable into this app; larger file than CSV for the same data.',
  },
];

export default function ExportModal({ onClose }: Props) {
  const [step, setStep] = useState<Step>('auth');
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
      const ext = format;
      const url = URL.createObjectURL(res.data as Blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `export.${ext}`;
      a.click();
      URL.revokeObjectURL(url);
      onClose();
    } catch {
      setDownloadError('Export failed. Your export token may have expired.');
    }
  };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={`${styles.modal} ${step === 'format' ? styles.modalWide : ''}`}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>Export Data</h2>
          <button className={styles.closeBtn} onClick={onClose}>x</button>
        </div>

        {step === 'auth' && (
          <form onSubmit={handleAuth} className={styles.modalBody}>
            <p className={styles.authHint}>Confirm your identity to export data.</p>
            <div className={styles.authToggle}>
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
                  autoFocus
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
                  autoFocus
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
          <div className={styles.modalBody}>
            <p className={styles.authHint}>Choose an export format:</p>
            <div className={styles.formatList}>
              {FORMATS.map(f => (
                <div key={f.id} className={styles.formatCard}>
                  <div className={styles.formatCardHeader}>
                    <span className={styles.formatCardLabel}>{f.label}</span>
                    {f.preferred && <span className={styles.formatCardBadge}>Preferred for backup / restore</span>}
                  </div>
                  <p className={styles.formatCardDesc}>{f.description}</p>
                  <p className={styles.formatCardProCon}><strong>Pros:</strong> {f.pros}</p>
                  <p className={styles.formatCardProCon}><strong>Cons:</strong> {f.cons}</p>
                  <button className={styles.formatCardBtn} onClick={() => handleExport(f.id)}>
                    Download {f.label}
                  </button>
                </div>
              ))}
            </div>
            {downloadError && <div className={styles.error}>{downloadError}</div>}
          </div>
        )}
      </div>
    </div>
  );
}
