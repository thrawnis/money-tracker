import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { getImportDrafts, deleteImportDraft, type ImportDraftSummary } from '../api/import';
import styles from './ImportDraftBanner.module.css';

// Global, hard-to-miss notice that an import was started but not finished —
// the upload and the user's review choices are safely staged server-side
// (see ImportDraft), but nothing is imported until they resume and commit.
export default function ImportDraftBanner() {
  const location = useLocation();
  const navigate = useNavigate();
  const [drafts, setDrafts] = useState<ImportDraftSummary[]>([]);
  const [dismissedIds, setDismissedIds] = useState<Set<number>>(new Set());

  useEffect(() => {
    getImportDrafts().then(setDrafts).catch(() => {});
  }, [location.pathname]);

  const visible = drafts.filter(d => !dismissedIds.has(d.id));
  if (visible.length === 0) return null;

  const handleResume = (d: ImportDraftSummary) => {
    navigate('/settings', { state: { tab: 'import', resumeDraftId: d.id } });
  };

  const handleDiscard = async (d: ImportDraftSummary) => {
    const msg = d.fromBankSync
      ? `Discard the ${d.rowCount} synced transaction(s) for "${d.accountName}"? Nothing is lost — the next bank sync will fetch them again.`
      : `Discard the unfinished import for "${d.accountName}" (${d.fileName})? This cannot be undone.`;
    if (!confirm(msg)) return;
    try {
      await deleteImportDraft(d.id);
      setDrafts(prev => prev.filter(x => x.id !== d.id));
    } catch {
      // leave it in the list; the user can retry
    }
  };

  return (
    <div className={styles.banner}>
      {visible.map(d => (
        <div key={d.id} className={styles.row}>
          <span className={styles.icon}>⚠</span>
          <span className={styles.text}>
            {d.fromBankSync ? (
              <>
                Bank sync found <strong>{d.rowCount} new transaction{d.rowCount === 1 ? '' : 's'}</strong> for{' '}
                <strong>{d.accountName}</strong> waiting for your review.
              </>
            ) : (
              <>
                Unfinished import for <strong>{d.accountName}</strong> — {d.fileName} ({d.rowCount} row{d.rowCount === 1 ? '' : 's'}),
                last touched {new Date(d.updatedAt).toLocaleString()}.
              </>
            )}
          </span>
          <button className={styles.resumeBtn} onClick={() => handleResume(d)}>{d.fromBankSync ? 'Review' : 'Resume'}</button>
          <button className={styles.discardBtn} onClick={() => handleDiscard(d)}>Discard</button>
          <button className={styles.hideBtn} onClick={() => setDismissedIds(prev => new Set(prev).add(d.id))} title="Hide for this visit only">
            ✕
          </button>
        </div>
      ))}
    </div>
  );
}
