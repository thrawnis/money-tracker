import { createContext, useContext } from 'react';
import type { AuthUser } from '../types/auth';

// Kept out of AuthContext.tsx deliberately: Fast Refresh only preserves state
// for modules that export components and nothing else, so the context object
// and the hook live here while the provider component lives there.
export interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  logout: () => Promise<void>;
  /** Establishes the session from a freshly issued access token. */
  signIn: (token: string) => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
