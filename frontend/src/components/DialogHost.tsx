import { useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { dialogStore, type DialogRequest } from '../utils/dialogs';
import styles from './DialogHost.module.css';

/** Renders the themed dialogs requested through utils/dialogs. Mount once. */
export default function DialogHost() {
  const d = useSyncExternalStore(dialogStore.subscribe, dialogStore.current);
  return d ? <Dialog key={d.id} d={d} /> : null;
}

function Dialog({ d }: { d: DialogRequest }) {
  const [value, setValue] = useState(d.kind === 'prompt' ? d.opts.defaultValue ?? '' : '');
  const [copied, setCopied] = useState(false);
  const okRef = useRef<HTMLButtonElement>(null);
  const inputRef = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  const copyOnly = d.kind === 'prompt' && d.opts.copyOnly;

  const finish = (ok: boolean) => {
    dialogStore.close(d.id);
    if (d.kind === 'confirm') d.resolve(ok);
    else if (d.kind === 'alert') d.resolve();
    else d.resolve(ok && !copyOnly ? value : null);
  };

  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    if (d.kind === 'prompt') { inputRef.current?.focus(); inputRef.current?.select(); }
    else okRef.current?.focus();
    // Give focus back to whatever had it (e.g. the button that asked).
    return () => { before?.focus?.(); };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Keys pressed in the dialog stay in it: page shortcuts (N, Escape to close
  // a panel…) must not fire underneath. Tab cycles within the dialog.
  const onKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    e.stopPropagation();
    if (e.key === 'Escape') { e.preventDefault(); finish(false); return; }
    if (e.key === 'Tab') {
      const items = [...e.currentTarget.querySelectorAll<HTMLElement>('input, textarea, button')];
      const i = items.indexOf(document.activeElement as HTMLElement);
      const next = e.shiftKey ? (i <= 0 ? items.length - 1 : i - 1) : (i === items.length - 1 ? 0 : i + 1);
      e.preventDefault(); items[next]?.focus();
    }
  };

  const copy = async () => {
    try { await navigator.clipboard.writeText(value); setCopied(true); }
    catch { inputRef.current?.select(); }
  };

  const title = d.opts.title ?? (d.kind === 'alert' ? 'Money Tracker' : d.kind === 'prompt' ? (copyOnly ? 'Copy' : 'Enter a value') : 'Please confirm');
  return (
    <div className={styles.overlay} onKeyDown={onKeyDown} onMouseDown={e => { if (e.target === e.currentTarget) finish(false); }}>
      <form
        className={styles.dialog}
        role={d.kind === 'confirm' ? 'alertdialog' : 'dialog'}
        aria-modal="true"
        aria-labelledby={`dlg-title-${d.id}`}
        aria-describedby={`dlg-msg-${d.id}`}
        onSubmit={e => { e.preventDefault(); if (copyOnly) void copy(); else finish(true); }}
      >
        <div className={`${styles.header} ${d.opts.danger ? styles.headerDanger : ''}`}>
          <h2 id={`dlg-title-${d.id}`} className={styles.title}>{title}</h2>
        </div>
        <div className={styles.body}>
          <p id={`dlg-msg-${d.id}`} className={styles.message}>{d.message}</p>
          {d.kind === 'prompt' && (copyOnly
            ? <textarea ref={inputRef} className={styles.input} value={value} readOnly rows={Math.min(8, value.split('\n').length + 1)} />
            : <input ref={inputRef} className={styles.input} value={value} placeholder={d.opts.placeholder}
                onChange={e => setValue(e.target.value)} />)}
        </div>
        <div className={styles.actions}>
          {d.kind !== 'alert' && (
            <button type="button" className={styles.btnCancel} onClick={() => finish(false)}>
              {copyOnly ? 'Close' : d.opts.cancelLabel ?? 'Cancel'}
            </button>
          )}
          <button ref={okRef} type="submit" className={d.opts.danger ? styles.btnDanger : styles.btnOk}>
            {copyOnly ? (copied ? '✓ Copied' : 'Copy') : d.opts.confirmLabel ?? 'OK'}
          </button>
        </div>
      </form>
    </div>
  );
}
