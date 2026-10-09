import { describe, expect, it } from 'vitest'
import { formatDate, integrationStatusLabel, integrationStatusType, sourceLabel, statusType } from '@/utils/format'

describe('display helpers', () => {
  it('formats ISO UTC dates in the Shanghai timezone', () => {
    expect(formatDate('2026-01-01T00:00:00.000Z')).toContain('2026')
    expect(formatDate(undefined)).toBe('—')
  })

  it('labels traceable seed, simulation, and manual sources', () => {
    expect(sourceLabel.Seed).toBe('种子数据')
    expect(sourceLabel.Simulation).toBe('模拟数据')
    expect(sourceLabel.Manual).toBe('人工录入')
  })

  it('maps business status to a visible tag severity', () => {
    expect(statusType('Completed')).toBe('success')
    expect(statusType('PendingReview')).toBe('warning')
    expect(statusType('Failed')).toBe('danger')
  })

  it('labels local integration pipeline statuses without implying external success', () => {
    expect(integrationStatusLabel.Pending).toBe('待发布/退避')
    expect(integrationStatusLabel.Published).toBe('已确认待消费')
    expect(integrationStatusLabel.Succeeded).toBe('本地回执成功')
    expect(integrationStatusLabel.Failed).toBe('重试耗尽')
    expect(integrationStatusLabel.SimulatedSucceeded).toBe('旧版本地回执成功')
  })

  it('keeps the published queue confirmation out of the success severity', () => {
    expect(integrationStatusType('Published')).toBe('warning')
    expect(integrationStatusType('Pending')).toBe('warning')
    expect(integrationStatusType('Succeeded')).toBe('success')
    expect(integrationStatusType('SimulatedSucceeded')).toBe('success')
    expect(integrationStatusType('Failed')).toBe('danger')
  })
})
