# REQUIREMENTS.md

This document records the core functionality requirements for the Money Tracker app.
Update this file whenever requirements change or new features are defined.

---

## Authentication & Security

- Users register with email and password (12+ chars, mixed case, digit)
- Login issues a short-lived JWT access token (15 min) and a httpOnly refresh token cookie
- TOTP-based MFA: users can enroll; enrolled users must verify on each login
- Optional WebAuthn/FIDO2 passkey login (build flag `FIDO2_AVAILABLE`)
- Password change requires current password; blocked for demo account
- "Sign out all sessions" invalidates all refresh tokens for the user
- Account lockout after repeated failed login attempts — applies to passwords, TOTP codes, and export re-authentication
- MFA step-2 endpoints (TOTP setup/enroll/verify, passkeys) require a session that has completed password verification — a userId alone is never sufficient
- Auth and demo endpoints are rate-limited per IP (30 requests/minute)
- Expired access tokens are silently refreshed and the request retried (axios interceptor); a failed refresh redirects to login

---

## Demo Mode

- Enabled via `DEMO_MODE=true` environment variable
- Demo user: `demo@example.com` / `Demo123456!!` — pre-seeded with realistic household data
- Demo user bypasses MFA at login
- Demo user's password cannot be changed
- Settings page shows a "Reset Demo Data" button (demo mode only); calls `POST /api/demo/reset`

---

## Accounts

