import { useState, type FormEvent } from 'react';
import api from '../api/client';
import styles from './ExportModal.module.css';

interface Props {
  onClose: () => void;
}

type Step = 'auth' | 'format';
type AuthMethod = 'password' | 'totp';
type ExportFormat = 'qif' | 'ofx' | 'csv' | 'xlsx' | 'json';

const FORMATS: { id: ExportFormat; label: string }[] = [
  { id: 'qif', label: 'QIF' },
  { id: 'ofx', label: 'OFX' },
  { id: 'csv', label: 'CSV' },
  { id: 'xlsx', label: 'XLSX' },
  { id: 'json', label: 'JSON' },
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
      <div className={styles.modal}>
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
            <div className={styles.formatBtns}>
              {FORMATS.map(f => (
                <button key={f.id} className={styles.formatBtn} onClick={() => handleExport(f.id)}>
                  {f.label}
                </button>
              ))}
            </div>
            {downloadError && <div className={styles.error}>{downloadError}</div>}
          </div>
        )}
      </div>
    </div>
  );
}
