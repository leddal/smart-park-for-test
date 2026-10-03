import { http } from './http'

export interface PageResult<T> { items: T[]; total: number; page: number; pageSize: number }

export function toPage<T>(payload: PageResult<T> | T[], page = 1, pageSize = 20): PageResult<T> {
  if (Array.isArray(payload)) return { items: payload, total: payload.length, page, pageSize }
  return { items: payload.items ?? [], total: payload.total ?? 0, page: payload.page ?? page, pageSize: payload.pageSize ?? pageSize }
}

export async function getPage<T>(url: string, params?: Record<string, unknown>): Promise<PageResult<T>> {
  const page = Number(params?.page ?? 1)
  const pageSize = Number(params?.pageSize ?? 20)
  const { data } = await http.get<PageResult<T> | T[]>(url, { params })
  return toPage(data, page, pageSize)
}

export async function getList<T>(url: string, params?: Record<string, unknown>): Promise<T[]> {
  const { data } = await http.get<PageResult<T> | T[]>(url, { params })
  return Array.isArray(data) ? data : data.items ?? []
}

export async function post<T>(url: string, body?: unknown): Promise<T> {
  const { data } = await http.post<T>(url, body)
  return data
}

export async function put<T>(url: string, body?: unknown): Promise<T> {
  const { data } = await http.put<T>(url, body)
  return data
}
