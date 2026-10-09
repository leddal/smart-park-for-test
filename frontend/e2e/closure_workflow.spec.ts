import { expect, test, type Page } from '@playwright/test'
import { capture, login, logout, selectElementPlusOption, unique } from './support'

async function selectSoilDevice(page: Page): Promise<void> {
  const devicePicker = page.getByRole('combobox', { name: '选择设备' })
  await selectElementPlusOption(page, devicePicker, /DEV-SOIL/)
  await expect(page.locator('.scenario')).toBeVisible()
}

async function triggerScenario(page: Page, action: RegExp): Promise<{ eventId?: string }> {
  const scenarioResponse = page.waitForResponse((response) => response.url().includes('/api/iot/scenarios') && response.request().method() === 'POST')
  await page.getByRole('button', { name: action }).click()
  const response = await scenarioResponse
  expect(response.ok()).toBeTruthy()
  return await response.json() as { eventId?: string }
}

async function chooseChecklistResultAndSubmit(page: Page, text: string, withPhoto = false): Promise<void> {
  await expect(page.getByRole('button', { name: 'Submit', exact: true })).toBeVisible()
  const checklist = page.locator('.checklist')
  await expect(checklist).toBeVisible()
  const selects = checklist.locator('[role="combobox"]')
  await expect(selects.first()).toBeVisible()
  const count = await selects.count()
  expect(count, 'work order must expose its required checklist').toBeGreaterThan(0)
  for (let index = 0; index < count; index += 1) {
    await selectElementPlusOption(page, selects.nth(index), '已检查正常')
  }
  await page.getByRole('textbox', { name: '处置说明 / 原因' }).fill(text)
  if (withPhoto) await page.locator('input[type=file]').setInputFiles({
    name: 'closure-proof.png',
    mimeType: 'image/png',
    buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j0ioAAAAASUVORK5CYII=', 'base64'),
  })
  await page.getByRole('button', { name: 'Submit' }).click()
}

test('admin abnormal to dispatcher task to worker review and event closure produces local simulation logs', async ({ page }, testInfo) => {
  const workTitle = unique('闭环工单')

  await login(page, 'admin')
  await page.goto('/admin/iot')
  await expect(page.getByRole('heading', { name: '智能物联' })).toBeVisible()
  await selectSoilDevice(page)
  await triggerScenario(page, /恢复/)
  const abnormal = await triggerScenario(page, /异常/)
  expect(abnormal.eventId, 'abnormal scenario must create an event').toBeTruthy()
  const eventId = abnormal.eventId!
  await logout(page)

  await login(page, 'dispatcher')
  await page.goto(`/admin/emergency/${eventId}`)
  await expect(page.getByRole('button', { name: '分派关联工单' })).toBeVisible()
  await page.getByRole('button', { name: '分派关联工单' }).click()
  const dispatch = page.getByRole('dialog')
  await dispatch.getByLabel('工单标题').fill(workTitle)
  await selectElementPlusOption(page, dispatch.getByLabel('处理人'), /养护作业员/)
  await dispatch.getByLabel('截止时间').fill('2030-12-31 12:00:00')
  const workOrderResponse = page.waitForResponse((response) => response.url().includes('/api/operations/work-orders') && response.request().method() === 'POST')
  await dispatch.getByRole('button', { name: /创建.*工单/ }).click()
  const workOrderPayload = await (await workOrderResponse).json() as { id?: string }
  expect(workOrderPayload.id, 'dispatcher must create an assigned task').toBeTruthy()
  const workOrderId = workOrderPayload.id!
  await logout(page)

  await login(page, 'worker')
  await page.goto(`/admin/operations/my-tasks/${workOrderId}`)
  await expect(page.getByText(workTitle)).toBeVisible()
  await page.getByRole('button', { name: 'Accept' }).click()
  await page.getByRole('button', { name: 'Start' }).click()
  await chooseChecklistResultAndSubmit(page, '已完成现场检查并提交文字反馈。', true)
  await expect(page.getByText('PendingReview')).toBeVisible()
  await expect(page.locator('.attachments img')).toBeVisible()
  await logout(page)

  await login(page, 'dispatcher')
  await page.goto(`/admin/operations/work-orders/${workOrderId}`)
  await page.getByRole('textbox', { name: '处置说明 / 原因' }).fill('请补充复查结论后再次提交。')
  await page.getByRole('button', { name: 'Reject' }).click()
  await expect(page.getByText('InProgress')).toBeVisible()
  await logout(page)

  await login(page, 'worker')
  await page.goto(`/admin/operations/my-tasks/${workOrderId}`)
  await chooseChecklistResultAndSubmit(page, '已补充复查结论并重新提交。')
  await logout(page)

  await login(page, 'dispatcher')
  await page.goto(`/admin/operations/work-orders/${workOrderId}`)
  await page.getByRole('textbox', { name: '处置说明 / 原因' }).fill('调度验收通过。')
  await page.getByRole('button', { name: 'Approve' }).click()
  await expect(page.getByText('Completed')).toBeVisible()
  await logout(page)

  await login(page, 'admin')
  await page.goto('/admin/iot')
  await selectSoilDevice(page)
  await triggerScenario(page, /恢复/)
  await logout(page)

  await login(page, 'dispatcher')
  await page.goto(`/admin/emergency/${eventId}`)
  await expect(page.getByText('PendingClosure')).toBeVisible()
  const reason = page.getByPlaceholder(/填写处置说明、办结依据或误报原因/)
  await reason.fill('告警已恢复，关联工单已验收，核实后人工办结。')
  await page.getByRole('button', { name: '人工办结' }).click()
  await expect(page.getByText('Closed')).toBeVisible()
  await logout(page)

  await login(page, 'admin')
  await page.goto('/admin/integrations')
  await expect(page.getByRole('heading', { name: '外部平台协同' })).toBeVisible()
  await expect(page.getByText('模拟同步消息')).toBeVisible()
  await expect(page.getByText('EventClosed', { exact: true }).first()).toBeVisible()
  await capture(page, testInfo, 'closed-event-local-simulation-log')
})

