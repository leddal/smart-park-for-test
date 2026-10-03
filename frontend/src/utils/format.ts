import type { Source } from '@/types/domain'

const shanghai = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', dateStyle: 'medium', timeStyle: 'short', hour12: false })

export function formatDate(value?: string | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : shanghai.format(date)
}

export function formatNumber(value?: number | null, digits = 0): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toLocaleString('zh-CN', { maximumFractionDigits: digits }) : '—'
}

export const sourceLabel: Record<Source, string> = { Seed: '种子数据', Simulation: '模拟数据', Manual: '人工录入' }

export function statusType(status?: string): 'success' | 'warning' | 'danger' | 'info' {
  if (/Completed|Closed|Published|Succeeded|正常|已完成|已发布/.test(status ?? '')) return 'success'
  if (/Failed|Timeout|Cancelled|Rejected|危险|失败|超时|已取消/.test(status ?? '')) return 'danger'
  if (/Pending|Processing|InProgress|Assigned|告警|待/.test(status ?? '')) return 'warning'
  return 'info'
}
