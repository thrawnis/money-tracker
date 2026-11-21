/**
 * "What's New" changelog: the banner at the top of the app shows the last
 * WHATS_NEW_DAYS days; the What's New page (/whats-new) keeps everything.
 *
 * Add an entry for every change users would notice in what the app does — new
 * features and changed behavior — written in plain, non-technical language.
 * Skip purely visual tweaks and behind-the-scenes changes. Newest date first,
 * one entry per date (days with nothing are simply absent).
 */
export interface WhatsNewEntry {
  /** YYYY-MM-DD, the day the change shipped. */
  date: string;
  items: string[];
}

export const WHATS_NEW_DAYS = 14;

export const WHATS_NEW: WhatsNewEntry[] = [
  {
    date: '2026-11-21',
    items: [
      'IOU payment requests — the page, the email, and the message copied by Copy pay link (ready to paste into a text) — now list each item oldest to newest with its amount, then the total at the bottom.',
      'Every search and type-to-search box now ignores upper/lower case and understands wildcards: * matches anything and ? matches one character (for example "amazon*" finds payees starting with Amazon, "*coffee*" anything containing coffee). Plain text without wildcards still finds names containing it.',
      'Backups are now marked Full or Partial, and Details shows exactly what each one contains. Downloaded backups include a readable list of their contents too.',
      'Daily backups: everything in your Money Tracker account is backed up automatically every evening (just before the nightly bank sync) as an encrypted file, and the last 7 are kept. Settings → Backups & Restore lets you choose what\'s included, back up any time, and download a copy protected with your own password.',
      'Restore: put your whole account back to an earlier day, or just parts of it — one account (replacing it or as a copy), categories, payees, bills, IOUs, reports or settings. Your data is backed up again first, so a restore can always be undone.',
    ],
  },
  {
    date: '2026-11-20',
    items: [
      'Imports and bank sync now remember what the bank or file originally said about each transaction (payee text, memo, date, amount and its bank ID) — shown as "Imported as" when you open it.',
      'Smarter duplicate checks when importing: transactions already imported with the same bank ID are skipped, likely duplicates are found even after you renamed the payee or changed the date, and each match tells you why it was flagged.',
    ],
  },
  {
    date: '2026-11-18',
    items: [
      'Loans: add a loan\'s rate, term and payment to see what you owe, when it will be paid off, how much interest is left and the full payment schedule — plus a "what if I paid extra each month?" calculator. Loans synced from your bank as "balance only" use your bank\'s balance.',
      'Loans: optionally have each payment\'s interest (and escrow) added to the loan automatically, so the loan balance goes down by the principal only and interest shows up in your spending.',
    ],
  },
  {
    date: '2026-11-17',
    items: [
      'Ask AI: the assistant\'s behind-the-scenes reasoning is no longer shown — you just see the steps it\'s taking (now including "Working it out…") and then the answer.',
    ],
  },
  {
    date: '2026-11-16',
    items: [
      'IOUs: send someone a private link (copy it or email it) to a page showing what they owe you — date, payee and amount for each — with one-tap buttons to pay you by Venmo, PayPal, Cash App, Zelle, Apple Cash, Google Pay or your own methods. Links work for 30 days, and you can turn them off any time. Add your payment details with the new Payment details button.',
      'IOUs: when someone taps "I\'ve sent it" on their payment page, you get an email and a note on the IOUs page so you can record the payment.',
      'Ask AI: a new chat offers four different starter questions each time, chosen to fit what you\'ve allowed the assistant to see.',
      'Ask AI can now suggest changes — categorizing transactions, linking transfers, renaming or merging payees, adding payee mapping rules and creating bills. Each suggestion shows exactly what would change, and nothing happens until you click Apply. Turn it on in Settings → AI Assistant.',
      'Ask AI: see what the assistant is doing while it works ("Searching transactions…", "Adding up spending…"), and watch the answer appear as it\'s written.',
      'Emails: the weekly summary\'s subject line no longer shows how much you spent — amounts only appear inside the email.',
      'Bills & Reminders: a new Create column shows which bills enter their upcoming transactions automatically — tick or untick it right there to change it.',
      'New: Ask AI — ask questions about your money in plain English ("How much did I spend on restaurants this year?"). Turn it on, and choose exactly what it may look up, in Settings → AI Assistant (the AI server itself is set up by whoever runs Money Tracker). It can only read, never change anything, and your conversations are saved encrypted.',
      'Payees: merge a payee into another one with the new Merge button — all its transactions and bills move over, and future imports with the old name go to the payee you kept.',
      'Questions and confirmations (like "Delete this payee?") now appear in the app\'s own style instead of your browser\'s pop-up boxes.',
      'Payees: click a payee\'s default category to change it, and type to search for the category, the same way as everywhere else.',
      'Account register: transactions linked to an IOU now show a simple 👥 icon. Hover over it to see who owes what and how much has been paid back.',
      'Account register: split transactions now list each part — its category, memo and amount — in lighter text underneath. You can turn this off with "Show split details" in the register\'s ⚙ menu.',
      'Possible Transfers: click any transaction to see it in its account. Going back brings you right back to where you were.',
    ],
  },
  {
    date: '2026-11-15',
    items: [
      'Possible Transfers: when a transaction could go with more than one other, all the choices are shown together so you can pick the right one — or say none of them.',
      'Possible Transfers: pairs you\'ve dealt with now stay on screen (faded out) instead of disappearing, so the list doesn\'t jump around while you scroll.',
      'Bills & Reminders: you now choose for each bill whether it should be entered ahead of time automatically, instead of one setting for all bills. New bills start with this off; your existing bills kept the setting you had.',
    ],
  },
  {
    date: '2026-11-14',
    items: [
      'Bank sync: a new "Require my approval before importing" setting (on unless you turn it off). Turn it off and new bank transactions are added to your accounts automatically, without a review step.',
      'Possible Transfers now also looks at transactions that already have a category.',
      'On phones: a Back button at the top of the screen when you use the app from your Home Screen, and pages no longer get cut off.',
    ],
  },
  {
    date: '2026-11-13',
    items: ['Bank Sync now lives on the Accounts page.'],
  },
  {
    date: '2026-11-12',
    items: [
      'New tool in Utilities — Possible Transfers: spots money that left one of your accounts and arrived in another (like a credit card payment) but was recorded as two separate transactions, and lets you join them into a transfer.',
      'New tool in Utilities — Uncategorized by Payee: categorize lots of transactions at once, payee by payee, with the categories you\'ve used before one click away.',
      'Bank sync now shows when each account last synced successfully.',
      'Payee Mapping Rules can be sorted by any column.',
      'If email is set up, signing in on a new account asks you to confirm your email address with a code.',
    ],
  },
  {
    date: '2026-11-11',
    items: ['Email alerts when bank sync brings in new or unusually large transactions, plus an optional weekly summary email.'],
  },
  {
    date: '2026-11-09',
    items: [
      'Sign in with just a passkey (Face ID, Touch ID, Windows Hello…) — no password needed.',
      'An optional daily summary email, and an email whenever someone signs in to your account.',
      'Cash-flow forecast: see where your balances are heading over the coming weeks, with a warning if an account looks like it will run low.',
      'Spending trends: compare this month\'s spending with your usual pace.',
      'Dashboard: give an uncategorized transaction a category just by clicking it, and optionally make it the payee\'s usual category.',
      'New tool in Utilities — Duplicate Names: find and combine payees or categories that are really the same thing.',
      'Bank Sync: create a new account and connect it to your bank in one step.',
      'Settings, Reports and Utilities remember which tab you had open.',
    ],
  },
  {
    date: '2026-10-26',
    items: [
      'IOUs: split a new transaction with someone right after saving it, or from the right-click menu.',
      'Sign-in security: use a passkey as your second sign-in step (an authenticator app still works too).',
      'Your payee mapping rules can now be applied to transactions that are already uncategorized.',
      'Receipt scanning no longer puts the restaurant name in the memo.',
    ],
  },
  {
    date: '2026-10-24',
    items: [
      'New IOUs page: keep track of who owes whom — split a transaction with someone, record repayments, and see running totals. There\'s also a Dashboard tile.',
    ],
  },
  {
    date: '2026-10-18',
    items: [
      'Account register: change the category of several selected transactions at once.',
      'Account register: transactions missing a payee or category are flagged in red, and transfers are clearly labeled.',
      'Clearer status icons in the register — blue for cleared, green for reconciled.',
    ],
  },
  {
    date: '2026-10-10',
    items: ['Editing a cleared transaction no longer asks for confirmation first.'],
  },
  {
    date: '2026-10-09',
    items: [
      'Bank sync reviews: go through waiting accounts one after another, with "Import New Only" as the main button.',
      'Import review: see when a payee\'s name will be changed, and possible duplicates are flagged with a side-by-side comparison.',
    ],
  },
  {
    date: '2026-10-05',
    items: ['Imported transactions without a category get the payee\'s usual category.'],
  },
  {
    date: '2026-10-03',
    items: ['Investment accounts connected to your bank show the value your bank reports.'],
  },
  {
    date: '2026-10-02',
    items: [
      'Bank sync runs automatically every night.',
      'Investment accounts can sync just their balance, with a daily value history and a holdings view showing gains and losses.',
      'New Balances Over Time chart, for your net worth and for each account.',
      'Bank sync settings moved to the Accounts page, and import review shows which account transactions are going into.',
      '"Remember me" now keeps you signed in for two months instead of two weeks.',
      'Slightly larger text throughout the app.',
    ],
  },
  {
    date: '2026-09-29',
    items: ['New Bank Sync screen: choose which bank accounts to sync, and untick anything you don\'t want when reviewing an import.'],
  },
  {
    date: '2026-09-28',
    items: ['Bank sync: connect your banks through SimpleFIN to bring in transactions automatically.'],
  },
  {
    date: '2026-09-20',
    items: ['Dark mode now darkens the whole page.'],
  },
  {
    date: '2026-09-06',
    items: [
      'Dashboard: hide account balances with one click, and jump from "Uncategorized Transactions" straight to the full list.',
    ],
  },
  {
    date: '2026-08-24',
    items: [
      'Install the app to your phone\'s Home Screen, and it follows your device\'s dark mode.',
      'Account register: filter by status and show or hide voided transactions.',
      'Opening a transaction shows when it was added.',
      'Deleting, voiding and saving in the register feel instant, and long registers load faster.',
      'Categories are listed alphabetically, and inactive accounts are no longer offered for new transactions.',
    ],
  },
  {
    date: '2026-08-23',
    items: [
      'Void a transaction instead of deleting it.',
      'Duplicate a transaction from its row in the register.',
      'A keyboard shortcuts help screen (see the link at the bottom of the menu).',
      'A new payee automatically remembers the category of its first transaction — and you can turn this off for a payee.',
      'Typing a new category name asks before creating it.',
      'When adding a transaction, pick which account it goes in.',
      'Set your time zone so dates and reminders line up with your day.',
      'Saving an edited transaction keeps your place in the register.',
    ],
  },
  {
    date: '2026-08-22',
    items: [
      'Do math in the Amount field (like 12.50+8.25), with a live preview.',
      'Switch the Amount field between Expense and Income.',
      'A newly added transaction is scrolled to and highlighted in the register.',
      'Better keyboard support when picking categories in a split transaction.',
    ],
  },
  {
    date: '2026-07-20',
    items: [
      'Make any transaction recurring from its right-click menu.',
      'Payee mapping rules: rename messy imported payees automatically. Unfinished imports are saved so you can come back to them.',
      'Find Duplicates: compare a group side by side, filter by account, and mark groups that aren\'t really duplicates.',
      'New Utilities menu, home to Find Duplicates, Institutions and Payee Mapping Rules.',
      'A bill or reminder no longer needs a name.',
    ],
  },
  {
    date: '2026-07-19',
    items: [
      'Bills & Reminders: sort by any column, and new or edited bills take effect right away.',
      'Find Duplicates: differences between transactions are highlighted, with a link to jump to each one.',
    ],
  },
];

const startOfToday = () => { const d = new Date(); d.setHours(0, 0, 0, 0); return d; };

/** Entries from the last WHATS_NEW_DAYS days, newest first. */
export function recentChanges() {
  const cutoff = startOfToday();
  cutoff.setDate(cutoff.getDate() - WHATS_NEW_DAYS);
  return WHATS_NEW.filter(e => new Date(e.date + 'T00:00:00') >= cutoff && e.items.length > 0);
}

export const formatChangeDate = (d: string, withYear = false) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US',
    { weekday: 'short', month: 'short', day: 'numeric', ...(withYear ? { year: 'numeric' } : {}) });
