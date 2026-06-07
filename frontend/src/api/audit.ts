import api from './client';

export interface AuditEntry {
  id: number;
  userId?: string;
  userEmail?: string;
  action: string;
  entityType?: string;
  entityId?: number;
  details?: string;      // JSON string — parse client-side for display
  ipAddress?: string;
  timestamp: string;
  isSystem: boolean;
}

export interface AuditPage {
  total: number;
  page: number;
  pageSize: number;
  items: AuditEntry[];
}

export interface GetAuditParams {
  page?: number;
  pageSize?: number;
  action?: string;
  entityType?: string;
  from?: string;
  to?: string;
  search?: string;
}

export const getAuditLog = (params?: GetAuditParams) =>
  api.get<AuditPage>('/audit', { params }).then(r => r.data);
