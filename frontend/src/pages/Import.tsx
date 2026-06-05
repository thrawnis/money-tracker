import { useState, useRef, type DragEvent, type ChangeEvent } from 'react';
import { importFile, getTemplate } from '../api/import';
import styles from './Import.module.css';

interface ImportResult {
  imported: number;
  errors?: string[];
}

export default function Import() {
  const [dragging, setDragging] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  const handleDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setDragging(false);
    const f = e.dataTransfer.files[0];
    if (f) { setFile(f); setResult(null); setError(''); }
  };

  const handleFileChange = (e: ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0] ?? null;
    if (f) { setFile(f); setResult(null); setError(''); }
  };

  const handleImport = async () => {
    if (!file) return;
    setLoading(true); setError(''); setResult(null);
    try {
      const res = await importFile(file);
      setResult(res);
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Import failed. Please check the file format and try again.');
    } finally {
      setLoading(false);
    }
  };

  const handleDownload = async (format: 'csv' | 'xlsx') => {
    try {
      const blob = await getTemplate(format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `template.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      alert('Failed to download template.');
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Import Transactions</h2>
      </div>

      <div className={styles.grid}>
        <div className={styles.importPanel}>
          <h3 className={styles.sectionTitle}>Import File</h3>
          <p className={styles.hint}>Supported formats: QIF, OFX, QFX, CSV, XLSX</p>

          <div
            className={`${styles.dropZone} ${dragging ? styles.dragging : ''}`}
            onDragOver={e => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={handleDrop}
            onClick={() => inputRef.current?.click()}
          >
            <input
              ref={inputRef}
              type="file"
              accept=".qif,.ofx,.qfx,.csv,.xlsx"
              style={{ display: 'none' }}
              onChange={handleFileChange}
            />
            {file ? (
              <div className={styles.selectedFile}>
                <div className={styles.fileName}>{file.name}</div>
                <div className={styles.fileSize}>{(file.size / 1024).toFixed(1)} KB</div>
              </div>
            ) : (
              <div className={styles.dropPrompt}>
                <div className={styles.dropIcon}>+</div>
                <div>Drag &amp; drop a file here, or click to browse</div>
              </div>
            )}
          </div>

          {file && (
            <button className={styles.btnPrimary} onClick={handleImport} disabled={loading}>
              {loading ? 'Importing…' : `Import ${file.name}`}
            </button>
          )}

          {error && <div className={styles.errorMsg}>{error}</div>}

          {result && (
            <div className={styles.resultBox}>
              <div className={styles.resultSuccess}>
                {result.imported} transaction{result.imported !== 1 ? 's' : ''} imported successfully.
              </div>
              {result.errors && result.errors.length > 0 && (
                <div className={styles.resultErrors}>
                  <div className={styles.resultErrorTitle}>Warnings / Errors:</div>
                  <ul>
                    {result.errors.map((e, i) => <li key={i}>{e}</li>)}
                  </ul>
                </div>
              )}
            </div>
          )}
        </div>

        <div className={styles.templatePanel}>
          <h3 className={styles.sectionTitle}>Download Templates</h3>
          <p className={styles.hint}>Download a template to see the expected format for CSV or XLSX imports.</p>
          <div className={styles.templateBtns}>
            <button className={styles.btnSecondary} onClick={() => handleDownload('csv')}>
              Download CSV Template
            </button>
            <button className={styles.btnSecondary} onClick={() => handleDownload('xlsx')}>
              Download XLSX Template
            </button>
          </div>

          <h3 className={styles.sectionTitle} style={{ marginTop: 24 }}>Format Notes</h3>
          <ul className={styles.formatList}>
            <li><strong>QIF</strong> — Quicken Interchange Format. Export from Quicken or MS Money.</li>
            <li><strong>OFX/QFX</strong> — Open Financial Exchange. Export from most banks.</li>
            <li><strong>CSV</strong> — Comma-separated values. Use the template above for column order.</li>
            <li><strong>XLSX</strong> — Excel spreadsheet. Use the template above for column order.</li>
          </ul>
        </div>
      </div>
    </div>
  );
}
