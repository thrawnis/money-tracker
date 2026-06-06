import api from './client';
import type { TokenResponse, LoginStepOneResponse } from '../types/auth';

export const register = (email: string, password: string) =>
  api.post<{ userId: string; requiresMfaSetup: true }>('/auth/register', { email, password }).then(r => r.data);

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

export const refreshTokens = () =>
  api.post<TokenResponse>('/auth/refresh', {}, { withCredentials: true }).then(r => r.data);

export const logout = () =>
  api.post('/auth/logout', {}, { withCredentials: true });
