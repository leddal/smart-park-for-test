import { expect, test } from '@playwright/test'
import { capture, login, logout, unique } from './support'

test('visitor can browse public services, reserve, view code, and cancel before the session', async ({ page }, testInfo) => {
  // Seed sessions can expire in a reused verification database; create only this test's future slot.
  const activityTitle = unique('E2E游客预约')
  await login(page, 'admin')
  const csrfResponse = await page.request.get('/api/auth/csrf')
  expect(csrfResponse.ok()).toBeTruthy()
  const { token } = await csrfResponse.json() as { token: string }
  const headers = { 'X-CSRF-TOKEN': token }
  const activityResponse = await page.request.post('/api/services/activities', {
    headers,
    data: { title: activityTitle, description: '隔离预约回归活动', location: '测试活动场地', status: 'Published' },
  })
  expect(activityResponse.status()).toBe(201)
  const { id: activityId } = await activityResponse.json() as { id: string }
  const startsAt = Date.now() + 24 * 60 * 60 * 1000
  const sessionResponse = await page.request.post(`/api/services/activities/${activityId}/sessions`, {
    headers,
    data: { startsAt: new Date(startsAt).toISOString(), endsAt: new Date(startsAt + 60 * 60 * 1000).toISOString(), capacity: 2, status: 'Published' },
  })
  expect(sessionResponse.status()).toBe(201)
  const { id: sessionId } = await sessionResponse.json() as { id: string }
  await logout(page)

  await page.goto('/visitor')
  await expect(page.getByRole('heading', { name: '活动预约' })).toBeVisible()
  await expect(page.getByRole('heading', { name: '公园公告' })).toBeVisible()

  const userName = unique('e2e-visitor')
  await page.goto('/login?next=/visitor')
  await page.getByRole('button', { name: '立即注册' }).click()
  await page.getByLabel('显示名称').fill('端到端游客')
  await page.getByLabel('用户名').fill(userName)
  await page.getByLabel('密码').fill('Visitor!2026')
  await page.getByRole('button', { name: '注册并登录' }).click()
  await expect(page).toHaveURL(/\/visitor$/)

  const activity = page.locator('article.activity').filter({ has: page.getByRole('heading', { name: activityTitle, exact: true }) })
  const reserve = activity.getByRole('button', { name: '预约', exact: true })
  await expect(reserve).toBeEnabled()
  const reservationResponse = page.waitForResponse((response) => response.url().endsWith(`/api/public/sessions/${sessionId}/reserve`) && response.request().method() === 'POST')
  await reserve.click()
  const response = await reservationResponse
  expect(response.status()).toBe(201)
  const reservation = await response.json() as { id: string; sessionId: string; reserveCode: string }
  expect(reservation.sessionId).toBe(sessionId)
  expect(reservation.reserveCode).toBeTruthy()
  await expect(page.getByText(/预约成功/)).toBeVisible()
  await page.getByRole('link', { name: /查看我的预约/ }).click()
  await expect(page.getByRole('heading', { name: '我的活动预约' })).toBeVisible()
  const booking = page.locator('article.reservation').filter({ has: page.getByRole('heading', { name: activityTitle, exact: true }) })
  await expect(booking.getByText('预约码', { exact: true })).toBeVisible()
  await expect(booking.getByText(reservation.reserveCode, { exact: true })).toBeVisible()
  await expect(booking.getByRole('button', { name: '取消预约' })).toBeEnabled()
  const cancellationResponse = page.waitForResponse((result) => result.url().endsWith(`/api/public/reservations/${reservation.id}/cancel`) && result.request().method() === 'POST')
  await booking.getByRole('button', { name: '取消预约' }).click()
  await page.getByRole('dialog', { name: '确认取消' }).getByRole('button', { name: 'OK' }).click()
  expect((await cancellationResponse).status()).toBe(204)
  await expect(page.getByText('预约已取消。', { exact: true })).toBeVisible()
  await expect(booking.getByText('当前不可取消', { exact: true })).toBeVisible()
  await expect(booking.getByRole('button', { name: '取消预约' })).toHaveCount(0)
  await capture(page, testInfo, 'visitor-reservation-cancelled')
})

test('visitor is blocked from an internal URL after authenticating', async ({ page }, testInfo) => {
  await login(page, 'visitor')
  await page.goto('/admin/control')

  await expect(page).toHaveURL(/\/forbidden$/)
  await expect(page.getByRole('heading', { name: '无访问权限', exact: true })).toBeVisible()
  await capture(page, testInfo, 'visitor-internal-url-forbidden')
})
