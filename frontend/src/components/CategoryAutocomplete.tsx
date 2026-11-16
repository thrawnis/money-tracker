import type { ComponentProps } from 'react';
import type { Category } from '../types';
import { flattenCategories } from '../utils/categories';
import NameAutocomplete from './NameAutocomplete';

/**
 * Category text box with type-ahead suggestions ("Parent: Child" labels),
 * shared by the transaction form, the register's bulk category edit and others.
 */
export default function CategoryAutocomplete({ categories, ...rest }:
  Omit<ComponentProps<typeof NameAutocomplete>, 'items'> & { categories: Category[] }) {
  return <NameAutocomplete items={flattenCategories(categories)} {...rest} />;
}
