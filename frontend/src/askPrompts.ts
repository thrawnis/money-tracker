import type { AiShare } from './api/ai';

/**
 * Starter questions for the Ask AI page: a new chat shows 4 at random, from
 * those that work with what the user shares with the assistant. Each lists
 * what it needs ('proposals' = "Allow the assistant to suggest changes").
 */
type Need = keyof AiShare | 'proposals';
export interface AskPrompt { text: string; needs: Need[] }

const P = (text: string, ...needs: Need[]): AskPrompt => ({ text, needs });

export const ASK_PROMPTS: AskPrompt[] = [
  // Spending
  P('How much did I spend on restaurants this month compared with last month?', 'transactions'),
  P('What are my top 5 spending categories this year?', 'transactions'),
  P('Where did most of my money go last month?', 'transactions'),
  P('How does my spending this month compare with my usual month?', 'transactions'),
  P('Which category has grown the most compared with last year?', 'transactions'),
  P('How much have I spent on groceries in the last 3 months?', 'transactions'),
  P('What did I spend on gas and car costs this year?', 'transactions'),
  P('Show my spending by month for the last 6 months.', 'transactions'),
  P('How much do I spend eating out per week, on average?', 'transactions'),
  P('Which payees did I spend the most with this year?', 'transactions'),
  P('What were my biggest purchases over $200 in the last 90 days?', 'transactions'),
  P('How much did I spend on Amazon this year?', 'transactions'),
  P('What did I spend over the holidays last December?', 'transactions'),
  P('Which months this year was my spending highest?', 'transactions'),
  // Income & saving
  P('How much income did I receive this year?', 'transactions'),
  P('Did I spend more than I earned last month?', 'transactions'),
  P('How much have I moved into savings this year?', 'transactions'),
  P('What was my net cash flow for each of the last 3 months?', 'transactions'),
  // Finding things
  P('Find all transactions with Comcast this year.', 'transactions'),
  P('When did I last pay my car insurance, and how much was it?', 'transactions'),
  P('Are there any charges that look like duplicates in the last month?', 'transactions'),
  P('Which subscriptions am I paying for, and how much do they cost per month?', 'transactions'),
  P('Did any of my regular bills go up in price recently?', 'transactions'),
  P('Show any transactions over $500 from the last 60 days.', 'transactions'),
  P('What did I buy at Costco recently?', 'transactions'),
  P('How many uncategorized transactions do I have, and from which payees?', 'transactions'),
  P('Which payees have I only used once?', 'transactions'),
  // Balances
  P('What are my account balances right now?', 'balances'),
  P('What is my net worth today?', 'balances'),
  P('How much do I owe across my credit cards and loans?', 'balances'),
  P('How much cash do I have in checking and savings combined?', 'balances'),
  // Bills
  P('Which bills are due in the next two weeks?', 'bills'),
  P('Do I have any overdue bills?', 'bills'),
  P('What do my recurring bills add up to each month?', 'bills'),
  P('Which is my most expensive recurring bill?', 'bills'),
  P('Will I have enough in checking to cover this month\'s bills?', 'bills', 'balances'),
  // IOUs
  P('Who owes me money right now?', 'ious'),
  P('How much do I owe other people?', 'ious'),
  P('Which IOUs have been open the longest?', 'ious'),
  // Mixed / reviews
  P('Give me a quick summary of my finances this month.', 'transactions', 'balances'),
  P('Anything unusual in my spending this month?', 'transactions'),
  P('What could I cut back on to save $200 a month?', 'transactions'),
  // Suggested changes
  P('Categorize my uncategorized transactions from the last month.', 'transactions', 'proposals'),
  P('Find transfers between my accounts that aren\'t linked yet.', 'transactions', 'proposals'),
  P('Are any of my payees duplicates that should be merged?', 'proposals'),
  P('Suggest payee mapping rules for messy imported payee names.', 'transactions', 'proposals'),
  P('Do I have recurring payments that should be set up as bills?', 'transactions', 'bills', 'proposals'),
  P('Make every Shell and Chevron purchase use the Gas category.', 'transactions', 'proposals'),
  P('Set up my streaming subscriptions as monthly bills.', 'transactions', 'proposals'),
  P('Clean up my payee names so they\'re consistent.', 'proposals'),
];

/** Up to `count` random prompts that work with the given sharing settings. */
export function pickPrompts(share: AiShare, allowProposals: boolean, count = 4): string[] {
  const ok = ASK_PROMPTS.filter(p => p.needs.every(n => n === 'proposals' ? allowProposals : share[n]));
  const pool = [...ok];
  for (let i = pool.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [pool[i], pool[j]] = [pool[j], pool[i]];
  }
  return pool.slice(0, count).map(p => p.text);
}
