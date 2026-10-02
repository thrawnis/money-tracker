import { useEffect, useMemo, useRef, useState } from 'react';
import styles from './LineChart.module.css';

export interface LineSeries {
  key: string;
  label: string;
  /** 1-3: the fixed categorical slot (see LineChart.module.css). */
  slot: 1 | 2 | 3;
  /** One per date; null leaves a gap. */
  values: (number | null)[];
}

interface Props {
  dates: string[]; // YYYY-MM-DD
  series: LineSeries[];
  height?: number;
  /** Accessible summary of what the chart shows. */
  label: string;
}

const PAD = { top: 10, right: 12, bottom: 24, left: 64 };

const money = (n: number) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(n);

// Axis labels: compact ($12k, $1.2M) so the left margin stays narrow.
const compact = (n: number) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', notation: 'compact', maximumFractionDigits: 1 }).format(n);

const day = (d: string, withYear: boolean) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US',
    withYear ? { month: 'short', day: 'numeric', year: 'numeric' } : { month: 'short', year: '2-digit' });

/** Round tick values: 1, 2, 2.5 or 5 times a power of ten, about `count` of them. */
function niceTicks(min: number, max: number, count = 5): number[] {
  if (min === max) { min -= 1; max += 1; }
  const raw = (max - min) / count;
  const pow = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(m => m * pow).find(s => s >= raw) ?? raw;
  const ticks: number[] = [];
  for (let v = Math.floor(min / step) * step; v <= max + step * 1e-9; v += step) ticks.push(Math.round(v * 100) / 100);
  if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step);
  return ticks;
}

/**
 * A plain SVG line chart: thin 2px lines on one shared money axis, recessive
 * grid, direct labels at the line ends, a legend for 2+ series, and a
 * crosshair tooltip showing every series at the hovered date.
 */
