import { useEffect, useRef, useState } from 'react';
import { extractReceipt, type ExtractedReceipt } from '../api/receipts';
import styles from './ReceiptScanner.module.css';

interface Props {
  onConfirm: (data: ExtractedReceipt) => void;
  onCancel: () => void;
}

export default function ReceiptScanner({ onConfirm, onCancel }: Props) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [extracting, setExtracting] = useState(false);
  const [error, setError] = useState('');
  const [extracted, setExtracted] = useState<ExtractedReceipt | null>(null);

  // Editable fields
  const [date, setDate] = useState('');
  const [payee, setPayee] = useState('');
  const [amount, setAmount] = useState('');
  const [memo, setMemo] = useState('');
  const [category, setCategory] = useState('');

  // Revoke object URLs when replaced and on unmount to avoid leaking blobs
  const previewUrlRef = useRef<string | null>(null);
  useEffect(() => () => {
    if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
  }, []);

  const handleFile = async (file: File) => {
    setError('');
    setExtracted(null);
    if (previewUrlRef.current) URL.revokeObjectURL(previewUrlRef.current);
    const url = URL.createObjectURL(file);
    previewUrlRef.current = url;
    setPreview(url);
    setExtracting(true);
    try {
      const data = await extractReceipt(file);
      setExtracted(data);
      setDate(data.date ?? new Date().toISOString().slice(0, 10));
      setPayee(data.payee ?? '');
      setAmount(data.amount != null ? String(data.amount) : '');
      setMemo(data.memo ?? '');
      setCategory(data.suggestedCategory ?? '');
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to extract receipt data.');
    } finally {
      setExtracting(false);
    }
  };

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) handleFile(file);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    const file = e.dataTransfer.files?.[0];
    if (file) handleFile(file);
  };

  const handleConfirm = () => {
    onConfirm({
      date: date || null,
      payee: payee || null,
      amount: amount ? Number(amount) : null,
      memo: memo || null,
      suggestedCategory: category || null,
    });
  };

  return (
    <div className={styles.overlay}>
      <div className={styles.panel}>
        <div className={styles.header}>
          <h3 className={styles.title}>📷 Scan Receipt</h3>
          <button className={styles.closeBtn} onClick={onCancel}>✕</button>
        </div>

        {!extracted && !extracting && (
          <div
            className={styles.dropZone}
            onClick={() => fileRef.current?.click()}
            onDrop={handleDrop}
            onDragOver={e => e.preventDefault()}
          >
            <div className={styles.dropIcon}>🧾</div>
            <div className={styles.dropText}>Tap to take a photo or upload an image</div>
            <div className={styles.dropHint}>JPEG, PNG, HEIC supported</div>
            <input
              ref={fileRef}
              type="file"
              accept="image/*"
              capture="environment"
              className={styles.fileInput}
              onChange={handleInputChange}
            />
          </div>
        )}

        {extracting && (
          <div className={styles.extracting}>
            {preview && <img src={preview} className={styles.previewThumb} alt="Receipt" />}
            <div className={styles.spinner} />
            <div className={styles.extractingText}>Analyzing receipt…</div>
          </div>
        )}

        {error && <div className={styles.error}>{error}</div>}

        {extracted && !extracting && (
          <>
            <div className={styles.twoCol}>
              {preview && <img src={preview} className={styles.previewImg} alt="Receipt" />}
              <div className={styles.fields}>
                <div className={styles.fieldNote}>Review and correct if needed</div>

                <div className={styles.field}>
                  <label className={styles.label}>Date</label>
                  <input className={styles.input} type="date" value={date} onChange={e => setDate(e.target.value)} />
                </div>
                <div className={styles.field}>
                  <label className={styles.label}>Payee / Merchant</label>
                  <input className={styles.input} type="text" value={payee} onChange={e => setPayee(e.target.value)} placeholder="Merchant name" />
                </div>
                <div className={styles.field}>
                  <label className={styles.label}>Amount</label>
                  <input className={styles.input} type="number" step="0.01" value={amount} onChange={e => setAmount(e.target.value)} placeholder="0.00" />
                </div>
                <div className={styles.field}>
                  <label className={styles.label}>Memo</label>
                  <input className={styles.input} type="text" value={memo} onChange={e => setMemo(e.target.value)} placeholder="Brief description" />
                </div>
                <div className={styles.field}>
                  <label className={styles.label}>Category</label>
                  <input className={styles.input} type="text" value={category} onChange={e => setCategory(e.target.value)} placeholder="e.g. Groceries" />
                </div>
              </div>
            </div>

            <div className={styles.actions}>
              <button className={styles.btnPrimary} onClick={handleConfirm}>
                Use This Data
              </button>
              <button
                className={styles.btnSecondary}
                onClick={() => {
                  setExtracted(null);
                  setPreview(null);
                  if (previewUrlRef.current) { URL.revokeObjectURL(previewUrlRef.current); previewUrlRef.current = null; }
                  if (fileRef.current) fileRef.current.value = '';
                }}
              >
                Try Again
              </button>
              <button className={styles.btnSecondary} onClick={onCancel}>Cancel</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