- Account types: Checking, Savings, Credit Card, Cash, Loan, Investment, Other
- Each account has: name, type, opening balance, institution (optional), account number (encrypted, optional), notes (encrypted, optional)
- Accounts can be marked active or inactive (via the edit form's toggle) without deleting them; inactive accounts are hidden from new-entry dropdowns but stay visible/collapsible in the Accounts list
- Deleting an account is permanent: the account, all its transactions (and splits), and its scheduled transactions are removed. Any transfer whose other leg lived in the deleted account is unlinked (kept as a plain transaction) rather than left pointing at a deleted row. Deletion requires password/TOTP re-authentication (same short-lived, single-use token as Export) in addition to a destructive-action confirmation and an optional backup note
- Before an account is deleted, a full JSON backup (account details + every transaction + splits, with sensitive fields still encrypted exactly as stored — never written to disk as plaintext) is saved server-side, timestamped, with an optional user-supplied note (auto-generated if omitted). Up to 3 backups are kept per account; the oldest is pruned when a new one is created for that same account (each account has its own independent history). Backups are listable from Settings → Account Backups; downloading one — even though it's still ciphertext — requires the same password/TOTP re-authentication as Export
- Accounts page (`/accounts`) lists all accounts grouped by type (active) with balance, first/last transaction date, and transaction count; inactive accounts are collapsible at the bottom
- Account register (`/accounts/:id`) shows all transactions for the account. A "Today" divider separates past/current transactions from everything future-dated (upcoming scheduled transactions, or the "no upcoming" placeholder, plus any manually-entered future-dated transactions) — the future group renders above Today when sorted newest-first (the default, since future dates sort ahead of today) and below Today when sorted oldest-first by date
- Account name in the register header is a dropdown to navigate between accounts (active first, then inactive)

---

## Transactions

- Fields: date, post date (optional), amount, payee (optional), category (optional), memo (encrypted, optional), check number (encrypted, optional), status (Uncleared / Cleared / Reconciled)
- Create, edit, delete on the account register page. Editing a transaction marked Cleared or Reconciled prompts a confirmation first, warning that the change may affect the reconciled balance
- **Split transactions**: a transaction can be divided across multiple categories, each with its own amount and memo (encrypted), summing exactly to the transaction total. One payee and one total amount per transaction — only the category breakdown is split. Not supported on transfer legs. The register, All Transactions, search, and the transaction detail panel show "Split (N)" in place of a single category name; the edit form shows the full per-split breakdown. QIF import parses S/E/$ split lines into real splits (falls back to a plain transaction with a warning if the split amounts don't add up to the total)
- All unsaved form changes must prompt before navigating away
- Bulk CSV, loose-QIF, and JSON import with preview and duplicate detection (per account); OFX/QFX not yet implemented
- QIF import is tolerant: unrecognized field codes and malformed lines are skipped rather than failing the file; supports both a single !Type section (no embedded account — the user selects a destination account) and multi-account exports using !Account blocks; !Type:Invst sections are skipped with a warning (no security/quantity data model)
- JSON import accepts this app's own JSON export format (array of account/transaction groups, including splits and subcategories) and re-imports it through the same pipeline as CSV/QIF — every field is re-encrypted on write, exactly like every other import path; the plaintext JSON is never itself persisted to disk or the database
- CSV/QIF/JSON import auto-detects transfers: an imported row is linked to an existing unlinked transaction in another of the user's accounts when date, opposite-sign amount, and memo all match (ambiguous multi-candidate matches are left unlinked); also matches transfer pairs within a single multi-account import file
- Duplicate detection also catches rows that exactly repeat an earlier row *within the same import file* (same account, date, and amount), not just rows that already exist in the database — these are always skipped automatically (no include-anyway option) and called out in preview warnings / import result errors, so a file with accidental repeated lines can't insert the same transaction twice
- Clicking a transaction row in the account register opens it for editing (the new/edit transaction form renders below the register table and scrolls into view); right-click a transaction for a context menu instead, which also offers a "Go to Other Account" option on transfers that jumps to and highlights the matching leg
- Transaction search across accounts with: date range, account, category, payee, regex memo/check# pattern
- Clicking a transaction in the All Transactions list opens a slide-in detail panel (view, edit, or delete in place) instead of navigating away; the panel's "Open in Register" action still jumps to and highlights it in the account register

---

## Transfers

- Transfer moves money between two of the user's accounts
- Creates a matching transaction on each side; both are deleted together
- Transfers show the destination/source account name in the transaction list

---

## Categories

- Hierarchical: optional parent category per category
- Names are case-insensitively unique per user (enforced in memory after decryption)
- Category name is encrypted (AES-256-GCM)
- Create, edit, delete on Categories page; deletions blocked if the category has subcategories, or is referenced by a transaction, a transaction split, or a scheduled transaction

---

## Payees

- Each payee can have a default category
- Payee name is encrypted; unique per user (enforced in memory)
- Payees page: list, create, edit, delete
- Last-used date tracked automatically
- Deletions blocked if payee is referenced by a transaction

---

## Bills & Reminders (Scheduled Transactions)

- Scheduled transaction fields: name, account, amount, payee (optional), category (optional), memo (encrypted, optional), next due date, frequency (N Days/Weeks/Months/Years), reminder days before due
- Supports scheduled transfers between accounts
- Bills can be active or inactive
- Bills & Reminders page shows upcoming items sorted by due date
- **Automatic posting**: a background job (every 6 hours and at startup) materializes due scheduled transactions into real transactions (Uncleared status) and advances the next due date; missed occurrences are back-filled

---

## Reports

- **Monthly Summary**: income vs. expense bar/line chart for last N months; filterable by accounts and categories
- **Transactions by Category**: totals per category for a date range; filterable by account
- Both reports are split-aware: a split transaction's amount is attributed to each split's own category rather than the whole transaction landing in one bucket
- Reports can be saved (name, type, parameters); one report can be set as the default
- Date range defaults: current month or last 6 months depending on report type

---

## Settings

- **Password**: change password with current-password confirmation
- **Export**: download all user data in QIF, OFX, CSV, XLSX, or JSON format; requires re-authentication. The format picker shows a brief description with pros/cons for each format; JSON is listed first and marked "Preferred for backup / restore" since it's the only format that round-trips losslessly through this app's own JSON importer
- **Import**: CSV, loose-QIF, or JSON (this app's own export format) upload with transaction preview and duplicate detection (per account); OFX/QFX not yet implemented
- **Accounts & Institutions**: manage account details and institution list
- **Audit Log**: users see their own activity; admins see all users
- **Demo Reset** (demo mode only): wipes and re-seeds the demo user's data

---

## Data Integrity & Security

- All sensitive text fields encrypted at rest with AES-256-GCM per-user DEK
- Decrypted (plaintext) data is never persisted server-side, in the database or as a file — the only plaintext export path is the on-demand, re-authenticated Export download (streamed directly to the browser, never written to disk); all imports (CSV/QIF/JSON) and account backups encrypt/keep-encrypted every sensitive field before it touches the database or disk
- Every API endpoint enforces per-user data isolation; no cross-user data leakage — including FK references (payee/category/account IDs in requests are verified against the caller)
- Transfers created and deleted as an atomic pair (wrapped in DB transactions)
- Unique-name enforcement for accounts, payees, categories, institutions (case-insensitive)
- Running balances are computed server-side over the full account history in date order, independent of pagination, filters, or sort
- Account balances exclude future-dated transactions ("as of today") consistently across Dashboard, Accounts page, and register
- Nightly `pg_dump` backups via the `db-backup` compose service (default 14-day retention, `./data/backups`)
- Per-account backups (see Accounts) via `./data/account-backups/{userId}/{accountId}/`, max 3 per account, sensitive fields kept encrypted in the backup file

---

## UX Standards

- All forms with unsaved data warn before navigation (in-app via `useBlocker`, browser-level via `beforeunload`)
- All destructive actions (delete, deactivate) require a `confirm()` dialog
- Inactive accounts and categories remain visible in historical data but are excluded from new-entry dropdowns (or shown at the bottom, grayed out)