export default function LineChart({ dates, series, height = 260, label }: Props) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  const [hover, setHover] = useState<number | null>(null);

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver(entries => setWidth(Math.max(240, entries[0].contentRect.width)));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Room on the right for direct end labels when there's more than one line.
  const labelRoom = series.length > 1 ? 74 : 0;
  const plotW = width - PAD.left - PAD.right - labelRoom;
  const plotH = height - PAD.top - PAD.bottom;

  const { ticks, y } = useMemo(() => {
    const all = series.flatMap(s => s.values).filter((v): v is number => v != null);
    const lo = Math.min(0, ...all);
    const hi = Math.max(0, ...all);
    const t = niceTicks(lo, hi);
    const t0 = t[0], t1 = t[t.length - 1];
    return { ticks: t, y: (v: number) => PAD.top + plotH - ((v - t0) / (t1 - t0 || 1)) * plotH };
  }, [series, plotH]);

  const x = (i: number) => PAD.left + (dates.length <= 1 ? plotW / 2 : (i / (dates.length - 1)) * plotW);

  const hasData = series.some(s => s.values.some(v => v != null));
  if (dates.length === 0 || !hasData) {
    return <div className={styles.empty}>No balance history for this period yet.</div>;
  }

  // Lines break at nulls rather than dropping to zero.
  const path = (values: (number | null)[]) => {
    let d = '';
    let pen = false;
    values.forEach((v, i) => {
      if (v == null) { pen = false; return; }
      d += `${pen ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`;
      pen = true;
    });
    return d;
  };

  const spanYears = dates.length > 1
    && new Date(dates[dates.length - 1]).getTime() - new Date(dates[0]).getTime() > 400 * 864e5;
  const xTickCount = Math.max(2, Math.min(6, Math.floor(plotW / 110)));
  const xTicks = Array.from(new Set(
    Array.from({ length: xTickCount }, (_, k) => Math.round((k / (xTickCount - 1)) * (dates.length - 1))),
  ));

  // End labels, nudged apart so they never overlap.
  const ends = series
    .map(s => {
      let i = s.values.length - 1;
      while (i >= 0 && s.values[i] == null) i--;
      return i < 0 ? null : { s, i, v: s.values[i] as number, ly: y(s.values[i] as number) };
    })
    .filter((e): e is NonNullable<typeof e> => e != null)
    .sort((a, b) => a.ly - b.ly);
  for (let k = 1; k < ends.length; k++) {
    if (ends[k].ly - ends[k - 1].ly < 14) ends[k].ly = ends[k - 1].ly + 14;
  }

  const onMove = (clientX: number) => {
    const rect = wrapRef.current?.getBoundingClientRect();
    if (!rect) return;
    const px = clientX - rect.left - PAD.left;
    const i = Math.round((px / plotW) * (dates.length - 1));
    setHover(Math.max(0, Math.min(dates.length - 1, i)));
  };

  const tipLeft = hover == null ? 0 : x(hover);
  const tipOnLeft = hover != null && tipLeft > width / 2;

  return (
    <div className={styles.root} ref={wrapRef}>
      {series.length > 1 && (
        <div className={styles.legend}>
          {series.map(s => (
            <span key={s.key} className={styles.legendItem}>
              <span className={styles.swatch} style={{ background: `var(--series-${s.slot})` }} />
              {s.label}
            </span>
          ))}
        </div>
      )}
      <svg
        className={styles.svg}
        width={width}
        height={height}
        role="img"
        aria-label={label}
        onMouseMove={e => onMove(e.clientX)}
        onMouseLeave={() => setHover(null)}
        onTouchStart={e => onMove(e.touches[0].clientX)}
        onTouchMove={e => onMove(e.touches[0].clientX)}
      >
        {ticks.map(t => (
          <g key={t}>
            <line x1={PAD.left} x2={PAD.left + plotW} y1={y(t)} y2={y(t)}
              stroke="var(--grid)" strokeWidth={t === 0 ? 1.5 : 1} />
            <text className={styles.tick} x={PAD.left - 8} y={y(t)} dy="0.32em" textAnchor="end">{compact(t)}</text>
          </g>
        ))}
        {xTicks.map(i => (
          <text key={i} className={styles.tick} x={x(i)} y={height - 6}
            textAnchor={i === 0 ? 'start' : i === dates.length - 1 ? 'end' : 'middle'}>
            {day(dates[i], !spanYears)}
          </text>
        ))}

        {series.map(s => (
          <path key={s.key} d={path(s.values)} fill="none" stroke={`var(--series-${s.slot})`}
            strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
        ))}

        {series.length > 1 && ends.map(e => (
          <text key={e.s.key} className={styles.endLabel} x={x(e.i) + 8} y={e.ly} dy="0.32em">{e.s.label}</text>
        ))}

        {hover != null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={PAD.top} y2={PAD.top + plotH} stroke="var(--axis)" strokeWidth={1} strokeDasharray="3 3" />
            {series.map(s => s.values[hover] != null && (
              <circle key={s.key} cx={x(hover)} cy={y(s.values[hover] as number)} r={4.5}
                fill={`var(--series-${s.slot})`} stroke="var(--surface)" strokeWidth={2} />
            ))}
          </g>
        )}
      </svg>

      {hover != null && (
        <div
          className={styles.tooltip}
          style={{
            top: series.length > 1 ? 28 : 4,
            left: tipOnLeft ? undefined : tipLeft + 12,
            right: tipOnLeft ? width - tipLeft + 12 : undefined,
          }}
        >
          <div className={styles.tooltipDate}>{day(dates[hover], true)}</div>
          {series.map(s => (
            <div key={s.key} className={styles.tooltipRow}>
              <span className={styles.legendItem}>
                <span className={styles.swatch} style={{ background: `var(--series-${s.slot})` }} />
                {s.label}
              </span>
              <span>{s.values[hover] == null ? '—' : money(s.values[hover] as number)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
