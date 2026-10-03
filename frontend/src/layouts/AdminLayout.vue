<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Bell, Calendar, Connection, DataAnalysis, FirstAidKit, Grid, Monitor, Operation, Setting, Suitcase, UserFilled } from '@element-plus/icons-vue'
import { http } from '@/api/http'
import { useSessionStore } from '@/stores/session'

const router = useRouter()
const session = useSessionStore()
const parkName = ref('智慧公园运行平台')
const isWorker = computed(() => session.hasAnyRole(['Worker']) && !session.hasAnyRole(['Administrator', 'Dispatcher']))
const isAdmin = computed(() => session.hasAnyRole(['Administrator']))
const menus = computed(() => isWorker.value ? [{ index: '/admin/operations/my-tasks', label: '我的任务', icon: Suitcase }] : [
  { index: '/admin/overview', label: '运行总览', icon: DataAnalysis },
  { index: '/admin/platform', label: '数据平台', icon: Grid },
  { index: '/admin/services', label: '智慧服务', icon: Calendar },
  { index: '/admin/control', label: '智能控制', icon: Operation },
  { index: '/admin/iot', label: '智能物联', icon: Connection },
  { index: '/admin/operations', label: '智慧运维', icon: Suitcase },
  { index: '/admin/assets', label: '资产信息', icon: Monitor },
  { index: '/admin/emergency', label: '应急协同', icon: FirstAidKit },
  { index: '/admin/integrations', label: '外部协同', icon: Setting },
  ...(isAdmin.value ? [{ index: '/admin/accounts', label: '内部账号', icon: UserFilled }] : []),
])
async function leave(): Promise<void> { await session.logout(); await router.push('/login') }
onMounted(async () => { const { data } = await http.get<{ name: string }>('/platform/park').catch(() => ({ data: undefined })); if (data?.name) parkName.value = data.name })
</script>

<template>
  <el-container class="admin-shell">
    <el-aside width="244px" class="admin-aside">
      <router-link class="brand" :to="isWorker ? '/admin/operations/my-tasks' : '/admin/overview'">
        <span class="brand-mark">P</span><span><strong>{{ parkName }}</strong><small>智慧运营系统</small></span>
      </router-link>
      <div class="role-chip"><UserFilled /><span>{{ isWorker ? '作业人员工作台' : '内部管理后台' }}</span></div>
      <el-menu router :default-active="$route.path" class="admin-menu">
        <el-menu-item v-for="menu in menus" :key="menu.index" :index="menu.index"><el-icon><component :is="menu.icon" /></el-icon><span>{{ menu.label }}</span></el-menu-item>
      </el-menu>
      <div class="aside-footer"><span class="sim-dot" />演示数据与模拟状态可追溯</div>
    </el-aside>
    <el-container>
      <el-header class="admin-header">
        <div class="header-status"><Bell /> 本地演示环境 · 所有设备与平台回执均非真实接入</div>
        <div class="user-area"><span>{{ session.user?.displayName || session.user?.userName }}</span><el-tag size="small">{{ session.user?.roles.join(' / ') }}</el-tag><el-button text type="primary" @click="leave">退出</el-button></div>
      </el-header>
      <el-main class="admin-main"><router-view /></el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.admin-shell { min-height:100vh; background:#f3f7f5; }.admin-aside { background:#123a35; color:#dcebe5; display:flex; flex-direction:column; padding:20px 12px 18px; position:sticky; top:0; height:100vh; }.brand { color:white; display:flex; align-items:center; gap:11px; padding:4px 9px 22px; }.brand-mark { display:grid; place-items:center; width:34px; height:34px; border-radius:10px; background:#50c2a2; color:#123a35; font-weight:800; font-size:19px; }.brand strong { display:block; font-size:15px; }.brand small { display:block; margin-top:3px; color:#93b5aa; font-size:11px; }.role-chip { margin:0 7px 12px; padding:8px; display:flex; gap:7px; align-items:center; border:1px solid rgba(156,223,200,.18); border-radius:8px; color:#a8d8c9; font-size:12px; }.admin-menu { border:0; background:transparent; --el-menu-bg-color:transparent; --el-menu-text-color:#bfd5ce; --el-menu-hover-bg-color:#1e5149; --el-menu-active-color:#fff; }.admin-menu :deep(.el-menu-item) { border-radius:8px; margin:3px 0; height:46px; }.admin-menu :deep(.el-menu-item.is-active) { background:#257462; }.aside-footer { margin:auto 8px 0; padding-top:16px; border-top:1px solid rgba(255,255,255,.09); color:#91ada5; font-size:11px; line-height:1.5; }.sim-dot { display:inline-block; width:7px; height:7px; border-radius:50%; background:#e5b150; margin-right:6px; }.admin-header { height:58px; padding:0 28px; display:flex; justify-content:space-between; align-items:center; background:#fff; border-bottom:1px solid #e4ece9; }.header-status,.user-area { display:flex; align-items:center; gap:9px; color:#657873; font-size:12px; }.header-status :deep(svg) { color:#bc8123; }.admin-main { padding:0; overflow:visible; }
</style>
