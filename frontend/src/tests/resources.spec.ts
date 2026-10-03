import { describe, expect, it } from 'vitest'
import { toPage } from '@/api/resources'

describe('paged API response normalization', () => {
  it('normalizes direct arrays without inventing records', () => {
    expect(toPage([{ id: 'one' }, { id: 'two' }], 3, 50)).toEqual({
      items: [{ id: 'one' }, { id: 'two' }], total: 2, page: 3, pageSize: 50,
    })
  })

  it('preserves the backend paging envelope', () => {
    expect(toPage({ items: [{ id: 'one' }], total: 8, page: 2, pageSize: 20 })).toEqual({
      items: [{ id: 'one' }], total: 8, page: 2, pageSize: 20,
    })
  })
})
