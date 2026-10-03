import axios, { AxiosError } from 'axios'

export interface ProblemDetails { title?: string; detail?: string; status?: number }

let csrfToken: string | undefined
let csrfRequest: Promise<string | undefined> | undefined

export const http = axios.create({ baseURL: '/api', withCredentials: true, headers: { Accept: 'application/json' } })

async function getCsrfToken(): Promise<string | undefined> {
  if (csrfToken) return csrfToken
  csrfRequest ??= http.get<{ token: string }>('/auth/csrf').then(({ data }) => {
    csrfToken = data.token
    return csrfToken
  }).catch(() => undefined).finally(() => { csrfRequest = undefined })
  return csrfRequest
}

http.interceptors.request.use(async (config) => {
  const method = config.method?.toLowerCase()
  if (method && !['get', 'head', 'options'].includes(method) && !config.url?.includes('/auth/csrf')) {
    const token = await getCsrfToken()
    if (token) config.headers.set('X-CSRF-TOKEN', token)
  }
  return config
})

export function problemMessage(error: unknown, fallback = '请求未能完成，请稍后重试。'): string {
  if (axios.isAxiosError(error)) {
    const data = error.response?.data as ProblemDetails | undefined
    return data?.detail || data?.title || error.message || fallback
  }
  return error instanceof Error ? error.message : fallback
}

export function isForbidden(error: unknown): boolean {
  return error instanceof AxiosError && error.response?.status === 403
}

export function clearCsrf(): void { csrfToken = undefined }
