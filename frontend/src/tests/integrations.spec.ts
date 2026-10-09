import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import IntegrationsView from '@/views/IntegrationsView.vue'
import { getList, getPage, post } from '@/api/resources'
import { http } from '@/api/http'
import type { IntegrationMessage, IntegrationPipeline } from '@/types/domain'

vi.mock('@/api/resources', () => ({ getList: vi.fn(), getPage: vi.fn(), post: vi.fn(), put: vi.fn() }))
vi.mock('@/api/http', () => ({ http: { get: vi.fn() }, problemMessage: vi.fn(() => '请求未能完成，请稍后重试。'), isForbidden: vi.fn(() => false), clearCsrf: vi.fn() }))

const pipeline: IntegrationPipeline = { transport: 'RabbitMQ', queueName: 'smartpark.integration', deadLetterQueueName: 'smartpark.integration.dlq', pending: 7, published: 2, succeeded: 5, failed: 1 }
const published: IntegrationMessage = { id: 'a1b2c3d4-0000-0000-0000-000000000001', platformName: '交通平台', kind: 'Assets', status: 'Published', generation: 2, attempts: 0, publishAttempts: 1, lastError: '等待本地消费者确认', attemptLogs: [{ generation: 2, stage: 'Publish', attempt: 1, attemptedAt: '2026-01-01T00:00:00.000Z', success: true, message: '已发布到本地队列' }] }

function mountView() {
  return mount(IntegrationsView, { global: { plugins: [createPinia(), ElementPlus], stubs: { 'router-link': true } } })
}

// 视图用 window.setInterval 轮询；这里捕获回调手动触发，避免依赖真实计时器。
function stubInterval(): (() => void) | undefined {
  let tick: (() => void) | undefined
  const implementation = ((handler: TimerHandler) => { if (typeof handler === 'function') tick = handler as () => void; return 1 }) as unknown as typeof window.setInterval
  vi.spyOn(window, 'setInterval').mockImplementation(implementation)
  return () => tick?.()
}

beforeEach(() => {
  vi.mocked(getList).mockResolvedValue([])
  vi.mocked(getPage).mockResolvedValue({ items: [published], total: 1, page: 1, pageSize: 20 })
  vi.mocked(http.get).mockResolvedValue({ data: pipeline } as never)
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.clearAllMocks()
})

describe('IntegrationsView local pipeline', () => {
  it('shows database pipeline counts and local queue names after loading', async () => {
    const wrapper = mountView()
    await flushPromises()
    expect(getList).toHaveBeenCalledWith('/integrations/platforms')
    expect(http.get).toHaveBeenCalledWith('/integrations/pipeline')
    expect(getPage).toHaveBeenCalledWith('/integrations/messages')
    expect(wrapper.findAll('.pipeline-stats b').map((item) => item.text())).toEqual(['7', '2', '5', '1'])
    expect(wrapper.text()).toContain('RabbitMQ')
    expect(wrapper.text()).toContain('smartpark.integration.dlq')
    wrapper.unmount()
  })

  it('renders Chinese status, generation and attempt counts, and expands stage logs', async () => {
    const wrapper = mountView()
    await flushPromises()
    const row = wrapper.find('.el-table__row')
    expect(row.text()).toContain('已确认待消费')
    expect(row.text()).toContain(published.id)
    expect(row.text()).toContain('1 / 0')
    expect(row.find('.el-tag').classes()).toContain('el-tag--warning')
    await wrapper.find('.el-table__expand-icon').trigger('click')
    await flushPromises()
    const logs = wrapper.findAll('.attempt-logs .el-table__row')
    expect(logs).toHaveLength(1)
    expect(logs[0]!.text()).toContain('发布')
    expect(logs[0]!.text()).toContain('已发布到本地队列')
    wrapper.unmount()
  })

  it('polls only while the document is visible and never overlaps requests', async () => {
    const tick = stubInterval()
    const wrapper = mountView()
    await flushPromises()
    expect(getPage).toHaveBeenCalledTimes(1)

    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
    tick?.()
    await flushPromises()
    expect(getPage).toHaveBeenCalledTimes(1)
    hidden.mockRestore()

    tick?.()
    await flushPromises()
    expect(getPage).toHaveBeenCalledTimes(2)

    vi.mocked(getPage).mockReturnValue(new Promise<never>(() => undefined))
    tick?.()
    await flushPromises()
    tick?.()
    await flushPromises()
    expect(getPage).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })

  it('shows loading failures without inventing successful pipeline counts', async () => {
    vi.mocked(http.get).mockRejectedValueOnce(new Error('offline'))
    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.find('.el-alert').text()).toContain('请求未能完成')
    expect(wrapper.findAll('.pipeline-stats b').map((item) => item.text())).toEqual(['—', '—', '—', '—'])
    wrapper.unmount()
  })

  it('only exposes replay for failed messages and blocks duplicate replay clicks', async () => {
    vi.mocked(getPage).mockResolvedValue({ items: [published, { ...published, id: 'failed', status: 'Failed' }], total: 2, page: 1, pageSize: 20 })
    let complete!: () => void
    vi.mocked(post).mockReturnValueOnce(new Promise<void>((resolve) => { complete = resolve }))
    const wrapper = mountView()
    await flushPromises()
    const replay = wrapper.findAll('button').filter((button) => button.text() === '人工重试')
    expect(replay).toHaveLength(1)
    await replay[0]!.trigger('click')
    await replay[0]!.trigger('click')
    expect(post).toHaveBeenCalledTimes(1)
    expect(post).toHaveBeenCalledWith('/integrations/messages/failed/retry')
    complete()
    await flushPromises()
    wrapper.unmount()
  })

  it('ignores stale polls during mutation and refreshes the committed result afterwards', async () => {
    const tick = stubInterval()
    let finishPoll!: (value: { items: IntegrationMessage[]; total: number; page: number; pageSize: number }) => void
    let finishReplay!: () => void
    vi.mocked(getPage).mockResolvedValueOnce({ items: [{ ...published, status: 'Failed' }], total: 1, page: 1, pageSize: 20 })
    const wrapper = mountView()
    await flushPromises()
    vi.mocked(getPage).mockReturnValueOnce(new Promise((resolve) => { finishPoll = resolve }))
    tick?.()
    await flushPromises()
    vi.mocked(post).mockReturnValueOnce(new Promise<void>((resolve) => { finishReplay = resolve }))
    const replay = wrapper.findAll('button').find((button) => button.text() === '人工重试')!
    await replay.trigger('click')
    finishPoll({ items: [{ ...published, status: 'Failed', generation: 1 }], total: 1, page: 1, pageSize: 20 })
    await flushPromises()
    vi.mocked(getPage).mockResolvedValueOnce({ items: [{ ...published, status: 'Succeeded', generation: 3 }], total: 1, page: 1, pageSize: 20 })
    finishReplay()
    await flushPromises()
    expect(getPage).toHaveBeenCalledTimes(3)
    expect(wrapper.find('.el-table__row').text()).toContain('本地回执成功')
    expect(wrapper.findAll('button').filter((button) => button.text() === '人工重试')).toHaveLength(0)
    wrapper.unmount()
  })

  it('clears the polling timer on unmount', async () => {
    stubInterval()
    const clearSpy = vi.spyOn(window, 'clearInterval').mockImplementation(() => undefined)
    const wrapper = mountView()
    await flushPromises()
    wrapper.unmount()
    expect(clearSpy).toHaveBeenCalledWith(1)
  })
})
