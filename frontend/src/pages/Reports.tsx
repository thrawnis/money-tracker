import { useEffect, useState } from 'react';
import { usePageTitle } from '../hooks/usePageTitle';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import {
  getMonthlyReport,
  getCategoryReport,
  getSavedReports,
  createSavedReport,
  updateSavedReport,
  deleteSavedReport,
  type MonthlyReport,
  type CategoryReport,
  type SavedReport,
} from '../api/reports';
import { getAccounts } from '../api/accounts';
import { getCategories } from '../api/categories';
import { getPayees } from '../api/payees';
import type { Account, Category, Payee } from '../types';
import styles from './Reports.module.css';

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

const now = new Date();

function presetRange(preset: string) {
  const y = now.getFullYear();
  const m = now.getMonth() + 1;
  if (preset === 'thisYear') return { fromYear: y, fromMonth: 1, toYear: y, toMonth: 12 };
  if (preset === 'lastYear') return { fromYear: y - 1, fromMonth: 1, toYear: y - 1, toMonth: 12 };
  if (preset === 'last3') {
    const from = new Date(now.getFullYear(), now.getMonth() - 2, 1);
    return { fromYear: from.getFullYear(), fromMonth: from.getMonth() + 1, toYear: y, toMonth: m };
  }
  if (preset === 'last6') {
    const from = new Date(now.getFullYear(), now.getMonth() - 5, 1);
    return { fromYear: from.getFullYear(), fromMonth: from.getMonth() + 1, toYear: y, toMonth: m };
  }
  if (preset === 'last12') {
    const from = new Date(now.getFullYear(), now.getMonth() - 11, 1);
    return { fromYear: from.getFullYear(), fromMonth: from.getMonth() + 1, toYear: y, toMonth: m };
  }
  return { fromYear: y, fromMonth: 1, toYear: y, toMonth: m };
}

