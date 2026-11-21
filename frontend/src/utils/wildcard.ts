/**
 * The text matching used by every search, filter and type-to-search box —
 * the same rules as the server's WildcardPattern, so a pattern means the same
 * thing wherever it's typed:
 *
 *  - always case-insensitive;
 *  - `*` matches any run of characters and `?` exactly one; a pattern with
 *    either is matched against the whole value ("Amazon*" = starts with Amazon,
 *    "*coffee*" = contains coffee);
 *  - a pattern with neither is a plain "contains" search.
 *
 * Other characters (".", "(", "/" …) are matched literally.
 */
export function wildcardMatcher(pattern: string): (value: string | null | undefined) => boolean {
  const p = pattern.trim();
  if (!p) return () => true;
  if (!/[*?]/.test(p)) {
    const needle = p.toLowerCase();
    return v => (v ?? '').toLowerCase().includes(needle);
  }
  const body = p.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  const rx = new RegExp(`^${body}$`, 'is');
  return v => rx.test(v ?? '');
}

/** One-off form of wildcardMatcher. */
export const matchesWildcard = (value: string | null | undefined, pattern: string) => wildcardMatcher(pattern)(value);
