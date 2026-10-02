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
  // When the list was fetched — the "now" that backlog ages are measured from.
  const [loadedAt, setLoadedAt] = useState(0);

  useEffect(() => {
    getImportDrafts().then(d => { setDrafts(d); setLoadedAt(Date.now()); }).catch(() => {});
  }, [location.pathname]);

  const visible = drafts.filter(d => !dismissedIds.has(d.id));

  // Each sync reaches back at most 90 days from the last approval, so an
  // unreviewed bank-sync backlog gets a warning well before then.
  const backlogDays = (d: ImportDraftSummary) =>
    d.syncPendingSince
      ? Math.floor((loadedAt - new Date(d.syncPendingSince + 'T00:00:00').getTime()) / 864e5)
      : 0;
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
                {backlogDays(d) >= 60 && (
                  <> <strong>Not reviewed for {backlogDays(d)} days</strong> — syncs only reach back 90 days, so
                    please review soon or older transactions will need importing from a bank file.</>
                )}
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
