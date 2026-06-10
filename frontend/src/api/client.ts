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
  return Promise.reject(error);
});

export default api;
