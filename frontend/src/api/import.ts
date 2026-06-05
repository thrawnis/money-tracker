import api from './client';

export const importFile = (file: File) => {
  const fd = new FormData();
  fd.append('file', file);
  return api.post('/import', fd).then(r => r.data);
};

export const getTemplate = (format: 'csv' | 'xlsx') =>
  api.get(`/import/template/${format}`, { responseType: 'blob' }).then(r => r.data);
