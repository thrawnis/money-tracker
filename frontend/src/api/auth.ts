import api from './client';
import type { TokenResponse, LoginStepOneResponse } from '../types/auth';

export const register = (email: string, password: string, timeZoneId: string) =>
  api.post<{ userId: string; requiresMfaSetup: true }>('/auth/register', { email, password, timeZoneId }).then(r => r.data);

export const login = (email: string, password: string, rememberMe = false) =>
  api.post<LoginStepOneResponse>('/auth/login', { email, password, rememberMe }).then(r => r.data);

export const verifyTotp = (userId: string, code: string) =>
  api.post<TokenResponse>('/auth/mfa/totp/verify', { userId, code }).then(r => r.data);

export const setupTotp = (userId: string) =>
  api.post<{ sharedKey: string; authenticatorUri: string }>('/auth/mfa/totp/setup', userId, {
    headers: { 'Content-Type': 'application/json' },
  }).then(r => r.data);

export const enrollTotp = (userId: string, code: string) =>
  api.post<TokenResponse>('/auth/mfa/totp/enroll', { userId, code }).then(r => r.data);

// Already-logged-in reset flow (Settings → Security): invalidates the current
// authenticator immediately and issues a fresh key, re-authenticated via the
// same exportToken used for Export/account-deletion rather than the pre-login
// session flag setupTotp/enrollTotp rely on.
export const resetTotpSetup = (exportToken: string) =>
  api.post<{ sharedKey: string; authenticatorUri: string }>('/auth/mfa/totp/reset-setup', { exportToken }).then(r => r.data);

export const resetTotpEnroll = (code: string) =>
  api.post<{ mfaEnrolled: boolean }>('/auth/mfa/totp/reset-enroll', { code }).then(r => r.data);

export interface PasskeyInfo { id: number; name?: string | null; createdAt: string; lastUsedAt: string }

// Live server-side enrollment state (the auth context's copy is only as fresh
// as the last login and goes stale mid-reset).
export const getMfaStatus = () =>
  api.get<{ mfaEnrolled: boolean; totpEnrolled: boolean; passkeys: PasskeyInfo[] }>('/auth/mfa/status').then(r => r.data);

// ── Passkeys ──
// Sign-in, step 2 (after the password): options for navigator.credentials.get().
const jsonBody = { headers: { 'Content-Type': 'application/json' } };
export const passkeyLoginBegin = (userId: string) =>
  api.post<Record<string, unknown>>('/auth/passkey/login/begin', JSON.stringify(userId), jsonBody).then(r => r.data);
export const passkeyLoginComplete = (userId: string, assertionResponseJson: string) =>
  api.post<TokenResponse>('/auth/passkey/login/complete', { userId, assertionResponseJson }).then(r => r.data);

// First-time setup (sign-up / first login without any second factor).
export const passkeyRegisterBegin = (userId: string) =>
  api.post<Record<string, unknown>>('/auth/passkey/register/begin', JSON.stringify(userId), jsonBody).then(r => r.data);
export const passkeyRegisterComplete = (userId: string, attestationResponseJson: string, deviceName?: string) =>
  api.post<TokenResponse>('/auth/passkey/register/complete', { userId, attestationResponseJson, deviceName }).then(r => r.data);

// Settings → Security (signed in; changes need a fresh identity check).
export const passkeyAddBegin = (exportToken: string) =>
  api.post<Record<string, unknown>>('/auth/passkeys/add/begin', { exportToken }).then(r => r.data);
export const passkeyAddComplete = (attestationResponseJson: string, deviceName?: string) =>
  api.post<{ id: number }>('/auth/passkeys/add/complete', { attestationResponseJson, deviceName }).then(r => r.data);
export const renamePasskey = (id: number, name: string) =>
  api.put(`/auth/passkeys/${id}`, { name });
export const removePasskey = (id: number, exportToken: string) =>
  api.post(`/auth/passkeys/${id}/remove`, { exportToken });
export const removeTotp = (exportToken: string) =>
  api.post('/auth/mfa/totp/remove', { exportToken });

// withCredentials is set globally on the client (required for the refresh
// cookie and the MFA-step session cookie on all auth calls)
export const refreshTokens = () =>
  api.post<TokenResponse>('/auth/refresh').then(r => r.data);

export const logout = () =>
  api.post('/auth/logout');
