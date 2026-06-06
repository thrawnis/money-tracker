import api from './client';

export interface ExtractedReceipt {
  date?: string | null;
  payee?: string | null;
  amount?: number | null;
  memo?: string | null;
  suggestedCategory?: string | null;
}

export const extractReceipt = async (file: File): Promise<ExtractedReceipt> => {
  const form = new FormData();
  form.append('image', file);
  const res = await api.post<ExtractedReceipt>('/receipts/extract', form, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res.data;
};
