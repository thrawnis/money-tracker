import api from './client';

export interface DemoInfo {
  isDemoMode: boolean;
  email?: string;
}

export async function getDemoInfo(): Promise<DemoInfo> {
  const res = await api.get<DemoInfo>('/demo/info');
  return res.data;
}

export async function resetDemo(): Promise<void> {
  await api.post('/demo/reset');
}
