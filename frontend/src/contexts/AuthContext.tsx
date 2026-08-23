import { useEffect, useRef, useState, useCallback, type ReactNode } from 'react';
import { setAccessToken } from '../api/client';
import * as authApi from '../api/auth';
import { AuthContext } from './auth-context';
import type { AuthUser } from '../types/auth';


function parseJwt(token: string): { sub: string; email: string; role: string; tz?: string } | null {
  try {
    const payload = token.split('.')[1];
    return JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/')));
  } catch {
    return null;
  }
}

const VISIBILITY_REFRESH_THROTTLE_MS = 60_000;

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const lastRefreshAtRef = useRef(0);

  // Single place a session is established. id/email/role all come from the
  // token's own claims — callers used to hand-build an AuthUser and fill the
  // fields they didn't have with '' (Login passed an empty id, the MFA pages
  // an empty email), leaving user.id silently blank app-wide.
  const applyToken = useCallback((token: string) => {
    setAccessToken(token);
    const payload = parseJwt(token);
    if (payload) {
      setUser({
        id: payload.sub,
        email: payload.email,
        role: payload.role as 'Admin' | 'Standard',
        timeZoneId: payload.tz,
      });
    }
  }, []);

  const refreshToken = useCallback(async () => {
    try {
      const data = await authApi.refreshTokens();
      applyToken(data.accessToken);
    } catch (err: unknown) {
      // Only clear the session on an explicit 401 (invalid/expired token).
      // Network errors or 5xx during a deployment restart should not log the
      // user out — the cookie is still valid and will work once the API recovers.
      const status = (err as { response?: { status?: number } })?.response?.status;
      if (status === 401) {
        setAccessToken(null);
        setUser(null);
      }
    }
  }, [applyToken]);

  // Silent refresh on mount
  useEffect(() => {
    lastRefreshAtRef.current = Date.now();
    refreshToken().finally(() => setLoading(false));
  }, [refreshToken]);

  // Re-authenticate when the tab becomes visible again after being hidden
  // (access token is in-memory only; this restores it after long idle or tab
  // switch). Throttled: refresh tokens rotate server-side on every use, so
  // firing on every single focus churned a new DB row per tab switch and made
  // two tabs waking together race each other. The access token lives 15
  // minutes, so re-checking at most once a minute is plenty.
  useEffect(() => {
    const handleVisibility = () => {
      if (document.visibilityState !== 'visible') return;
      const now = Date.now();
      if (now - lastRefreshAtRef.current < VISIBILITY_REFRESH_THROTTLE_MS) return;
      lastRefreshAtRef.current = now;
      refreshToken();
    };
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  }, [refreshToken]);

  const logout = useCallback(async () => {
    try { await authApi.logout(); } catch { /* ignore */ }
    setAccessToken(null);
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, loading, logout, signIn: applyToken }}>
      {children}
    </AuthContext.Provider>
  );
}
