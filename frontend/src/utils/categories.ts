import type { Category } from '../types';

// Flatten category tree into searchable entries
export function flattenCategories(categories: Category[]): { id: number; label: string }[] {
  const flat: { id: number; label: string }[] = [];
  for (const cat of categories) {
    flat.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        flat.push({ id: sub.id, label: `${cat.name}: ${sub.name}` });
      }
    }
  }
  return flat;
}
