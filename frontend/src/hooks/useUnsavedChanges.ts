import { useEffect } from 'react';
import { useBlocker } from 'react-router-dom';
import { confirmDialog } from '../utils/dialogs';

const MSG = 'You have unsaved changes. Leave anyway?';

export function useUnsavedChanges(isDirty: boolean) {
  // Block in-app navigation (sidebar links, back button, navigate() calls)
  const blocker = useBlocker(isDirty);

  useEffect(() => {
    if (blocker.state !== 'blocked') return;
    void confirmDialog(MSG, { confirmLabel: 'Leave', cancelLabel: 'Stay', danger: true })
      .then(ok => { if (ok) blocker.proceed?.(); else blocker.reset?.(); });
  }, [blocker]);

  // Block browser refresh / tab close (browsers only allow their own dialog here)
  useEffect(() => {
    if (!isDirty) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);
}
