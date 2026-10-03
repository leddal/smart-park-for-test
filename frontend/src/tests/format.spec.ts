import { describe, expect, it } from 'vitest'
import { formatDate, sourceLabel, statusType } from '@/utils/format'

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
})
