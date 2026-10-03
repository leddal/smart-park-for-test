import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SourceBadge from '@/components/SourceBadge.vue'

describe('SourceBadge', () => {
  it('renders a clear simulation provenance label', () => {
    const wrapper = mount(SourceBadge, { props: { source: 'Simulation' } })
    expect(wrapper.text()).toBe('模拟数据')
    expect(wrapper.classes()).toContain('simulation')
  })

  it('does not imply a source when it is absent', () => {
    expect(mount(SourceBadge).text()).toBe('来源未知')
  })
})
