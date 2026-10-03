import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { http, clearCsrf } from '@/api/http'
import type { Role, User } from '@/types/domain'

export const useSessionStore = defineStore('session', () => {
  const user = ref<User | null>(null)
  const loaded = ref(false)
  const loading = ref(false)
  const isAuthenticated = computed(() => !!user.value)

  async function load(force = false): Promise<User | null> {
    if (loaded.value && !force) return user.value
    if (loading.value) return user.value
    loading.value = true
    try { user.value = (await http.get<User>('/auth/me')).data }
    catch { user.value = null }
    finally { loaded.value = true; loading.value = false }
    return user.value
  }

  async function login(userName: string, password: string): Promise<User> {
    await http.post('/auth/login', { userName, password })
    clearCsrf()
    const account = await load(true)
    if (!account) throw new Error('登录状态未建立，请重试。')
    return account
  }

  async function register(userName: string, password: string, displayName: string): Promise<User> {
    await http.post('/auth/register', { userName, password, displayName })
    clearCsrf()
    const account = await load(true)
    if (!account) throw new Error('注册状态未建立，请重试。')
    return account
  }

  async function logout(): Promise<void> {
    try { await http.post('/auth/logout') } finally { user.value = null; loaded.value = true; clearCsrf() }
  }

  function hasAnyRole(roles: Role[]): boolean { return !!user.value?.roles.some((role) => roles.includes(role)) }
  return { user, loaded, loading, isAuthenticated, load, login, register, logout, hasAnyRole }
})
