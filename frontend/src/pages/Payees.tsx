import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getPayees, updatePayee, deletePayee } from '../api/payees';
import { getCategories } from '../api/categories';
import { usePageTitle } from '../hooks/usePageTitle';
import InfoIcon from '../components/InfoIcon';
import type { Payee, Category } from '../types';
import styles from './Payees.module.css';

const FIRST_TX_INFO = 'Date of the earliest transaction ever assigned to this payee, including future-dated transactions.';
const LAST_TX_INFO = 'Date of the latest transaction ever assigned to this payee, including future-dated transactions.';
const COUNT_INFO = 'Total number of transactions assigned to this payee, including future-dated transactions.';

type ApiError = { response?: { data?: { message?: string } } };
const apiMsg = (err: unknown, fallback: string) =>
  (err as ApiError)?.response?.data?.message ?? fallback;

function fmt(d?: string) {
  if (!d) return null;
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

function flatCats(categories: Category[]): { id: number; label: string }[] {
  const out: { id: number; label: string }[] = [];
  for (const c of categories) {
    out.push({ id: c.id, label: c.name });
    for (const s of c.subCategories ?? []) {
      out.push({ id: s.id, label: `${c.name}: ${s.name}` });
    }
  }
  return out;
}

export default function Payees() {
  usePageTitle('Payees');
  const navigate = useNavigate();
  const [payees, setPayees] = useState<Payee[]>([]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [renamingId, setRenamingId] = useState<number | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [editingCatFor, setEditingCatFor] = useState<number | null>(null);
  const [catValue, setCatValue] = useState<string>('');

  const load = () =>
    Promise.all([getPayees(), getCategories()])
      .then(([pays, cats]) => { setPayees(pays); setCategories(cats); })
      .catch(() => setError('Failed to load.'))
      .finally(() => setLoading(false));

  useEffect(() => { load(); }, []);

  const handleRename = async (id: number) => {
    if (!renameValue.trim()) return;
    const payee = payees.find(p => p.id === id);
    if (!confirm(`Rename "${payee?.name}" to "${renameValue.trim()}"?`)) return;
    try {
      await updatePayee(id, { name: renameValue.trim() });
      setPayees(prev => prev.map(p => p.id === id ? { ...p, name: renameValue.trim() } : p));
      setRenamingId(null);
    } catch (err) { setError(apiMsg(err, 'Failed to rename payee.')); }
  };

  const handleSetCategory = async (id: number) => {
    const payee = payees.find(p => p.id === id);
    const targetId = catValue ? Number(catValue) : null;
    const flat = flatCats(categories);
    const catName = targetId ? flat.find(c => c.id === targetId)?.label : 'none';
    if (!confirm(`Set default category for "${payee?.name}" to ${catName ? `"${catName}"` : 'none'}?`)) return;
    try {
      await updatePayee(id, { defaultCategoryId: targetId });
      setPayees(prev => prev.map(p => p.id === id ? { ...p, defaultCategoryId: targetId ?? undefined } : p));
      setEditingCatFor(null);
    } catch { setError('Failed to update default category.'); }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete payee "${name}"? This cannot be undone.\n\nNote: transactions using this payee will retain their payee name.`)) return;
    try {
      await deletePayee(id);
      setPayees(prev => prev.filter(p => p.id !== id));
    } catch { setError('Failed to delete payee.'); }
  };

  const flat = flatCats(categories);

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>;

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Payees</h2>
        <span className={styles.hint}>Double-click a payee to view all its transactions</span>
      </div>

      {error && <div className={styles.errorMsg}>{error}</div>}

      <div className={styles.tableWrapper}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Payee</th>
              <th>Default Category</th>
              <th>
                First Transaction
                <InfoIcon text={FIRST_TX_INFO} />
              </th>
              <th>
                Last Transaction
                <InfoIcon text={LAST_TX_INFO} />
              </th>
              <th>
                # Transactions
                <InfoIcon text={COUNT_INFO} />
              </th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {payees.length === 0 && (
              <tr><td colSpan={6} className={styles.empty}>No payees yet.</td></tr>
            )}
            {payees.map(payee => {
              const defCatLabel = payee.defaultCategoryId
                ? flat.find(c => c.id === payee.defaultCategoryId)?.label ?? '—'
                : '—';
              return (
                <tr
                  key={payee.id}
                  className={styles.row}
                  onDoubleClick={() => navigate(`/transactions?payeeId=${payee.id}&label=${encodeURIComponent(payee.name)}`)}
                >
                  <td>
                    {renamingId === payee.id ? (
                      <div className={styles.inlineEdit}>
                        <input
                          className={styles.input}
                          value={renameValue}
                          onChange={e => setRenameValue(e.target.value)}
                          onKeyDown={e => { if (e.key === 'Enter') handleRename(payee.id); if (e.key === 'Escape') setRenamingId(null); }}
                          autoFocus
                        />
                        <button className={styles.btnSm} onClick={() => handleRename(payee.id)}>Save</button>
                        <button className={styles.btnSmSecondary} onClick={() => setRenamingId(null)}>Cancel</button>
                      </div>
                    ) : (
                      <span className={styles.payeeName}>{payee.name}</span>
                    )}
                  </td>
                  <td>
                    {editingCatFor === payee.id ? (
                      <div className={styles.inlineEdit}>
                        <select
                          className={styles.select}
                          value={catValue}
                          onChange={e => setCatValue(e.target.value)}
                        >
                          <option value="">— None —</option>
                          {flat.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
                        </select>
                        <button className={styles.btnSm} onClick={() => handleSetCategory(payee.id)}>Save</button>
                        <button className={styles.btnSmSecondary} onClick={() => setEditingCatFor(null)}>Cancel</button>
                      </div>
                    ) : (
                      <span className={styles.catLabel}>{defCatLabel}</span>
                    )}
                  </td>
                  <td className={styles.lastUsed}>{fmt(payee.firstUsed) ?? '—'}</td>
                  <td className={styles.lastUsed}>{fmt(payee.lastUsed) ?? '—'}</td>
                  <td className={styles.lastUsed}>{payee.transactionCount ?? 0}</td>
                  <td>
                    <div className={styles.rowActions}>
                      <button className={styles.btnSm} onClick={() => { setRenamingId(payee.id); setRenameValue(payee.name); setEditingCatFor(null); }}>Rename</button>
                      <button className={styles.btnSm} onClick={() => { setEditingCatFor(payee.id); setCatValue(payee.defaultCategoryId ? String(payee.defaultCategoryId) : ''); setRenamingId(null); }}>Category</button>
                      <button className={styles.btnSmDelete} onClick={() => handleDelete(payee.id, payee.name)}>Delete</button>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
