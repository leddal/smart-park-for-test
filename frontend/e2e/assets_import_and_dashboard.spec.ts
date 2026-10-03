import { expect, test } from '@playwright/test'
import { capture, login, selectElementPlusOption, unique } from './support'

test('administrator imports CSV assets, verifies QR public route, and dashboard reads backend state', async ({ page }, testInfo) => {
  const code = unique('e2easset').replaceAll('-', '').toUpperCase()
  const publicCode = `P-${code}`
  await login(page, 'admin')

  await page.goto('/admin/platform')
  await expect(page.getByRole('heading', { name: '智慧公园数据平台' })).toBeVisible()
  await page.getByRole('button', { name: '导入空间或元素数据' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await selectElementPlusOption(page, dialog.getByRole('combobox', { name: '文件类型' }), 'CSV')
  await dialog.locator('input[type=file]').setInputFiles({
    name: 'assets.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from(`code,name,category,longitude,latitude,publicDescription\n${code},E2E imported asset,Facility,121.438,31.19,Public E2E asset\n`),
  })
  await dialog.getByRole('button', { name: /预览/ }).click()
  await expect(dialog.getByText('预览结果：1 条有效数据')).toBeVisible()
  await dialog.getByRole('button', { name: /确认/ }).click()
  await expect(page.getByText('导入批次已确认。')).toBeVisible()

  await page.goto('/admin/assets')
  await expect(page.getByRole('heading', { name: '资产信息管理' })).toBeVisible()
  const row = page.getByRole('row', { name: new RegExp(code) })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: '二维码' }).click()
  await expect(page.getByRole('dialog').locator('img')).toBeVisible()
  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('link', { name: '下载二维码 PNG' }).click()
  const download = await downloadPromise
  expect(download.suggestedFilename()).toBe('E2E imported asset-二维码.png')
  expect(await download.path()).not.toBeNull()
  await capture(page, testInfo, 'asset-qr-code')

  await page.goto(`/visitor/assets/${publicCode}`)
  await expect(page.getByText('E2E imported asset')).toBeVisible()

  await page.goto('/admin/overview')
  await expect(page.getByRole('heading', { name: '运行总览' })).toBeVisible()
  await expect(page.getByText(/演示|来源/).first()).toBeVisible()
  await capture(page, testInfo, 'admin-overview-backend-dashboard')
})
