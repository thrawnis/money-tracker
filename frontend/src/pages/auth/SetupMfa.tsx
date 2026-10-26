import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { useAuth } from '../../contexts/auth-context';
import { createPasskey, defaultPasskeyName, passkeyErrorMessage, passkeysSupported } from '../../utils/webauthn';
import styles from './Login.module.css';

/**
 * First-time two-factor setup, right after sign-up (or a first sign-in
 * without any second factor). A passkey is recommended; the authenticator
 * app is the alternative. The other one can be added later in Settings.
 */
export default function SetupMfa() {
  const navigate = useNavigate();
  const { signIn } = useAuth();
  const userId = sessionStorage.getItem('mfa_setup_user_id') ?? '';
  const supported = passkeysSupported();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const setUpPasskey = async () => {
    if (!userId) { setError('Session expired. Please sign in again.'); return; }
    setError('');
    setBusy(true);
    try {
      const options = await authApi.passkeyRegisterBegin(userId);
      const attestation = await createPasskey(options);
      const res = await authApi.passkeyRegisterComplete(userId, attestation, defaultPasskeyName());
      sessionStorage.removeItem('mfa_setup_user_id');
      signIn(res.accessToken);
      navigate('/');
    } catch (err) {
      setError(passkeyErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Secure Your Account</h1>
        <p className={styles.subtitle}>
          Money Tracker asks for a second step after your password. Choose how you'd like to confirm it's you.
        </p>

        <button type="button" className={styles.btn} onClick={setUpPasskey} disabled={busy || !supported}>
          {busy ? 'Waiting for your device…' : 'Set up a passkey (recommended)'}
        </button>
        <p className={styles.hint} style={{ marginTop: 8 }}>
          {supported
            ? 'Uses your fingerprint, face, device PIN or a security key. Nothing to type, and it can\'t be phished.'
            : "This browser can't create passkeys — use an authenticator app, and add a passkey later from another device."}
        </p>
        {error && <div className={styles.error} role="alert">{error}</div>}

        <div className={styles.divider}><span>or</span></div>

        <button type="button" className={styles.linkBtn} onClick={() => navigate('/auth/setup-totp')} disabled={busy}>
          Use an authenticator app instead
        </button>
      </div>
    </div>
  );
}
