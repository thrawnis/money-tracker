import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { useAuth } from '../../contexts/auth-context';
import { getPasskey, passkeyErrorMessage, passkeysSupported } from '../../utils/webauthn';
import styles from './Login.module.css';

type ApiError = { response?: { data?: { message?: string } | string } };
const apiMessage = (err: unknown, fallback: string) => {
  const data = (err as ApiError)?.response?.data;
  return typeof data === 'string' ? data : data?.message ?? fallback;
};

/**
 * Sign-in step 2. A passkey is the default; the authenticator app code is
 * the alternative when the account has it set up. Either one completes
 * sign-in.
 */
export default function MfaChallenge() {
  const navigate = useNavigate();
  const { signIn } = useAuth();
  const userId = sessionStorage.getItem('mfa_login_user_id') ?? '';
  const methods = JSON.parse(sessionStorage.getItem('mfa_login_methods') ?? '{"passkey":false,"totp":true}') as
    { passkey: boolean; totp: boolean };
  const canPasskey = methods.passkey && passkeysSupported();
  const [mode, setMode] = useState<'passkey' | 'totp'>(canPasskey || !methods.totp ? 'passkey' : 'totp');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const finish = (accessToken: string) => {
    sessionStorage.removeItem('mfa_login_user_id');
    sessionStorage.removeItem('mfa_login_methods');
    signIn(accessToken);
    navigate('/');
  };

  const usePasskey = async () => {
    if (!userId) { setError('Session expired. Please sign in again.'); return; }
    setError('');
    setBusy(true);
    try {
      const options = await authApi.passkeyLoginBegin(userId);
      const assertion = await getPasskey(options);
      const res = await authApi.passkeyLoginComplete(userId, assertion);
      finish(res.accessToken);
    } catch (err) {
      setError(passkeyErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const submitCode = async (e: FormEvent) => {
    e.preventDefault();
    if (code.length !== 6) { setError('Please enter a 6-digit code'); return; }
    if (!userId) { setError('Session expired. Please sign in again.'); return; }
    setError('');
    setBusy(true);
    try {
      const res = await authApi.verifyTotp(userId, code);
      finish(res.accessToken);
    } catch (err) {
      setError(apiMessage(err, 'Invalid code. Please try again.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Two-Factor Authentication</h1>

        {mode === 'passkey' ? (
          <>
            {canPasskey ? (
              <>
                <p className={styles.subtitle}>Confirm it's you with your passkey — your fingerprint, face, PIN or security key.</p>
                <button type="button" className={styles.btn} onClick={usePasskey} disabled={busy} autoFocus>
                  {busy ? 'Waiting for your passkey…' : 'Sign in with passkey'}
                </button>
              </>
            ) : (
              <p className={styles.subtitle}>
                {methods.passkey
                  ? "This browser can't use passkeys."
                  : 'No passkey is set up for this account.'}
                {methods.totp ? ' Use your authenticator app instead.' : ' Try another browser or device.'}
              </p>
            )}
            {error && <div className={styles.error} role="alert">{error}</div>}
            {methods.totp && (
              <p className={styles.hint} style={{ textAlign: "center" }}>
                <button type="button" className={styles.linkBtn} onClick={() => { setMode('totp'); setError(''); }}>
                  Use authenticator app code instead
                </button>
              </p>
            )}
          </>
        ) : (
          <>
            <p className={styles.subtitle}>Enter the 6-digit code from your authenticator app.</p>
            <form onSubmit={submitCode} noValidate>
              <div className={styles.field}>
                <label className={styles.label} htmlFor="totp">Authentication Code</label>
                <input
                  id="totp"
                  type="text"
                  inputMode="numeric"
                  pattern="[0-9]{6}"
                  maxLength={6}
                  className={styles.input}
                  value={code}
                  onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
                  autoComplete="one-time-code"
                  autoFocus
                  placeholder="000000"
                />
                {error && <div className={styles.error} role="alert">{error}</div>}
              </div>
              <button type="submit" className={styles.btn} disabled={busy}>
                {busy ? 'Verifying…' : 'Verify'}
              </button>
            </form>
            {methods.passkey && (
              <p className={styles.hint} style={{ textAlign: "center" }}>
                <button type="button" className={styles.linkBtn} onClick={() => { setMode('passkey'); setError(''); }}>
                  Use a passkey instead
                </button>
              </p>
            )}
          </>
        )}
      </div>
    </div>
  );
}