function exportCsv(headers: string[], rows: (string | number)[][], filename: string) {
  const lines = [headers, ...rows].map(r => r.map(c => `"${String(c).replace(/"/g, '""')}"`).join(','));
  const blob = new Blob([lines.join('\n')], { type: 'text/csv' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename; a.click();
  URL.revokeObjectURL(url);
}

type ReportType = 'monthly' | 'category';

export default function Reports() {
  usePageTitle('Reports');
  const [reportType, setReportType] = useState<ReportType>('monthly');
  const [activeSavedId, setActiveSavedId] = useState<number | null>(null);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [payees, setPayees] = useState<Payee[]>([]);
  const [savedReports, setSavedReports] = useState<SavedReport[]>([]);
  const [saveReportName, setSaveReportName] = useState('');
  const [savingReport, setSavingReport] = useState(false);
  const [savedReportError, setSavedReportError] = useState('');

  // saveReportName doubles as the display title for a loaded saved report, so
  // "dirty" means it differs from what's actually saved — not just non-empty.
  const activeSavedName = activeSavedId !== null ? savedReports.find(r => r.id === activeSavedId)?.name : undefined;
  const hasUnsavedReportName = activeSavedId === null ? saveReportName !== '' : saveReportName !== activeSavedName;
  useUnsavedChanges(hasUnsavedReportName);

  const defaultRange = presetRange('last6');
  const [mFromYear, setMFromYear] = useState(defaultRange.fromYear);
  const [mFromMonth, setMFromMonth] = useState(defaultRange.fromMonth);
  const [mToYear, setMToYear] = useState(defaultRange.toYear);
  const [mToMonth, setMToMonth] = useState(defaultRange.toMonth);
  const [mAccounts, setMAccounts] = useState<number[]>([]);
  const [mCategories, setMCategories] = useState<number[]>([]);
  const [monthlyReport, setMonthlyReport] = useState<MonthlyReport | null>(null);
  const [monthlyLoading, setMonthlyLoading] = useState(false);
  const [monthlyError, setMonthlyError] = useState('');

  const [cCategoryId, setCCategoryId] = useState('');
  const [cFrom, setCFrom] = useState('');
  const [cTo, setCTo] = useState('');
  const [cAccounts, setCAccounts] = useState<number[]>([]);
  const [cPayees, setCPayees] = useState<number[]>([]);
  const [catReport, setCatReport] = useState<CategoryReport | null>(null);
  const [catLoading, setCatLoading] = useState(false);
  const [catError, setCatError] = useState('');
  const [visibleCols, setVisibleCols] = useState({ date: true, account: true, payee: true, memo: true, amount: true });

  useEffect(() => {
    Promise.all([getAccounts(), getCategories(), getPayees(), getSavedReports()])
      .then(([accs, cats, pays, saved]) => {
        setAccounts(accs); setCategories(cats); setPayees(pays); setSavedReports(saved);
      }).catch(console.error);
  }, []);

  const allCategories: { id: number; label: string }[] = [];
  for (const cat of categories) {
    allCategories.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        allCategories.push({ id: sub.id, label: `${cat.name} : ${sub.name}` });
      }
    }
  }

  const loadSavedReport = (r: SavedReport) => {
    setActiveSavedId(r.id);
    setSaveReportName(r.name);
    setReportType(r.type);
    setMonthlyReport(null);
    setCatReport(null);
    const p = r.params as Record<string, unknown>;
    if (r.type === 'monthly') {
      if (p.fromYear) setMFromYear(Number(p.fromYear));
      if (p.fromMonth) setMFromMonth(Number(p.fromMonth));
      if (p.toYear) setMToYear(Number(p.toYear));
      if (p.toMonth) setMToMonth(Number(p.toMonth));
      setMAccounts(Array.isArray(p.accountIds) ? p.accountIds.map(Number) : []);
      setMCategories(Array.isArray(p.categoryIds) ? p.categoryIds.map(Number) : []);
    } else {
      setCCategoryId(p.categoryId ? String(p.categoryId) : '');
      setCFrom(typeof p.from === 'string' ? p.from : '');
      setCTo(typeof p.to === 'string' ? p.to : '');
      setCAccounts(Array.isArray(p.accountIds) ? p.accountIds.map(Number) : []);
      setCPayees(Array.isArray(p.payeeIds) ? p.payeeIds.map(Number) : []);
    }
  };

  const selectReportType = (t: ReportType) => {
    setReportType(t);
    setActiveSavedId(null);
    setSaveReportName('');
    setMonthlyReport(null);
    setCatReport(null);
  };

  const runMonthly = async () => {
    setMonthlyLoading(true); setMonthlyError('');
    try {
      const r = await getMonthlyReport({
        fromYear: mFromYear, fromMonth: mFromMonth,
        toYear: mToYear, toMonth: mToMonth,
        accountIds: mAccounts.length ? mAccounts : undefined,
        categoryIds: mCategories.length ? mCategories : undefined,
      });
      setMonthlyReport(r);
    } catch { setMonthlyError('Failed to load report.'); }
    finally { setMonthlyLoading(false); }
  };

  const runCategory = async () => {
    setCatLoading(true); setCatError('');
    try {
      const r = await getCategoryReport({
        categoryId: cCategoryId ? Number(cCategoryId) : undefined,
        from: cFrom || undefined,
        to: cTo || undefined,
        accountIds: cAccounts.length ? cAccounts : undefined,
        payeeIds: cPayees.length ? cPayees : undefined,
      });
      setCatReport(r);
    } catch { setCatError('Failed to load report.'); }
    finally { setCatLoading(false); }
  };

  const applyPreset = (preset: string) => {
    const r = presetRange(preset);
    setMFromYear(r.fromYear); setMFromMonth(r.fromMonth);
    setMToYear(r.toYear); setMToMonth(r.toMonth);
  };

  const currentParams = () => reportType === 'monthly'
    ? { fromYear: mFromYear, fromMonth: mFromMonth, toYear: mToYear, toMonth: mToMonth, accountIds: mAccounts, categoryIds: mCategories }
    : { categoryId: cCategoryId, from: cFrom, to: cTo, accountIds: cAccounts, payeeIds: cPayees };

  const handleSaveReport = async () => {
    if (!saveReportName) return;
    setSavingReport(true);
    setSavedReportError('');
    try {
      if (activeSavedId !== null) {
        const updated = await updateSavedReport(activeSavedId, { name: saveReportName, params: currentParams() });
        setSavedReports(prev => prev.map(r => r.id === activeSavedId ? updated : r));
      } else {
        const saved = await createSavedReport({ name: saveReportName, type: reportType, params: currentParams() });
        setSavedReports(prev => [...prev, saved]);
        setActiveSavedId(saved.id);
      }
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setSavedReportError(msg ?? 'Failed to save report.');
    }
    finally { setSavingReport(false); }
  };

  const handleDeleteSaved = async (id: number) => {
    setSavedReportError('');
    try {
      await deleteSavedReport(id);
    } catch {
      setSavedReportError('Failed to delete saved report.');
      return;
    }
    setSavedReports(prev => prev.filter(r => r.id !== id));
    if (activeSavedId === id) {
      setActiveSavedId(null);
      setSaveReportName('');
    }
  };

  const handleExportCsv = () => {
    if (!catReport) return;
    const headers = ['Date', 'Account', 'Payee', 'Memo', 'Amount'];
    const rows = catReport.items.map(item => [item.date, item.accountName, item.payee ?? '', item.memo ?? '', item.amount]);
    exportCsv(headers, rows, 'category-report.csv');
  };

  const months = monthlyReport?.months ?? [];
  const YEARS = Array.from({ length: 10 }, (_, i) => now.getFullYear() - 5 + i);
  const MONTHS = [
    [1,'Jan'],[2,'Feb'],[3,'Mar'],[4,'Apr'],[5,'May'],[6,'Jun'],
    [7,'Jul'],[8,'Aug'],[9,'Sep'],[10,'Oct'],[11,'Nov'],[12,'Dec'],
  ] as const;

  return (
    <div className={styles.page}>
      <div className={styles.layout}>

        {/* ── Left sidebar list ── */}
        <div className={styles.sidebar}>
          <div className={styles.sidebarSection}>
            <div className={styles.sidebarHeading}>Report Types</div>
            <button
              className={`${styles.listItem} ${reportType === 'monthly' && activeSavedId === null ? styles.listItemActive : ''}`}
              onClick={() => selectReportType('monthly')}
            >
              📊 Monthly Income/Expense
            </button>
            <button
              className={`${styles.listItem} ${reportType === 'category' && activeSavedId === null ? styles.listItemActive : ''}`}
              onClick={() => selectReportType('category')}
            >
              🏷️ Transactions by Category
            </button>
          </div>

          {savedReports.length > 0 && (
            <div className={styles.sidebarSection}>
              <div className={styles.sidebarHeading}>Saved Reports</div>
              {savedReports.map(r => (
                <div key={r.id} className={`${styles.savedItem} ${activeSavedId === r.id ? styles.savedItemActive : ''}`}>
                  <button className={styles.savedItemBtn} onClick={() => loadSavedReport(r)}>
                    {r.type === 'monthly' ? '📊' : '🏷️'} {r.name}
                  </button>
                  <button className={styles.savedItemDelete} onClick={() => handleDeleteSaved(r.id)} title="Delete">✕</button>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* ── Main content ── */}
        <div className={styles.main}>
          <div className={styles.mainHeader}>
            <h2 className={styles.pageTitle}>
              {activeSavedId !== null
                ? saveReportName
                : reportType === 'monthly' ? 'Monthly Income/Expense' : 'Transactions by Category'}
            </h2>
          </div>

          <div className={styles.controls}>
            {reportType === 'monthly' && (
              <>
                <div className={styles.controlRow}>
                  <span className={styles.controlLabel}>Presets:</span>
                  {[['thisYear','This Year'],['lastYear','Last Year'],['last3','Last 3M'],['last6','Last 6M'],['last12','Last 12M']].map(([k,l]) => (
                    <button key={k} className={styles.presetBtn} onClick={() => applyPreset(k)}>{l}</button>
                  ))}
                </div>
                <div className={styles.controlRow}>
                  <label className={styles.controlLabel}>From:</label>
                  <select value={mFromYear} onChange={e => setMFromYear(Number(e.target.value))}>
                    {YEARS.map(y => <option key={y} value={y}>{y}</option>)}
                  </select>
                  <select value={mFromMonth} onChange={e => setMFromMonth(Number(e.target.value))}>
                    {MONTHS.map(([n,l]) => <option key={n} value={n}>{l}</option>)}
                  </select>
                  <label className={styles.controlLabel}>To:</label>
                  <select value={mToYear} onChange={e => setMToYear(Number(e.target.value))}>
                    {YEARS.map(y => <option key={y} value={y}>{y}</option>)}
                  </select>
                  <select value={mToMonth} onChange={e => setMToMonth(Number(e.target.value))}>
                    {MONTHS.map(([n,l]) => <option key={n} value={n}>{l}</option>)}
                  </select>
                </div>
                <div className={styles.controlRow}>
                  <label className={styles.controlLabel}>Accounts:</label>
                  <select multiple size={3} value={mAccounts.map(String)} onChange={e => setMAccounts(Array.from(e.target.selectedOptions, o => Number(o.value)))} className={styles.multiSelect}>
                    {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                  </select>
                  <label className={styles.controlLabel}>Categories:</label>
                  <select multiple size={3} value={mCategories.map(String)} onChange={e => setMCategories(Array.from(e.target.selectedOptions, o => Number(o.value)))} className={styles.multiSelect}>
                    {allCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
                  </select>
                </div>
              </>
            )}

            {reportType === 'category' && (
              <>
                <div className={styles.controlRow}>
                  <label className={styles.controlLabel}>Category:</label>
                  <select value={cCategoryId} onChange={e => setCCategoryId(e.target.value)}>
                    <option value="">All</option>
                    {allCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
                  </select>
                  <label className={styles.controlLabel}>From:</label>
                  <input type="date" value={cFrom} onChange={e => setCFrom(e.target.value)} />
                  <label className={styles.controlLabel}>To:</label>
                  <input type="date" value={cTo} onChange={e => setCTo(e.target.value)} />
                </div>
                <div className={styles.controlRow}>
                  <label className={styles.controlLabel}>Accounts:</label>
                  <select multiple size={3} value={cAccounts.map(String)} onChange={e => setCAccounts(Array.from(e.target.selectedOptions, o => Number(o.value)))} className={styles.multiSelect}>
                    {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                  </select>
                  <label className={styles.controlLabel}>Payees:</label>
                  <select multiple size={3} value={cPayees.map(String)} onChange={e => setCPayees(Array.from(e.target.selectedOptions, o => Number(o.value)))} className={styles.multiSelect}>
                    {payees.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                </div>
                <div className={styles.controlRow}>
                  <span className={styles.controlLabel}>Columns:</span>
                  {(['date','account','payee','memo','amount'] as const).map(col => (
                    <label key={col} className={styles.colToggle}>
                      <input type="checkbox" checked={visibleCols[col]} onChange={e => setVisibleCols(v => ({ ...v, [col]: e.target.checked }))} />
                      {' '}{col.charAt(0).toUpperCase() + col.slice(1)}
                    </label>
                  ))}
                </div>
              </>
            )}

            <div className={styles.controlRow}>
              <button className={styles.btnPrimary} onClick={reportType === 'monthly' ? runMonthly : runCategory} disabled={monthlyLoading || catLoading}>
                {(monthlyLoading || catLoading) ? 'Loading…' : 'Run Report'}
              </button>
              {reportType === 'category' && catReport && (
                <button className={styles.btnSecondary} onClick={handleExportCsv}>Export CSV</button>
              )}
              <div className={styles.saveGroup}>
                <input
                  className={styles.saveInput}
                  placeholder="Report name…"
                  value={saveReportName}
                  onChange={e => setSaveReportName(e.target.value)}
                />
                <button className={styles.btnSave} onClick={handleSaveReport} disabled={savingReport || !saveReportName}>
                  {activeSavedId !== null ? 'Update' : 'Save'}
                </button>
                {activeSavedId !== null && (
                  <button className={styles.btnSecondary} onClick={() => { setActiveSavedId(null); setSaveReportName(''); }}>
                    Save as New
                  </button>
                )}
              </div>
            </div>
          </div>

          {monthlyError && <div className={styles.errorMsg}>{monthlyError}</div>}
          {catError && <div className={styles.errorMsg}>{catError}</div>}
          {savedReportError && <div className={styles.errorMsg}>{savedReportError}</div>}

          {reportType === 'monthly' && monthlyReport && (
            <div className={styles.tableWrapper}>
              <table className={styles.reportTable}>
                <thead>
                  <tr>
                    <th>Category</th>
                    {months.map(m => <th key={m} className={styles.right}>{m}</th>)}
                    <th className={styles.right}>Total</th>
                  </tr>
                </thead>
                <tbody>
                  {monthlyReport.rows.map((row, i) => (
                    <tr key={i}>
                      <td>{row.categoryName}</td>
                      {months.map(m => (
                        <td key={m} className={`${styles.right} ${(row.months[m] ?? 0) < 0 ? styles.negative : ''}`}>
                          {row.months[m] ? formatCurrency(row.months[m]) : '—'}
                        </td>
                      ))}
                      <td className={`${styles.right} ${row.total < 0 ? styles.negative : styles.bold}`}>
                        {formatCurrency(row.total)}
                      </td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr className={styles.totalsRow}>
                    <td>Total</td>
                    {months.map(m => (
                      <td key={m} className={`${styles.right} ${(monthlyReport.totals[m] ?? 0) < 0 ? styles.negative : ''}`}>
                        {monthlyReport.totals[m] ? formatCurrency(monthlyReport.totals[m]) : '—'}
                      </td>
                    ))}
                    <td className={styles.right}></td>
                  </tr>
                </tfoot>
              </table>
            </div>
          )}

          {reportType === 'category' && catReport && (
            <>
              <div className={styles.catSummary}>
                {catReport.count} transaction{catReport.count !== 1 ? 's' : ''} &bull; Total: <strong>{formatCurrency(catReport.total)}</strong>
              </div>
              <div className={styles.tableWrapper}>
                <table className={styles.reportTable}>
                  <thead>
                    <tr>
                      {visibleCols.date && <th>Date</th>}
                      {visibleCols.account && <th>Account</th>}
                      {visibleCols.payee && <th>Payee</th>}
                      {visibleCols.memo && <th>Memo</th>}
                      {visibleCols.amount && <th className={styles.right}>Amount</th>}
                    </tr>
                  </thead>
                  <tbody>
                    {catReport.items.map(item => (
                      <tr key={item.id}>
                        {visibleCols.date && <td>{formatDate(item.date)}</td>}
                        {visibleCols.account && <td>{item.accountName}</td>}
                        {visibleCols.payee && <td>{item.payee ?? '—'}</td>}
                        {visibleCols.memo && <td>{item.memo ?? ''}</td>}
                        {visibleCols.amount && (
                          <td className={`${styles.right} ${item.amount < 0 ? styles.negative : ''}`}>
                            {formatCurrency(item.amount)}
                          </td>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
