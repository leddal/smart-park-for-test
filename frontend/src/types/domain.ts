export type Role = 'Administrator' | 'Dispatcher' | 'Worker' | 'Visitor'
export type Source = 'Seed' | 'Simulation' | 'Manual'

export interface User { id: string; userName: string; displayName: string; roles: Role[] }
export interface Metric { deviceId?: string; metricCode: string; value: number; unit: string; collectedAt: string; source: Source; stale?: boolean }
export interface Asset { id: string; code: string; name: string; category: 'Sensor' | 'Facility' | 'Plant'; zoneId?: string; longitude?: number; latitude?: number; status: string; publicDescription?: string; publicCode?: string; version?: number; device?: Record<string, unknown>; plant?: Record<string, unknown>; facility?: Record<string, unknown>; [key: string]: unknown }
export interface WorkOrder { id: string; number?: string; title: string; type: string; status: string; priority?: string; assigneeName?: string; assigneeId?: string; dueAt?: string; version: number; details?: string; checklist?: { name: string; result?: string }[]; [key: string]: unknown }
export interface ParkEvent { id: string; number?: string; title: string; category: string; severity: string; status: string; description?: string; longitude?: number; latitude?: number; version: number; [key: string]: unknown }
export interface Announcement { id: string; title: string; body: string; startsAt?: string; endsAt?: string; status?: string; published?: boolean; [key: string]: unknown }
export interface ActivitySession { id: string; startsAt: string; endsAt: string; capacity: number; reserved?: number; reservedCount?: number; status: string; [key: string]: unknown }
export interface Activity { id: string; title: string; description: string; location: string; status?: string; sessions?: ActivitySession[]; [key: string]: unknown }
export interface Device { id: string; code: string; name?: string; type: string; enabled: boolean; model?: string; manufacturer?: string; lastCollectedAt?: string; [key: string]: unknown }
export interface Alert { id: string; title?: string; deviceId?: string; metricCode?: string; value?: number; unit?: string; severity: string; status?: string; createdAt?: string; eventId?: string; [key: string]: unknown }
