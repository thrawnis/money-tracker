import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getCategories, createCategory, updateCategory, deleteCategory } from '../api/categories';
import { usePageTitle } from '../hooks/usePageTitle';
import InfoIcon from '../components/InfoIcon';
import type { Category } from '../types';
import styles from './Categories.module.css';

const FIRST_TX_INFO = 'Date of the earliest transaction ever assigned to this category, including future-dated transactions.';
const LAST_TX_INFO = 'Date of the latest transaction ever assigned to this category, including future-dated transactions.';
const COUNT_INFO = 'Total number of transactions assigned to this category, including future-dated transactions.';

type ApiError = { response?: { data?: { message?: string } } };
const apiMsg = (err: unknown, fallback: string) =>
  (err as ApiError)?.response?.data?.message ?? fallback;

function fmt(d?: string) {
  if (!d) return null;
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

function Stats({ item }: { item: Category }) {
  if (!item.transactionCount) return null;
  return (
    <span className={styles.stats}>
      <span>First: {fmt(item.firstUsed)}</span>
      <span>Last: {fmt(item.lastUsed)}</span>
      <span>{item.transactionCount} tx</span>
    </span>
  );
}

export default function Categories() {
  usePageTitle('Categories');
  const navigate = useNavigate();
  const [categories, setCategories] = useState<Category[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Add top-level category
  const [newCatName, setNewCatName] = useState('');
  const [addingCat, setAddingCat] = useState(false);

  // Add subcategory
  const [addingSubFor, setAddingSubFor] = useState<number | null>(null);
  const [newSubName, setNewSubName] = useState('');

  // Rename
  const [renamingId, setRenamingId] = useState<number | null>(null);
  const [renameValue, setRenameValue] = useState('');

  // Move subcategory
  const [movingId, setMovingId] = useState<number | null>(null);
  const [moveTarget, setMoveTarget] = useState<string>(''); // '' = make top-level, or parent id string

  const load = () =>
    getCategories()
      .then(setCategories)
      .catch(() => setError('Failed to load categories.'))
      .finally(() => setLoading(false));

  useEffect(() => { load(); }, []);

  const handleAddCategory = async () => {
    if (!newCatName.trim()) return;
    if (!confirm(`Create category "${newCatName.trim()}"?`)) return;
    try {
      const created = await createCategory({ name: newCatName.trim() });
      setCategories(prev => [...prev, { ...created, subCategories: [] }]);
      setNewCatName('');
      setAddingCat(false);
    } catch (err) { setError(apiMsg(err, 'Failed to create category.')); }
  };

  const handleAddSub = async (parentId: number) => {
    if (!newSubName.trim()) return;
    const parent = categories.find(c => c.id === parentId);
    if (!confirm(`Create subcategory "${newSubName.trim()}" under "${parent?.name}"?`)) return;
    try {
      const created = await createCategory({ name: newSubName.trim(), parentId });
      setCategories(prev => prev.map(c =>
        c.id === parentId ? { ...c, subCategories: [...(c.subCategories ?? []), created] } : c
      ));
      setNewSubName('');
      setAddingSubFor(null);
    } catch (err) { setError(apiMsg(err, 'Failed to create subcategory.')); }
  };

  const handleRename = async (id: number, isSubcat: boolean) => {
    if (!renameValue.trim()) return;
    const cat = isSubcat
      ? categories.flatMap(c => c.subCategories ?? []).find(s => s.id === id)
      : categories.find(c => c.id === id);
    if (!confirm(`Rename "${cat?.name}" to "${renameValue.trim()}"?`)) return;
    try {
      await updateCategory(id, { name: renameValue.trim() });
      setCategories(prev => prev.map(c => {
        if (!isSubcat && c.id === id) return { ...c, name: renameValue.trim() };
        return { ...c, subCategories: c.subCategories?.map(s => s.id === id ? { ...s, name: renameValue.trim() } : s) };
      }));
      setRenamingId(null);
    } catch (err) { setError(apiMsg(err, 'Failed to rename.')); }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
    try {
      await deleteCategory(id);
      setCategories(prev =>
        prev.filter(c => c.id !== id).map(c => ({
          ...c,
          subCategories: c.subCategories?.filter(s => s.id !== id),
        }))
      );
    } catch { setError('Failed to delete. It may be in use by transactions.'); }
  };

  const handleMove = async (subId: number, subName: string) => {
    const targetParentId = moveTarget === '' ? null : Number(moveTarget);
    const targetName = targetParentId === null
      ? 'a top-level category'
      : `"${categories.find(c => c.id === targetParentId)?.name}"`;
    if (!confirm(`Move "${subName}" to ${targetName}?`)) return;
    try {
      await updateCategory(subId, { parentId: targetParentId ?? undefined });
      // Remove sub from its current parent
      let moved: Category | undefined;
      const updated = categories.map(c => {
        const sub = c.subCategories?.find(s => s.id === subId);
        if (sub) { moved = sub; return { ...c, subCategories: c.subCategories!.filter(s => s.id !== subId) }; }
        return c;
      });
      if (moved) {
        if (targetParentId === null) {
          // Promote to top-level
          setCategories([...updated, { ...moved, subCategories: [] }]);
        } else {
          setCategories(updated.map(c =>
            c.id === targetParentId ? { ...c, subCategories: [...(c.subCategories ?? []), moved!] } : c
          ));
        }
      }
      setMovingId(null);
      setMoveTarget('');
    } catch { setError('Failed to move category.'); }
  };

  const otherParents = (currentParentId: number) =>
    categories.filter(c => c.id !== currentParentId);

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>;

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Categories</h2>
        <button className={styles.btnPrimary} onClick={() => { setAddingCat(true); setNewCatName(''); }}>
          + Add Category
        </button>
      </div>

      {error && <div className={styles.errorMsg}>{error}</div>}

      <div className={styles.statsLegend}>
        <span>First Transaction<InfoIcon text={FIRST_TX_INFO} /></span>
        <span>Last Transaction<InfoIcon text={LAST_TX_INFO} /></span>
        <span># Transactions<InfoIcon text={COUNT_INFO} /></span>
      </div>

      {addingCat && (
        <div className={styles.addRow}>
          <input
            className={styles.input}
            value={newCatName}
            onChange={e => setNewCatName(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') handleAddCategory(); if (e.key === 'Escape') setAddingCat(false); }}
            placeholder="Category name"
            autoFocus
          />
          <button className={styles.btnPrimary} onClick={handleAddCategory}>Save</button>
          <button className={styles.btnSecondary} onClick={() => setAddingCat(false)}>Cancel</button>
        </div>
      )}

      <div className={styles.list}>
        {categories.length === 0 && <p className={styles.empty}>No categories yet.</p>}

        {categories.map(cat => (
          <div key={cat.id} className={styles.categoryGroup}>

            {/* ── Parent category row ── */}
            <div className={styles.categoryRow}>
              {renamingId === cat.id ? (
                <>
                  <input
                    className={styles.input}
                    value={renameValue}
                    onChange={e => setRenameValue(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') handleRename(cat.id, false); if (e.key === 'Escape') setRenamingId(null); }}
                    autoFocus
                  />
                  <button className={styles.btnSm} onClick={() => handleRename(cat.id, false)}>Save</button>
                  <button className={styles.btnSmSecondary} onClick={() => setRenamingId(null)}>Cancel</button>
                </>
              ) : (
                <>
                  <span
                    className={styles.categoryName}
                    onDoubleClick={() => navigate(`/transactions?categoryId=${cat.id}&label=${encodeURIComponent(cat.name)}`)}
                    title="Double-click to view transactions"
                  >{cat.name}</span>
                  <Stats item={cat} />
                  <div className={styles.rowActions}>
                    <button className={styles.btnSm} onClick={() => { setRenamingId(cat.id); setRenameValue(cat.name); }}>Rename</button>
                    <button className={styles.btnSmAdd} onClick={() => { setAddingSubFor(cat.id); setNewSubName(''); }}>+ Sub</button>
                    <button className={styles.btnSmDelete} onClick={() => handleDelete(cat.id, cat.name)}>Delete</button>
                  </div>
                </>
              )}
            </div>

            {/* ── Subcategory rows ── */}
            {cat.subCategories?.map(sub => (
              <div key={sub.id} className={styles.subRow}>
                {movingId === sub.id ? (
                  <div className={styles.movePanel}>
                    <span className={styles.subName}>{sub.name}</span>
                    <select
                      className={styles.select}
                      value={moveTarget}
                      onChange={e => setMoveTarget(e.target.value)}
                    >
                      <option value="">— Make top-level category —</option>
                      {otherParents(cat.id).map(p => (
                        <option key={p.id} value={p.id}>Move under "{p.name}"</option>
                      ))}
                    </select>
                    <button className={styles.btnSm} onClick={() => handleMove(sub.id, sub.name)}>Confirm</button>
                    <button className={styles.btnSmSecondary} onClick={() => setMovingId(null)}>Cancel</button>
                  </div>
                ) : renamingId === sub.id ? (
                  <div className={styles.movePanel}>
                    <input
                      className={styles.input}
                      value={renameValue}
                      onChange={e => setRenameValue(e.target.value)}
                      onKeyDown={e => { if (e.key === 'Enter') handleRename(sub.id, true); if (e.key === 'Escape') setRenamingId(null); }}
                      autoFocus
                    />
                    <button className={styles.btnSm} onClick={() => handleRename(sub.id, true)}>Save</button>
                    <button className={styles.btnSmSecondary} onClick={() => setRenamingId(null)}>Cancel</button>
                  </div>
                ) : (
                  <>
                    <span
                      className={styles.subName}
                      onDoubleClick={() => navigate(`/transactions?categoryId=${sub.id}&label=${encodeURIComponent(`${cat.name}: ${sub.name}`)}`)}
                      title="Double-click to view transactions"
                    >{cat.name}: {sub.name}</span>
                    <Stats item={sub} />
                    <div className={styles.rowActions}>
                      <button className={styles.btnSm} onClick={() => { setRenamingId(sub.id); setRenameValue(sub.name); }}>Rename</button>
                      <button className={styles.btnSm} onClick={() => { setMovingId(sub.id); setMoveTarget(''); }}>Move</button>
                      <button className={styles.btnSmDelete} onClick={() => handleDelete(sub.id, `${cat.name}: ${sub.name}`)}>Delete</button>
                    </div>
                  </>
                )}
              </div>
            ))}

            {/* ── Add subcategory inline ── */}
            {addingSubFor === cat.id && (
              <div className={styles.addSubRow}>
                <span className={styles.subPrefix}>{cat.name}:</span>
                <input
                  className={styles.input}
                  value={newSubName}
                  onChange={e => setNewSubName(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') handleAddSub(cat.id); if (e.key === 'Escape') setAddingSubFor(null); }}
                  placeholder="Subcategory name"
                  autoFocus
                />
                <button className={styles.btnSm} onClick={() => handleAddSub(cat.id)}>Save</button>
                <button className={styles.btnSmSecondary} onClick={() => setAddingSubFor(null)}>Cancel</button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
