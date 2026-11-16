import api from './client';

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
