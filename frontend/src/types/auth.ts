export interface TokenResponse {
  accessToken: string;
  accessTokenExpiry: string;
  role: 'Admin' | 'Standard';
  mfaEnrolled: boolean;
}

export interface LoginStepOneResponse {
  requiresMfa?: boolean;
  requiresMfaSetup?: boolean;
  userId?: string;
}

export interface TotpSetupResponse {
  sharedKey: string;
  authenticatorUri: string;
}

export interface AuthUser {
  id: string;
  email: string;
  role: 'Admin' | 'Standard';
}
