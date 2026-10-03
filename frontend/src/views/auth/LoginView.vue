<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { problemMessage } from '@/api/http'
import { useSessionStore } from '@/stores/session'

const router = useRouter(); const route = useRoute(); const session = useSessionStore()
const mode = ref<'login' | 'register'>('login'); const submitting = ref(false)
const form = reactive({ userName: '', password: '', displayName: '' })
async function submit(): Promise<void> {
  if (!form.userName || !form.password || (mode.value === 'register' && !form.displayName)) return ElMessage.warning('请完整填写必填信息。')
  submitting.value = true
  try { if (mode.value === 'login') await session.login(form.userName, form.password); else await session.register(form.userName, form.password, form.displayName); const next = typeof route.query.next === 'string' ? route.query.next : session.hasAnyRole(['Visitor']) ? '/visitor' : session.hasAnyRole(['Worker']) ? '/admin/operations/my-tasks' : '/admin/overview'; await router.push(next) }
  catch (error) { ElMessage.error(problemMessage(error, '登录或注册失败。')) }
  finally { submitting.value = false }
}
</script>
<template>
  <main class="login-page"><section class="login-hero"><div class="login-symbol">P</div><p class="eyebrow">SMART PARK OPERATIONS</p><h1>让每一次<br><em>公园运营</em>有据可循</h1><p>桌面端统一呈现服务、资产、运维与运行态势。演示数据均标注来源，不代表实际硬件或外部平台已接入。</p><div class="demo-accounts"><b>本地演示账号</b><span>admin / dispatcher / worker / visitor</span><code>ParkDemo!2026</code></div></section><section class="login-card"><div><p class="eyebrow">{{ mode === 'login' ? 'WELCOME BACK' : 'CREATE VISITOR ACCOUNT' }}</p><h2>{{ mode === 'login' ? '登录运行平台' : '注册游客账户' }}</h2><p class="muted">{{ mode === 'login' ? '使用已授权的本地账户进入。' : '注册账户仅授予游客公开服务权限。' }}</p></div><el-form label-position="top" @submit.prevent="submit"><el-form-item v-if="mode === 'register'" label="显示名称"><el-input v-model="form.displayName" autocomplete="name" placeholder="例如：王晓明" /></el-form-item><el-form-item label="用户名"><el-input v-model="form.userName" autocomplete="username" placeholder="输入用户名" /></el-form-item><el-form-item label="密码"><el-input v-model="form.password" type="password" show-password autocomplete="current-password" placeholder="输入密码" @keyup.enter="submit" /></el-form-item><el-button class="login-submit" type="primary" native-type="submit" :loading="submitting">{{ mode === 'login' ? '安全登录' : '注册并登录' }}</el-button></el-form><div class="mode-switch">{{ mode === 'login' ? '需要游客账户？' : '已有账户？' }}<el-button text type="primary" @click="mode = mode === 'login' ? 'register' : 'login'">{{ mode === 'login' ? '立即注册' : '返回登录' }}</el-button></div><el-button text @click="router.push('/visitor')">先浏览公园公开服务</el-button></section></main>
</template>
<style scoped>
.login-page { min-height:100vh; display:grid; grid-template-columns:1.16fr .84fr; background:#f7faf8; }.login-hero { padding:12vh max(60px,calc((100vw - 1280px)/2)); color:#e9f7f0; background:radial-gradient(circle at 70% 25%,#278572,transparent 35%),linear-gradient(135deg,#0b332e,#174d43); }.login-symbol { display:grid; place-items:center; width:48px; height:48px; margin-bottom:60px; background:#5cc7a7; border-radius:14px; color:#133b33; font-size:25px; font-weight:800; }.eyebrow { margin:0 0 11px; color:#7fcdb6; font-weight:700; font-size:11px; letter-spacing:.14em; }.login-hero h1 { font-size:45px; line-height:1.22; margin:0; letter-spacing:.04em; }.login-hero h1 em { color:#80d6ba; font-style:normal; }.login-hero > p:not(.eyebrow) { max-width:460px; margin:28px 0; line-height:1.9; color:#b6d5ca; }.demo-accounts { width:360px; display:grid; gap:7px; padding:16px; border:1px solid rgba(176,226,210,.25); border-radius:12px; background:rgba(0,0,0,.1); font-size:12px; }.demo-accounts span { color:#b3d3c7; }.demo-accounts code { color:#f2d49a; }.login-card { align-self:center; width:420px; margin:auto; padding:40px; }.login-card h2 { margin:0 0 9px; font-size:28px; color:#1b4238; }.login-card .el-form { margin-top:30px; }.login-submit { width:100%; height:42px; margin-top:8px; }.mode-switch { display:flex; align-items:center; margin-top:18px; font-size:13px; color:#71827d; }
</style>