test('RabbitMQ publishes and consumes a local sync, then replays a failed generation without erasing receipts', async ({ page }, testInfo) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  const platformsResponse = await page.request.get('/api/integrations/platforms')
  expect(platformsResponse.ok()).toBeTruthy()
  const platforms = await platformsResponse.json() as { id: string; enabled: boolean; forceFailure: boolean }[]
  const platform = platforms[0]!
  expect(platform).toBeTruthy()
  const csrfResponse = await page.request.get('/api/auth/csrf')
  const { token } = await csrfResponse.json() as { token: string }
  const headers = { 'X-CSRF-TOKEN': token }
  const configure = async (enabled: boolean, forceFailure: boolean): Promise<void> => {
    const result = await page.request.put(`/api/integrations/platforms/${platform.id}`, { headers, data: { enabled, forceFailure } })
    expect(result.ok()).toBeTruthy()
  }
  type SyncMessage = { id: string; status: string; generation: number; attempts: number; publishAttempts: number; publishedAt: string | null; consumedAt: string | null; attemptLogs: { generation: number; stage: string; success: boolean }[] }
  const readMessage = async (id: string): Promise<SyncMessage | undefined> => {
    const result = await page.request.get('/api/integrations/messages?pageSize=100')
    expect(result.ok()).toBeTruthy()
    const payload = await result.json() as { items: SyncMessage[] }
    return payload.items.find((message) => message.id === id)
  }
  try {
    await configure(true, false)
    await page.goto('/admin/integrations')
    await expect(page.getByText('本地消息链路', { exact: true })).toBeVisible()
    const queued = page.waitForResponse((response) => response.url().endsWith('/api/integrations/sync') && response.request().method() === 'POST')
    await page.getByRole('button', { name: '资产', exact: true }).first().click()
    const response = await queued
    expect(response.status()).toBe(202)
    const { id } = await response.json() as { id: string }
    await expect.poll(async () => (await readMessage(id))?.status, { timeout: 30_000 }).toBe('Succeeded')
    const delivered = (await readMessage(id))!
    expect(delivered.publishedAt).toBeTruthy()
    expect(delivered.consumedAt).toBeTruthy()
    expect(delivered.attempts).toBe(1)
    expect(delivered.attemptLogs.filter((log) => log.stage === 'Publish' && log.success)).toHaveLength(1)
    expect(delivered.attemptLogs.filter((log) => log.stage === 'Consume' && log.success)).toHaveLength(1)
    await page.getByRole('button', { name: '刷新日志', exact: true }).click()
    const successRow = page.locator('.el-table__row').filter({ has: page.locator(`[data-message-id="${id}"]`) })
    await expect(successRow).toContainText('本地回执成功')
    await expect(successRow.getByRole('button', { name: '人工重试' })).toHaveCount(0)

    await configure(true, true)
    const failureResponse = await page.request.post('/api/integrations/sync', { headers, data: { platformId: platform.id, kind: 'VisitorCount' } })
    expect(failureResponse.status()).toBe(202)
    const failedId = (await failureResponse.json() as { id: string }).id
    await expect.poll(async () => (await readMessage(failedId))?.status, { timeout: 120_000, intervals: [1000] }).toBe('Failed')
    const failed = (await readMessage(failedId))!
    expect(failed.attempts).toBe(5)
    expect(failed.attemptLogs.filter((log) => log.stage === 'Consume' && !log.success)).toHaveLength(5)
    await configure(true, false)
    await page.getByRole('button', { name: '刷新日志', exact: true }).click()
    const failedRow = page.locator('.el-table__row').filter({ has: page.locator(`[data-message-id="${failedId}"]`) })
    await expect(failedRow).toContainText('重试耗尽')
    await failedRow.getByRole('button', { name: '人工重试', exact: true }).click()
    await expect.poll(async () => (await readMessage(failedId))?.status, { timeout: 30_000 }).toBe('Succeeded')
    const replayed = (await readMessage(failedId))!
    expect(replayed.generation).toBe(1)
    expect(replayed.attemptLogs.filter((log) => log.generation === 0)).toHaveLength(failed.attemptLogs.length)
    expect(replayed.attemptLogs.some((log) => log.generation === 1 && log.stage === 'Consume' && log.success)).toBeTruthy()
    await capture(page, testInfo, 'rabbitmq-local-pipeline-replay')
  } finally {
    await configure(platform.enabled, platform.forceFailure)
  }
})
