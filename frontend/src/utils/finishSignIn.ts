import type { NavigateFunction } from 'react-router-dom';

/**
 * Every sign-in path ends here: either the server issued a token (sign in and
 * go to the app), or it wants the address verified first (go enter the emailed
 * code; the token comes from that step).
 */
export function finishSignIn(
  res: { accessToken?: string; requiresEmailVerification?: boolean; email?: string },
  signIn: (accessToken: string) => void,
  navigate: NavigateFunction,
): boolean {
  if (res.requiresEmailVerification) {
    sessionStorage.setItem('verify_email', res.email ?? '');
    navigate('/auth/verify-email');
    return true;
  }
  if (!res.accessToken) return false;
  signIn(res.accessToken);
  navigate('/');
  return true;
}
