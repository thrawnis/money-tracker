# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Working Style

- **Always work on the `dev` branch** unless explicitly told otherwise. Never push to `main` without permission.
- **Quality over speed.** Thoroughly review code — especially frontend — before committing. Read the relevant files, understand the context, and verify the change is correct end-to-end.
- **Accuracy over assumptions.** If the intent of a request is unclear, or a change touches something with non-obvious side effects, ask before proceeding.
- **Consult REQUIREMENTS.md** before implementing features or making structural changes. Keep it updated when requirements change.

---

## Commands

All commands run from their respective directories unless noted.

**Backend** (`/backend`):
```bash
dotnet build
dotnet run
dotnet test                          # xUnit + Testcontainers (requires Docker)
dotnet ef database update            # manual migration (auto-applied on startup)
dotnet build /p:FIDO2_AVAILABLE=true # build with passkey support
```

**Frontend** (`/frontend`):
```bash
npm ci
npm run dev      # Vite dev server → http://localhost:3000
npm run build    # TypeScript compile + Vite bundle
npm run lint     # ESLint
```

**Full stack (Docker)**:
```bash
./rebuild.sh     # pull dev branch, rebuild images, restart containers
docker compose up
```

---

## Architecture

### Stack
- **Backend**: ASP.NET Core 8 · Entity Framework Core 8 · PostgreSQL 16
- **Frontend**: React 19 · React Router 7 (`createBrowserRouter`) · TypeScript · Vite · Axios
- **Auth**: JWT (15-min access tokens) + httpOnly refresh token cookies · TOTP MFA · optional WebAuthn/FIDO2 passkeys (`FIDO2_AVAILABLE` build flag)
- **Deployment**: Docker Compose — `api` (port 5012), `frontend` nginx (port 3012), `db` (Postgres)

### Authentication Flow
1. `POST /auth/login` → password check → if MFA enrolled, returns `{ requiresMfa, userId }` → client calls `/auth/mfa/totp/verify` or `/passkey/login/*`
2. Tokens issued via `IssueTokensAsync`: JWT access token + refresh token stored in DB, set as httpOnly cookie
3. Silent refresh on app load and tab focus (`AuthContext`)
4. Demo user (`demo@example.com`) skips MFA when `DEMO_MODE=true`

### Field-Level Encryption
Sensitive fields are encrypted with AES-256-GCM using a **per-user Data Encryption Key (DEK)** that is itself encrypted with a master key (`ENCRYPTION_KEY` env var). Call `IEncryptionService` for all reads/writes of:
- `Transaction.MemoEncrypted`, `Transaction.CheckNumberEncrypted`
- `Payee.NameEncrypted`, `Category.NameEncrypted`
- `Account.AccountNumberEncrypted`, `Account.NotesEncrypted`

Because payee and category names are encrypted, **DB-level unique constraints are impossible** for those fields — uniqueness is enforced by loading all records and comparing in memory after decryption.

### Transfer Transactions
Transfers are two linked `Transaction` rows. Each row carries:
- `TransferAccountId` — the other account's ID (plain int, for display)
- `TransferTransactionId` — FK to the other transaction row (for delete cascade)

Always create/delete both sides together. See `TransfersController.cs`.

### Unique Name Enforcement
All names (accounts, payees, categories, institutions) must be **case-insensitively unique per user**. Pattern:
- Plaintext fields (accounts, institutions): `db.Table.AnyAsync(x => x.Name.ToLower() == dto.Name.ToLower())`
- Encrypted fields (payees, categories): load all, decrypt, compare in memory
- Return `409 Conflict` with `{ message: "A ... named \"X\" already exists." }` on duplicate

### Frontend Patterns
- **React Router**: uses `createBrowserRouter` (required for `useBlocker` / data router features)
- **API layer**: one file per resource in `src/api/`. All calls go through the Axios client in `src/api/client.ts`, which injects the JWT Bearer token
- **Error handling**: always extract `(err as ApiError)?.response?.data?.message` before falling back to a generic message
- **CSS**: CSS Modules for all component styles (`.module.css` files)
- **Unsaved changes**: use the `useUnsavedChanges(isDirty)` hook on any page with a form. It uses `useBlocker` for in-app navigation and `beforeunload` for browser-level navigation
- **Destructive actions**: always require a `confirm()` dialog before deleting or deactivating anything
- **Page titles**: always call `usePageTitle('Money Tracker')` — do not use page-specific titles

### Data Ownership
Every controller action must filter by `UserId` from the JWT claim (`GetUserId()`). Never return or mutate data belonging to a different user.

### Demo Mode
Enabled with `DEMO_MODE=true`. On startup, `DemoSeeder.SeedIfNeededAsync()` creates `demo@example.com` (password `Demo123456!!`) with realistic household data. Password changes are blocked for this account. `POST /api/demo/reset` wipes and re-seeds the demo user.

### Database Migrations
EF Core migrations live in `backend/Migrations/`. They are applied automatically via `db.Database.Migrate()` in `Program.cs` on startup. To add a migration:
```bash
dotnet ef migrations add <MigrationName>
```

---

## Key Invariants to Preserve

- Encrypted fields must never be stored as plaintext
- All controller actions enforce per-user data isolation via `UserId`
- Transfers must always be created and deleted as a pair
- Case-insensitive uniqueness must be enforced for all name fields
- All forms must guard against accidental navigation away with unsaved data
- Destructive UI actions must confirm before proceeding
