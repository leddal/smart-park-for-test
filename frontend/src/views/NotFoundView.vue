<script setup lang="ts">import { useRouter } from 'vue-router'; const router = useRouter()</script>
<template><main class="simple-state"><el-result icon="info" title="页面不存在" sub-title="请检查访问地址或从系统导航重新进入。"><template #extra><el-button type="primary" @click="router.push('/visitor')">前往公园服务</el-button></template></el-result></main></template>
<style scoped>.simple-state { min-height:100vh; display:grid; place-items:center; background:#f4f8f6; }</style>
