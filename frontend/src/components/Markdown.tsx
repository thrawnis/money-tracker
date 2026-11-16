import { Fragment, type ReactNode } from 'react';
import styles from './Markdown.module.css';

/**
 * Renders the small Markdown subset AI answers use — paragraphs, headings,
 * bullet/numbered lists, tables, **bold**, *italic* and `code` — as React
 * elements (never raw HTML, so nothing in an answer can inject markup).
 */
export default function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: ReactNode[] = [];
  let i = 0;
  const isTableRow = (l: string) => /^\s*\|.*\|\s*$/.test(l);
  const cells = (l: string) => l.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());

  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }

    const h = line.match(/^(#{1,4})\s+(.*)$/);
    if (h) { blocks.push(<p key={i} className={styles.heading}>{inline(h[2])}</p>); i++; continue; }

    if (isTableRow(line) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      const head = cells(line);
      const align = cells(lines[i + 1]).map(c => (c.endsWith(':') ? 'right' : 'left') as 'right' | 'left');
      const rows: string[][] = [];
      i += 2;
      while (i < lines.length && isTableRow(lines[i])) rows.push(cells(lines[i++]));
      blocks.push(
        <div key={i} className={styles.tableWrap}>
          <table className={styles.table}>
            <thead><tr>{head.map((c, k) => <th key={k} style={{ textAlign: align[k] }}>{inline(c)}</th>)}</tr></thead>
            <tbody>{rows.map((r, j) => <tr key={j}>{r.map((c, k) => <td key={k} style={{ textAlign: align[k] }}>{inline(c)}</td>)}</tr>)}</tbody>
          </table>
        </div>);
      continue;
    }

    const bullet = /^\s*[-*•]\s+/, numbered = /^\s*\d+[.)]\s+/;
    if (bullet.test(line) || numbered.test(line)) {
      const ordered = numbered.test(line);
      const re = ordered ? numbered : bullet;
      const items: ReactNode[] = [];
      while (i < lines.length && re.test(lines[i])) { items.push(<li key={i}>{inline(lines[i].replace(re, ''))}</li>); i++; }
      blocks.push(ordered ? <ol key={i} className={styles.list}>{items}</ol> : <ul key={i} className={styles.list}>{items}</ul>);
      continue;
    }

    const para: string[] = [];
    while (i < lines.length && lines[i].trim() && !isTableRow(lines[i]) && !/^#{1,4}\s/.test(lines[i])
      && !bullet.test(lines[i]) && !numbered.test(lines[i])) para.push(lines[i++]);
    blocks.push(<p key={i} className={styles.para}>{para.map((p, k) => <Fragment key={k}>{k > 0 && <br />}{inline(p)}</Fragment>)}</p>);
  }
  return <div className={styles.md}>{blocks}</div>;
}

function inline(s: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*|_[^_\s][^_]*_)/g;
  let last = 0, m: RegExpExecArray | null, k = 0;
  while ((m = re.exec(s))) {
    if (m.index > last) out.push(s.slice(last, m.index));
    const t = m[0];
    if (t.startsWith('**')) out.push(<strong key={k++}>{t.slice(2, -2)}</strong>);
    else if (t.startsWith('`')) out.push(<code key={k++}>{t.slice(1, -1)}</code>);
    else out.push(<em key={k++}>{t.slice(1, -1)}</em>);
    last = m.index + t.length;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}
