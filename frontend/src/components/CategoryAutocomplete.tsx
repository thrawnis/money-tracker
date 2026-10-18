import { useState, type KeyboardEvent, type RefObject } from 'react';
import type { Category } from '../types';
import { flattenCategories } from '../utils/categories';
import styles from './TransactionForm.module.css';

/**
 * Category text box with type-ahead suggestions ("Parent: Child" labels),
 * shared by the transaction form and the register's bulk category edit.
 */
export default function CategoryAutocomplete({
  value, onChange, categories, className, placeholder, tabIndex, autoFocus, inputRef,
}: {
  value: string;
  onChange: (val: string) => void;
  categories: Category[];
  className?: string;
  placeholder?: string;
  tabIndex?: number;
  autoFocus?: boolean;
  inputRef?: RefObject<HTMLInputElement | null>;
}) {
  const [suggestions, setSuggestions] = useState<{ id: number; label: string }[]>([]);
  const [show, setShow] = useState(false);
  const [highlight, setHighlight] = useState(-1);

  const updateSuggestions = (val: string) => {
    if (val.length >= 1) {
      const flat = flattenCategories(categories);
      const filtered = flat.filter(c => c.label.toLowerCase().includes(val.toLowerCase()));
      setSuggestions(filtered.slice(0, 10));
      setShow(true);
      setHighlight(-1);
    } else {
      setShow(false);
      setHighlight(-1);
    }
  };

  const handleChange = (val: string) => {
    onChange(val);
    updateSuggestions(val);
  };

  const select = (entry: { id: number; label: string }) => {
    onChange(entry.label);
    setShow(false);
    setHighlight(-1);
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (!show || suggestions.length === 0) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setHighlight(h => (h + 1) % suggestions.length);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlight(h => (h - 1 + suggestions.length) % suggestions.length);
    } else if (e.key === 'Enter') {
      if (highlight >= 0) {
        e.preventDefault();
        select(suggestions[highlight]);
      }
    } else if (e.key === 'Escape') {
      setShow(false);
      setHighlight(-1);
    }
  };

  return (
    <div className={styles.fieldRelative}>
      <input
        ref={inputRef}
        type="text"
        className={className ?? styles.input}
        value={value}
        onChange={e => handleChange(e.target.value)}
        onKeyDown={handleKeyDown}
        onFocus={() => updateSuggestions(value)}
        onBlur={() => setTimeout(() => setShow(false), 150)}
        tabIndex={tabIndex}
        autoComplete="off"
        placeholder={placeholder}
        autoFocus={autoFocus}
      />
      {show && suggestions.length > 0 && (
        <ul className={styles.suggestions}>
          {suggestions.map((c, i) => (
            <li
              key={c.id}
              onMouseDown={() => select(c)}
              className={i === highlight ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}
            >
              {c.label}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
