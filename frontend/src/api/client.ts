import axios, { type AxiosError, type InternalAxiosRequestConfig } from 'axios';

let _accessToken: string | null = null;
export const setAccessToken = (t: string | null) => { _accessToken = t; };

const baseURL = import.meta.env.VITE_API_URL ?? '/api';

// withCredentials is required for the httpOnly refresh-token cookie and the
// short-lived auth session cookie (MFA step-2 verification) to round-trip.
const api = axios.create({ baseURL, withCredentials: true });

api.interceptors.request.use(cfg => {
  if (_accessToken) cfg.headers.Authorization = `Bearer ${_accessToken}`;
  return cfg;
});

// ── 401 → silent refresh → retry once ────────────────────────────────────────
// Single-flight: concurrent 401s share one refresh request.
let refreshPromise: Promise<string> | null = null;

function refreshAccessToken(): Promise<string> {
  refreshPromise ??= axios
    .post<{ accessToken: string }>(`${baseURL}/auth/refresh`, {}, { withCredentials: true })
    .then(res => {
      setAccessToken(res.data.accessToken);
      return res.data.accessToken;
    })
    .finally(() => { refreshPromise = null; });
  return refreshPromise;
}

api.interceptors.response.use(undefined, async (error: AxiosError) => {
  const original = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;

  const isAuthCall = original?.url?.includes('/auth/') ?? false;
  if (error.response?.status === 401 && original && !original._retried && !isAuthCall) {
    original._retried = true;
    try {
      const token = await refreshAccessToken();
      original.headers.Authorization = `Bearer ${token}`;
      return api(original);
    } catch {
      // Refresh cookie is gone or revoked — session is over.
      setAccessToken(null);
      if (!window.location.pathname.startsWith('/auth/')) {
        window.location.assign('/auth/login');
      }
    }
  }
  // 428: the API is holding this user at the mandatory timezone picker. It
  // normally can't happen — AppShell reads the same "tz" claim and shows the
  // picker before any of this renders — but a tab left open across the deploy
  // that introduced the requirement would otherwise sit there failing every
  // call with an unexplained error. Reloading re-runs the silent refresh and
  // lands on the picker (or straight into the app, if another tab already
  // answered it).
  if (error.response?.status === 428) {
    window.location.reload();
  }

  return Promise.reject(error);
});

export default api;
