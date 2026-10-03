import { expect, type Locator, type Page, type TestInfo } from '@playwright/test'

export const demoPassword = 'ParkDemo!2026'

export async function login(page: Page, userName: string, password = demoPassword): Promise<void> {
  await page.goto('/login')
  await expect(page.getByRole('heading', { name: /登录运行平台/ })).toBeVisible()
  await page.getByLabel('用户名').fill(userName)
  await page.getByLabel('密码').fill(password)
  await page.getByRole('button', { name: '安全登录' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

export async function logout(page: Page): Promise<void> {
  const leave = page.getByRole('button', { name: '退出' })
  if (await leave.isVisible()) await leave.click()
  await expect(page).toHaveURL(/\/login/)
}

export async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ path: testInfo.outputPath(`${name}.png`), fullPage: true })
}

export async function selectElementPlusOption(page: Page, combobox: Locator, optionName: string | RegExp): Promise<void> {
  await combobox.locator('xpath=ancestor::div[contains(@class, "el-select__wrapper")]').click()
  await expect(combobox).toHaveAttribute('aria-expanded', 'true')
  const option = page.getByRole('option', { name: optionName }).last()
  await expect(option).toBeVisible()
  await option.click()
}

export function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
}
