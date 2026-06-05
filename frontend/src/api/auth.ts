import api from './client';
import type { TokenResponse, LoginStepOneResponse } from '../types/auth';

export const register = (email: string, password: string) =>
  api.post<TokenResponse>('/auth/register', { email, password }).then(r => r.data);

export const login = (email: string, password: string) =>
  api.post<LoginStepOneResponse>('/auth/login', { email, password }).then(r => r.data);

export const verifyTotp = (code: string) =>
  api.post<TokenResponse>('/auth/totp/verify', { code }).then(r => r.data);

export const setupTotp = () =>
  api.post<{ sharedKey: string; authenticatorUri: string }>('/auth/totp/setup').then(r => r.data);

export const enrollTotp = (code: string) =>
  api.post('/auth/totp/enroll', { code });

export const refreshTokens = () =>
  api.post<TokenResponse>('/auth/refresh', {}, { withCredentials: true }).then(r => r.data);

export const logout = () =>
  api.post('/auth/logout', {}, { withCredentials: true });
