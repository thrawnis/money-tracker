import api, { baseURL, getAccessToken, refreshAccessToken } from './client';

export interface AiShare { balances: boolean; transactions: boolean; memos: boolean; bills: boolean; ious: boolean }

export interface AiSettings {
  enabled: boolean;
  share: AiShare;
  /** The AI server set in .env (null: not set up — see serverProblem). */
  server: { provider: string; host: string; model: string } | null;
  serverProblem: string | null;
  ready: boolean;
}

export interface AiConversationSummary { id: number; title: string; updatedAt: string }
export interface AiMessage { id?: number; role: 'user' | 'assistant'; content: string; createdAt?: string }

export const getAiSettings = (): Promise<AiSettings> => api.get('/ai/settings').then(r => r.data);

export const saveAiSettings = (s: { enabled: boolean; share: AiShare }): Promise<AiSettings> =>
  api.put('/ai/settings', s).then(r => r.data);

export const testAiServer = (): Promise<{ ok: boolean; message: string }> =>
  api.post('/ai/test').then(r => r.data);

export const getAiConversations = (): Promise<AiConversationSummary[]> => api.get('/ai/conversations').then(r => r.data);
export const getAiConversation = (id: number): Promise<{ id: number; title: string; messages: AiMessage[] }> =>
  api.get(`/ai/conversations/${id}`).then(r => r.data);
export const deleteAiConversation = (id: number) => api.delete(`/ai/conversations/${id}`);

export const askAi = (conversationId: number | null, message: string): Promise<{ conversationId: number; title: string; answer: string }> =>
  api.post('/ai/ask', { conversationId, message }, { timeout: 0 }).then(r => r.data);

export type AiStreamEvent =
  | { type: 'status'; text: string }
  | { type: 'delta'; text: string }
  | { type: 'done'; conversationId: number; title: string; answer: string }
  | { type: 'error'; message: string };

/**
 * Asks with live progress (POST /ai/ask/stream, Server-Sent Events): onEvent
 * hears each status line and piece of the answer. Resolves with the final
 * "done" event; rejects with an ApiError-shaped error ({ response: { data: { message } } }).
 * Uses fetch (axios can't stream in the browser), with the same token and one
 * silent refresh on 401 as the axios client.
 */
export async function askAiStream(conversationId: number | null, message: string,
  onEvent: (e: AiStreamEvent) => void): Promise<Extract<AiStreamEvent, { type: 'done' }>> {
  const fail = (msg: string) => ({ response: { data: { message: msg } } });
  const send = (token: string | null) => fetch(`${baseURL}/ai/ask/stream`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify({ conversationId, message }),
  });

  let res: Response;
  try {
    res = await send(getAccessToken());
    if (res.status === 401) res = await send(await refreshAccessToken());
  } catch {
    throw fail('Couldn\'t reach Money Tracker. Check your connection and try again.');
  }
  if (!res.ok || !res.body) {
    const body = await res.json().catch(() => null) as { message?: string } | null;
    throw fail(body?.message ?? `The request failed (${res.status}).`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let cut: number;
    while ((cut = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, cut);
      buffer = buffer.slice(cut + 2);
      const data = block.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5).trim()).join('');
      if (!data) continue;
      let event: AiStreamEvent;
      try { event = JSON.parse(data) as AiStreamEvent; } catch { continue; }
      if (event.type === 'error') throw fail(event.message);
      if (event.type === 'done') return event;
      onEvent(event);
    }
  }
  throw fail('The connection closed before the answer finished. If it was saved, it will be in your conversations.');
}
