/**
 * The browser's own IANA zone, used to preselect the right answer for most
 * people at sign-up and on the mandatory picker.
 *
 * Kept out of TimeZoneSelect.tsx so that file exports only its component —
 * Fast Refresh drops component state for modules that also export plain
 * functions.
 */
export function detectTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || '';
  } catch {
    return '';
  }
}

/**
 * Formats a server ISO timestamp (Transaction.createdAt/updatedAt — real UTC
 * instants, unlike the plain `date`/`postDate` calendar-date strings) in the
 * given IANA zone, e.g. "Aug 23, 2026, 3:45 PM". Falls back to the browser's
 * own zone when none is given, and to Intl's own default if that zone id is
 * somehow invalid — never throws just because a timestamp needs displaying.
 */
export function formatDateTime(iso: string, timeZoneId?: string): string {
  const tz = timeZoneId || detectTimeZone() || undefined;
  try {
    return new Date(iso).toLocaleString('en-US', {
      timeZone: tz,
      year: 'numeric', month: 'short', day: 'numeric',
      hour: 'numeric', minute: '2-digit',
    });
  } catch {
    return new Date(iso).toLocaleString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: 'numeric', minute: '2-digit',
    });
  }
}
