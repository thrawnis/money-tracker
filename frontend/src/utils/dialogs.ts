/**
 * Themed replacements for the browser's confirm() / prompt() / alert().
 * Each returns a promise; <DialogHost/> (mounted once in App) renders them.
 *
 *   if (!(await confirmDialog('Delete this payee?'))) return;
 *   const name = await promptDialog('Rename this person:', { defaultValue: p.name });
 *
 * A confirmation whose message starts with Delete / Remove / Discard / Reset /
 * Sign out / Void / Unlink gets a red button labelled with that verb unless
 * `confirmLabel` / `danger` say otherwise.
 */
export interface DialogOptions {
  title?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
}
export interface PromptOptions extends DialogOptions {
  defaultValue?: string;
  placeholder?: string;
  /** Show the text read-only with a Copy button (replaces prompt('Copy this…', text)). */
  copyOnly?: boolean;
}

export type DialogRequest =
  | { id: number; kind: 'confirm'; message: string; opts: DialogOptions; resolve: (ok: boolean) => void }
  | { id: number; kind: 'alert'; message: string; opts: DialogOptions; resolve: () => void }
  | { id: number; kind: 'prompt'; message: string; opts: PromptOptions; resolve: (v: string | null) => void };

let queue: DialogRequest[] = [];
let nextId = 1;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

export const dialogStore = {
  subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; },
  /** The dialog on screen (the oldest waiting one). */
  current: (): DialogRequest | undefined => queue[0],
  close(id: number) { queue = queue.filter(d => d.id !== id); emit(); },
};

const push = (d: DialogRequest) => { queue = [...queue, d]; emit(); };

const DANGER = /^(delete|remove|discard|reset|sign out|void|unlink|disconnect)\b/i;

export function confirmDialog(message: string, opts: DialogOptions = {}): Promise<boolean> {
  const verb = message.trim().match(DANGER)?.[1];
  const full: DialogOptions = {
    ...opts,
    danger: opts.danger ?? !!verb,
    confirmLabel: opts.confirmLabel ?? (verb ? verb[0].toUpperCase() + verb.slice(1).toLowerCase() : 'OK'),
  };
  return new Promise(resolve => push({ id: nextId++, kind: 'confirm', message, opts: full, resolve }));
}

export function promptDialog(message: string, opts: PromptOptions = {}): Promise<string | null> {
  return new Promise(resolve => push({ id: nextId++, kind: 'prompt', message, opts, resolve }));
}

export function alertDialog(message: string, opts: DialogOptions = {}): Promise<void> {
  return new Promise(resolve => push({ id: nextId++, kind: 'alert', message, opts, resolve }));
}
