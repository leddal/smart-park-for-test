<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useSessionStore } from '@/stores/session'

const router = useRouter()
const session = useSessionStore()
async function leave(): Promise<void> { await session.logout(); await router.push('/visitor') }
onMounted(() => { void session.load() })
</script>
<template>
  <div class="visitor-shell">
    <header class="visitor-header"><router-link to="/visitor" class="visitor-brand"><b>绿境</b><span>智慧公园</span></router-link><nav><router-link to="/visitor">公园服务</router-link><router-link to="/visitor#guide">智慧导览</router-link><router-link to="/visitor#eco">生态信息</router-link><router-link to="/visitor/bookings">我的预约</router-link></nav><div><template v-if="session.isAuthenticated"><span class="visitor-user">{{ session.user?.displayName }}</span><el-button text @click="leave">退出</el-button></template><el-button v-else type="primary" @click="router.push('/login?next=/visitor/bookings')">登录预约</el-button></div></header>
    <main><router-view /></main>
    <footer>智慧公园公共服务 · 当前页面仅展示公开信息，不含内部设备、事件与工单数据。</footer>
  </div>
</template>
<style scoped>
.visitor-shell { min-height:100vh; color:#25443c; background:#fcfdfb; }.visitor-header { position:sticky; z-index:10; top:0; height:70px; padding:0 max(38px,calc((100vw - 1340px)/2)); display:flex; align-items:center; justify-content:space-between; gap:35px; background:rgba(255,255,255,.94); backdrop-filter:blur(12px); border-bottom:1px solid #ebf0ed; }.visitor-brand { display:flex; align-items:baseline; gap:8px; }.visitor-brand b { color:#13725f; font-size:23px; letter-spacing:.12em; }.visitor-brand span { color:#5c736b; font-size:13px; }.visitor-header nav { display:flex; gap:28px; font-size:14px; }.visitor-header nav a.router-link-active { color:#167b67; font-weight:700; }.visitor-user { font-size:13px; color:#5d726c; margin-right:8px; } footer { padding:35px; text-align:center; border-top:1px solid #e5ede9; color:#7c8d87; font-size:12px; }
</style>
