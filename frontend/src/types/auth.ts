export interface TokenResponse {
  accessToken: string;
  accessTokenExpiry: string;
  role: 'Admin' | 'Standard';
  mfaEnrolled: boolean;
}

// /auth/login either asks for a second factor, or — when MFA is bypassed, as
// for the demo account — returns a full TokenResponse straight away.
export interface LoginStepOneResponse {
  requiresMfa?: boolean;
  requiresMfaSetup?: boolean;
  userId?: string;
  accessToken?: string;
  accessTokenExpiry?: string;
  role?: 'Admin' | 'Standard';
  mfaEnrolled?: boolean;
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
