import api from './client';

export interface AiShare { balances: boolean; transactions: boolean; memos: boolean; bills: boolean; ious: boolean }

export interface AiSettings {
  enabled: boolean;
  baseUrl: string | null;
  model: string | null;
  hasApiKey: boolean;
  share: AiShare;
  allowUserServers: boolean;
  /** The server-wide default, if the admin set one. */
  server: { host: string; model: string } | null;
  /** What questions would use right now (null: not set up). */
  active: { host: string; model: string; personal: boolean } | null;
  ready: boolean;
}

export interface AiConversationSummary { id: number; title: string; updatedAt: string }
export interface AiMessage { id?: number; role: 'user' | 'assistant'; content: string; createdAt?: string }

export const getAiSettings = (): Promise<AiSettings> => api.get('/ai/settings').then(r => r.data);

/** apiKey: undefined keeps the saved key, '' removes it. */
export const saveAiSettings = (s: { enabled: boolean; baseUrl: string | null; model: string | null; apiKey?: string; share: AiShare }): Promise<AiSettings> =>
  api.put('/ai/settings', { ...s, apiKey: s.apiKey ?? null }).then(r => r.data);

export const testAiServer = (s: { baseUrl: string | null; model: string | null; apiKey?: string }): Promise<{ ok: boolean; message: string }> =>
  api.post('/ai/test', { ...s, apiKey: s.apiKey ?? null }).then(r => r.data);

export const getAiConversations = (): Promise<AiConversationSummary[]> => api.get('/ai/conversations').then(r => r.data);
export const getAiConversation = (id: number): Promise<{ id: number; title: string; messages: AiMessage[] }> =>
  api.get(`/ai/conversations/${id}`).then(r => r.data);
export const deleteAiConversation = (id: number) => api.delete(`/ai/conversations/${id}`);

export const askAi = (conversationId: number | null, message: string): Promise<{ conversationId: number; title: string; answer: string }> =>
  api.post('/ai/ask', { conversationId, message }, { timeout: 0 }).then(r => r.data);
