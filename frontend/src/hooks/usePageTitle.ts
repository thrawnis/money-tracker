// The tab title is deliberately always "Money Tracker" — page-specific titles
// would leak account and payee names into browser history and screen shares.
// The argument is kept so call sites read naturally and can be given meaning
// later without touching every page.
export function usePageTitle(_title: string) {
  void _title;
}
