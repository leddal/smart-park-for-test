import { expect, test } from '@playwright/test'
import { capture, login, selectElementPlusOption } from './support'

test('dispatcher submits a local simulated control command and sees its receipt log', async ({ page }, testInfo) => {
  await login(page, 'dispatcher')
  await page.goto('/admin/control')
  await expect(page.getByRole('heading', { name: '智能控制' })).toBeVisible()
  await page.getByRole('button', { name: '模拟亮度' }).first().click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '提交模拟指令' }).click()
  await expect(page.getByText('模拟指令已提交，正在等待本地模拟回执。')).toBeVisible()
  await expect(page.getByRole('table').getByText(/Pending|SimulatedSucceeded/).first()).toBeVisible()
  await capture(page, testInfo, 'local-control-command-receipt')
})

test('administrator and dispatcher can open the desktop overview and dedicated screen', async ({ page }, testInfo) => {
  await login(page, 'admin')
  await page.goto('/admin/overview')
  await expect(page.getByRole('heading', { name: '公园运行总览' })).toBeVisible()
  await expect(page.getByRole('combobox', { name: '选择趋势指标' })).toBeVisible()
  await capture(page, testInfo, 'admin-desktop-overview')

  await page.goto('/screen')
  await expect(page.getByRole('heading', { name: /智慧公园|运行指挥中心/ })).toBeVisible()
  await capture(page, testInfo, 'dedicated-operation-screen')
})

test('administrator injects a failed local control scenario and sees the actual failure receipt', async ({ page }, testInfo) => {
  await login(page, 'admin')
  await page.goto('/admin/control')
  await page.getByRole('button', { name: '模拟启动' }).first().click()
  const dialog = page.getByRole('dialog')
  await selectElementPlusOption(page, dialog.getByRole('combobox', { name: '模拟结果注入' }), '模拟失败')
  await dialog.getByRole('button', { name: '提交模拟指令' }).click()
  await expect(page.getByRole('table').getByText('Failed').first()).toBeVisible({ timeout: 8_000 })
  await expect(page.getByRole('table').getByText(/Local simulation failed/).first()).toBeVisible()
})
