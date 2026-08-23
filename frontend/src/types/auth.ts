export interface TokenResponse {
  accessToken: string;
  accessTokenExpiry: string;
  role: 'Admin' | 'Standard';
  mfaEnrolled: boolean;
  /** True for accounts predating the timezone requirement — the app holds them
   *  at a mandatory picker, and the API refuses everything else meanwhile. */
  requiresTimeZone?: boolean;
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
  requiresTimeZone?: boolean;
}

export interface TotpSetupResponse {
  sharedKey: string;
  authenticatorUri: string;
}

export interface AuthUser {
  id: string;
  email: string;
  role: 'Admin' | 'Standard';
  /** IANA id from the token's "tz" claim. Absent on accounts that predate the
   *  timezone requirement — those are held at the mandatory picker. */
  timeZoneId?: string;
}
