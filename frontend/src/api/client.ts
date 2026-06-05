import axios from 'axios';

let _accessToken: string | null = null;
export const setAccessToken = (t: string | null) => { _accessToken = t; };

const api = axios.create({ baseURL: import.meta.env.VITE_API_URL ?? '/api' });
api.interceptors.request.use(cfg => {
  if (_accessToken) cfg.headers.Authorization = `Bearer ${_accessToken}`;
  return cfg;
});
export default api;
