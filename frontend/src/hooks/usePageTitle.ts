import { useEffect } from 'react';

export function usePageTitle(title: string) {
  useEffect(() => {
    document.title = `${title} - Money Tracker`;
    return () => { document.title = 'Money Tracker'; };
  }, [title]);
}
