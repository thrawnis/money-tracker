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
