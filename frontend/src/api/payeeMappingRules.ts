import api from './client';

export interface PayeeMappingRule {
  id: number;
  pattern: string;
  isRegex: boolean;
  targetPayeeId: number;
  targetPayeeName: string;
  createdAt: string;
}

export const getPayeeMappingRules = (): Promise<PayeeMappingRule[]> =>
  api.get('/payee-mapping-rules').then(r => r.data);

export const createPayeeMappingRule = (data: { pattern: string; isRegex: boolean; targetPayeeId: number }): Promise<{ id: number }> =>
  api.post('/payee-mapping-rules', data).then(r => r.data);

export const deletePayeeMappingRule = (id: number): Promise<void> =>
  api.delete(`/payee-mapping-rules/${id}`).then(() => undefined);
