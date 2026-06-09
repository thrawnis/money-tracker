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
- Account lockout after repeated failed login attempts

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
- Accounts can be marked active or inactive; inactive accounts are hidden but not deleted
- Accounts page (`/accounts`) lists all accounts grouped by type (active) with balance and last transaction date; inactive accounts are collapsible at the bottom
- Account register (`/accounts/:id`) shows all transactions for the account
- Account name in the register header is a dropdown to navigate between accounts (active first, then inactive)

---

## Transactions

- Fields: date, post date (optional), amount, payee (optional), category (optional), memo (encrypted, optional), check number (encrypted, optional), status (Uncleared / Cleared / Reconciled)
- Create, edit, delete on the account register page
- All unsaved form changes must prompt before navigating away
- Bulk CSV import with preview and duplicate detection (per account)
- Transaction search across accounts with: date range, account, category, payee, regex memo/check# pattern

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
- Create, edit, delete on Categories page; deletions blocked if the category is in use

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

---

## Reports

- **Monthly Summary**: income vs. expense bar/line chart for last N months; filterable by accounts and categories
- **Transactions by Category**: totals per category for a date range; filterable by account
- Reports can be saved (name, type, parameters); one report can be set as the default
- Date range defaults: current month or last 6 months depending on report type

---

## Settings

- **Password**: change password with current-password confirmation
- **Export**: download all user data in QIF, OFX, CSV, XLSX, or JSON format; requires re-authentication
- **Import**: CSV or OFX upload with transaction preview and duplicate detection (per account)
- **Accounts & Institutions**: manage account details and institution list
- **Audit Log**: users see their own activity; admins see all users
- **Demo Reset** (demo mode only): wipes and re-seeds the demo user's data

---

## Data Integrity & Security

- All sensitive text fields encrypted at rest with AES-256-GCM per-user DEK
- Every API endpoint enforces per-user data isolation; no cross-user data leakage
- Transfers created and deleted as an atomic pair
- Unique-name enforcement for accounts, payees, categories, institutions (case-insensitive)

---

## UX Standards

- All forms with unsaved data warn before navigation (in-app via `useBlocker`, browser-level via `beforeunload`)
- All destructive actions (delete, deactivate) require a `confirm()` dialog
- Inactive accounts and categories remain visible in historical data but are excluded from new-entry dropdowns (or shown at the bottom, grayed out)
