import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { getImportDrafts, deleteImportDraft, type ImportDraftSummary } from '../api/import';
import styles from './ImportDraftBanner.module.css';

// Global, hard-to-miss notice of imports waiting for the user: one summary
// line for all bank-sync reviews, and one line per unfinished file import —
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
    const msg = `Discard the unfinished import for "${d.accountName}" (${d.fileName})? This cannot be undone.`;
    if (!confirm(msg)) return;
    try {
      await deleteImportDraft(d.id);
      setDrafts(prev => prev.filter(x => x.id !== d.id));
    } catch {
      // leave it in the list; the user can retry
    }
  };

  // Bank-sync reviews are summed into one line; each file import keeps its own.
  const synced = visible.filter(d => d.fromBankSync);
  const files = visible.filter(d => !d.fromBankSync);
  const syncedTx = synced.reduce((n, d) => n + d.rowCount, 0);
  const oldestDays = Math.max(0, ...synced.map(backlogDays));

  return (
    <div className={styles.banner}>
      {synced.length > 0 && (
        <div className={styles.row}>
          <span className={styles.icon}>⚠</span>
          <span className={styles.text}>
            Bank sync: <strong>{syncedTx} new transaction{syncedTx === 1 ? '' : 's'}</strong> in{' '}
            <strong>{synced.length} account{synced.length === 1 ? '' : 's'}</strong> waiting for your review.
            {oldestDays >= 60 && (
              <> <strong>The oldest hasn't been reviewed for {oldestDays} days</strong> — syncs only reach back 90 days,
                so please review soon or older transactions will need importing from a bank file.</>
            )}
          </span>
          <button className={styles.resumeBtn} onClick={() => navigate('/bank-sync')}>Review</button>
          <button
            className={styles.hideBtn}
            onClick={() => setDismissedIds(prev => new Set([...prev, ...synced.map(d => d.id)]))}
            title="Hide for this visit only"
          >
            ✕
          </button>
        </div>
      )}
      {files.map(d => (
        <div key={d.id} className={styles.row}>
          <span className={styles.icon}>⚠</span>
          <span className={styles.text}>
            Unfinished import for <strong>{d.accountName}</strong> — {d.fileName} ({d.rowCount} row{d.rowCount === 1 ? '' : 's'}),
            last touched {new Date(d.updatedAt).toLocaleString()}.
          </span>
          <button className={styles.resumeBtn} onClick={() => handleResume(d)}>Resume</button>
          <button className={styles.discardBtn} onClick={() => handleDiscard(d)}>Discard</button>
          <button className={styles.hideBtn} onClick={() => setDismissedIds(prev => new Set(prev).add(d.id))} title="Hide for this visit only">
            ✕
          </button>
        </div>
      ))}
    </div>
  );
}
