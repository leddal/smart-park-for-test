<script setup lang="ts">import { useRouter } from 'vue-router'; const router = useRouter()</script>
<template><main class="simple-state"><el-result icon="warning" sub-title="当前账户无权查看该页面。"><template #title><h1>无访问权限</h1></template><template #extra><el-button type="primary" @click="router.push('/visitor')">返回公开服务</el-button></template></el-result></main></template>
<style scoped>.simple-state { min-height:100vh; display:grid; place-items:center; background:#f4f8f6; }</style>
