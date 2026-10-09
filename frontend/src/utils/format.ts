import type { IntegrationStatus, Source } from '@/types/domain'

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

// 协同消息状态专属映射：Published 只代表 RabbitMQ 已确认、等待本地消费，不是业务成功。
export const integrationStatusLabel: Record<IntegrationStatus, string> = { Pending: '待发布/退避', Published: '已确认待消费', Succeeded: '本地回执成功', Failed: '重试耗尽', SimulatedSucceeded: '旧版本地回执成功' }

// 协同状态标签色：Published 用警告色，避免把“队列已确认”误读为业务成功。
export function integrationStatusType(status?: IntegrationStatus): 'success' | 'warning' | 'danger' | 'info' {
  if (status === 'Succeeded' || status === 'SimulatedSucceeded') return 'success'
  if (status === 'Failed') return 'danger'
  if (status === 'Pending' || status === 'Published') return 'warning'
  return 'info'
}

export function statusType(status?: string): 'success' | 'warning' | 'danger' | 'info' {
  if (/Completed|Closed|Published|Succeeded|正常|已完成|已发布/.test(status ?? '')) return 'success'
  if (/Failed|Timeout|Cancelled|Rejected|危险|失败|超时|已取消/.test(status ?? '')) return 'danger'
  if (/Pending|Processing|InProgress|Assigned|告警|待/.test(status ?? '')) return 'warning'
  return 'info'
}
