import { createRouter, createWebHistory, type RouteLocationNormalized } from 'vue-router'
import { useSessionStore } from '@/stores/session'
import type { Role } from '@/types/domain'

interface RouteMeta { requiresAuth?: boolean; roles?: Role[] }

function adminDefault(): string {
  const session = useSessionStore()
  return session.hasAnyRole(['Worker']) && !session.hasAnyRole(['Administrator', 'Dispatcher']) ? '/admin/operations/my-tasks' : '/admin/overview'
}

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('@/views/auth/LoginView.vue') },
    { path: '/screen', component: () => import('@/views/ScreenView.vue'), meta: { requiresAuth: true, roles: ['Administrator', 'Dispatcher'] } },
    {
      path: '/admin',
      component: () => import('@/layouts/AdminLayout.vue'),
      meta: { requiresAuth: true, roles: ['Administrator', 'Dispatcher', 'Worker'] },
      children: [
        { path: '', redirect: adminDefault },
        { path: 'overview', component: () => import('@/views/OverviewView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'platform', component: () => import('@/views/PlatformView.vue'), meta: { roles: ['Administrator'] } },
        { path: 'services', component: () => import('@/views/ServicesView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'control', component: () => import('@/views/ControlView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'iot', component: () => import('@/views/IoTView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'operations', component: () => import('@/views/OperationsView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'operations/work-orders/:id', component: () => import('@/views/WorkOrderDetailView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'operations/my-tasks', component: () => import('@/views/MyTasksView.vue'), meta: { roles: ['Administrator', 'Dispatcher', 'Worker'] } },
        { path: 'operations/my-tasks/:id', component: () => import('@/views/WorkOrderDetailView.vue'), meta: { roles: ['Administrator', 'Dispatcher', 'Worker'] } },
        { path: 'assets', component: () => import('@/views/AssetsView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'assets/:id', component: () => import('@/views/AssetDetailView.vue'), meta: { roles: ['Administrator', 'Dispatcher', 'Worker'] } },
        { path: 'accounts', component: () => import('@/views/AccountView.vue'), meta: { roles: ['Administrator'] } },
        { path: 'emergency', component: () => import('@/views/EmergencyView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'emergency/:id', component: () => import('@/views/EventDetailView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'integrations', component: () => import('@/views/IntegrationsView.vue'), meta: { roles: ['Administrator', 'Dispatcher'] } },
        { path: 'help/production/:topic', component: () => import('@/views/ProductionHelpView.vue') },
      ],
    },
    {
      path: '/visitor', component: () => import('@/layouts/VisitorLayout.vue'), children: [
        { path: '', component: () => import('@/views/visitor/VisitorHomeView.vue') },
        { path: 'assets/:publicCode', component: () => import('@/views/visitor/PublicAssetView.vue') },
        { path: 'bookings', component: () => import('@/views/visitor/BookingsView.vue'), meta: { requiresAuth: true, roles: ['Visitor'] } },
      ],
    },
    { path: '/forbidden', component: () => import('@/views/ForbiddenView.vue') },
    { path: '/:pathMatch(.*)*', component: () => import('@/views/NotFoundView.vue') },
  ],
})

function inheritedMeta(to: RouteLocationNormalized): RouteMeta {
  return to.matched.reduce<RouteMeta>((meta, record) => ({ ...meta, ...record.meta }), {})
}

router.beforeEach(async (to) => {
  const meta = inheritedMeta(to)
  const session = useSessionStore()
  if (meta.requiresAuth || meta.roles) await session.load()
  if ((meta.requiresAuth || meta.roles) && !session.isAuthenticated) return { path: '/login', query: { next: to.fullPath } }
  if (meta.roles && !session.hasAnyRole(meta.roles)) return '/forbidden'
  const onlyWorker = session.hasAnyRole(['Worker']) && !session.hasAnyRole(['Administrator', 'Dispatcher'])
  const workerAssetDetail = /^\/admin\/assets\/[^/]+$/.test(to.path)
  if (onlyWorker && to.path.startsWith('/admin') && !to.path.startsWith('/admin/operations/my-tasks') && !workerAssetDetail) return '/forbidden'
  return true
})

export default router
