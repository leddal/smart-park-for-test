import { expect, test } from '@playwright/test'
import { capture, login, unique } from './support'

test('visitor can browse public services, reserve, view code, and cancel before the session', async ({ page }, testInfo) => {
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

  const reserve = page.getByRole('button', { name: '预约' }).first()
  await expect(reserve).toBeEnabled()
  await reserve.click()
  await expect(page.getByText(/预约成功/)).toBeVisible()
  await page.getByRole('link', { name: /查看我的预约/ }).click()
  await expect(page.getByRole('heading', { name: '我的活动预约' })).toBeVisible()
  await expect(page.getByText('预约码', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '取消预约' })).toBeEnabled()
  await page.getByRole('button', { name: '取消预约' }).click()
  await page.getByRole('dialog', { name: '确认取消' }).getByRole('button', { name: 'OK' }).click()
  await expect(page.getByText('预约已取消。', { exact: true })).toBeVisible()
  await capture(page, testInfo, 'visitor-reservation-cancelled')
})

test('visitor is blocked from an internal URL after authenticating', async ({ page }, testInfo) => {
  await login(page, 'visitor')
  await page.goto('/admin/control')

  await expect(page).toHaveURL(/\/forbidden$/)
  await expect(page.getByRole('heading', { name: '无访问权限', exact: true })).toBeVisible()
  await capture(page, testInfo, 'visitor-internal-url-forbidden')
})
