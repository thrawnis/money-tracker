# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Working Style

- **Work directly on `main`** and push all changes there. Do not create or use other branches.
- **Quality over speed.** Thoroughly review code — especially frontend — before committing. Read the relevant files, understand the context, and verify the change is correct end-to-end.
- **Accuracy over assumptions.** If the intent of a request is unclear, or a change touches something with non-obvious side effects, ask before proceeding.
- **Consult REQUIREMENTS.md** before implementing features or making structural changes. Keep it updated when requirements change.
- **Update the What's New changelog** (`frontend/src/whatsNew.ts`) with every change users would notice in what the app does — new features or changed behavior — in plain, non-technical language, under that day's date (newest first; add to an existing entry for the same day). Skip purely visual tweaks and behind-the-scenes changes.

---

## Commands

All commands run from their respective directories unless noted.

**Backend** (`/backend`):
```bash
dotnet build
dotnet run
dotnet test                          # xUnit + Testcontainers (requires Docker)
dotnet ef database update            # manual migration (auto-applied on startup)
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
./rebuild.sh     # reset to latest origin/main (discards local changes; keeps .env and data/); rebuild + restart only if the running images aren't from the latest commit (--force to always rebuild); before restarting it waits up to 15 min for a running bank sync to finish (GET /api/system/busy, local requests only; --ignore-sync skips)
docker compose up
```

---

## Architecture

### Stack
- **Backend**: ASP.NET Core 8 · Entity Framework Core 8 · PostgreSQL 16
- **Frontend**: React 19 · React Router 7 (`createBrowserRouter`) · TypeScript · Vite · Axios
- **Auth**: JWT (15-min access tokens) + httpOnly refresh token cookies · required second factor: WebAuthn passkeys (default, Fido2 library) and/or TOTP authenticator app
- **Deployment**: Docker Compose — `api` (port 5012), `frontend` nginx (port 3012), `db` (Postgres)

### Authentication Flow
1. `POST /auth/login` → password check → if MFA enrolled, returns `{ requiresMfa, userId, methods: { passkey, totp } }` → client calls `/auth/passkey/login/*` (default) or `/auth/mfa/totp/verify`; if not enrolled, `{ requiresMfaSetup }` → first-time setup offers a passkey (recommended) or TOTP. Passwordless: `/auth/passkey/signin/begin|complete` signs in with a passkey alone (no allow-list; the credential identifies the user)
2. Tokens issued via `IssueTokensAsync`: JWT access token + refresh token stored in DB, set as httpOnly cookie. Email verification: when SMTP is configured (and `Auth:RequireEmailVerification` isn't false), an account whose `EmailConfirmed` is false gets `{ requiresEmailVerification, email }` instead of tokens and a 6-digit code by email; `/auth/email/verify` (then `IssueTokensAsync`) finishes the sign-in, `/auth/email/resend` resends. Refresh refuses unverified accounts. The frontend routes every sign-in result through `utils/finishSignIn.ts`
3. Silent refresh on app load and tab focus (`AuthContext`)
4. Demo user (`demo@example.com`) skips MFA when `DEMO_MODE=true`

### Field-Level Encryption
Sensitive fields are encrypted with AES-256-GCM using a **per-user Data Encryption Key (DEK)** that is itself encrypted with a master key (`ENCRYPTION_KEY` env var). Call `IEncryptionService` for all reads/writes of:
- `Transaction.MemoEncrypted`, `Transaction.CheckNumberEncrypted`
- `Payee.NameEncrypted`, `Category.NameEncrypted`
- `Account.AccountNumberEncrypted`, `Account.NotesEncrypted`
- `AiConversation.TitleEncrypted`, `AiMessage.ContentEncrypted`, `AiProposal.PayloadEncrypted`, `AiProposal.ResultEncrypted`

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
- **Dialogs**: never use the browser's `confirm()` / `prompt()` / `alert()`. Use the themed `confirmDialog` / `promptDialog` / `alertDialog` from `src/utils/dialogs.ts` (promise-based: `if (!(await confirmDialog('Delete …?'))) return;`), rendered by `DialogHost` in `App.tsx`. The only browser dialog left is the tab-close/refresh warning (`beforeunload`), which browsers don't allow to be replaced
- **Destructive actions**: always require a `confirmDialog` before deleting or deactivating anything
- **Page titles**: always call `usePageTitle('Money Tracker')` — do not use page-specific titles

### AI Assistant
The AI server is configured only in `.env` (`AI_PROVIDER`/`AI_BASE_URL`/`AI_API_KEY`/`AI_MODEL`; receipt scanning uses them too unless `RECEIPT_*` are set) — resolved in `GeneralAi`. `AiAssistant` sends questions to its OpenAI-compatible `/chat/completions` API with read-only tools from `AiTools`. Lookup tools must stay read-only, filter by the user, respect the user's `AiShare*` flags, and never include account numbers or notes. Changes go only through `AiProposals`: a `propose_*` tool records a proposal (no writes); the user applies it from the Ask page, and `ApplyAsync` re-validates everything then.

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
