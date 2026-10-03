<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { getPage } from '@/api/resources'
import { problemMessage } from '@/api/http'
import { useSessionStore } from '@/stores/session'
import type { WorkOrder } from '@/types/domain'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

const session = useSessionStore(); const tab = ref('active'); const orders = ref<WorkOrder[]>([]); const loading = ref(true); const error = ref(''); const statuses = computed(() => tab.value === 'todo' ? ['Assigned'] : tab.value === 'active' ? ['Accepted','InProgress'] : tab.value === 'review' ? ['PendingReview'] : ['Completed','Cancelled'])
async function load(): Promise<void> { loading.value = true; error.value = ''; try { const data = await getPage<WorkOrder>('/operations/work-orders', { statuses: statuses.value.join(','), pageSize: 100 }); orders.value = data.items.filter((item) => !session.hasAnyRole(['Administrator', 'Dispatcher']) || item.assigneeId === session.user?.id) } catch (e) { error.value = problemMessage(e) } finally { loading.value = false } }
onMounted(() => { void load() })
</script>
<template><div class="page"><div class="page-heading"><div><h1>我的任务</h1><p>仅显示当前账户被分派的桌面作业任务；接单、开始与提交均由服务端再次校验归属。</p></div><el-button @click="load">刷新</el-button></div><el-alert v-if="error" type="error" :title="error" :closable="false" /><el-tabs v-model="tab" @tab-change="load"><el-tab-pane label="待接单" name="todo" /><el-tab-pane label="处理中" name="active" /><el-tab-pane label="待验收" name="review" /><el-tab-pane label="已完成 / 已取消" name="done" /></el-tabs><section class="task-list" v-loading="loading"><article v-for="order in orders" :key="order.id" class="surface task-card"><div class="task-card-top"><div><span>{{ order.number || order.id }}</span><h3>{{ order.title }}</h3></div><StatusTag :status="order.status" /></div><div class="task-meta"><span>{{ order.type }}</span><span>优先级 {{ order.priority || 'Normal' }}</span><span>截止 {{ formatDate(order.dueAt) }}</span></div><p>{{ order.details || '请查看任务详情及检查清单。' }}</p><el-button type="primary" @click="$router.push(`/admin/operations/my-tasks/${order.id}`)">打开任务并处理</el-button></article><el-empty v-if="!loading && !orders.length" description="该分类下暂无任务" /></section></div></template>
<style scoped>.task-list { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:15px; }.task-card { padding:18px; }.task-card-top { display:flex; justify-content:space-between; gap:20px; }.task-card-top span { color:#7b8c86; font-size:11px; }.task-card h3 { margin:5px 0; color:#2b4d44; font-size:17px; }.task-meta { display:flex; flex-wrap:wrap; gap:12px; margin-top:14px; color:#658078; font-size:12px; }.task-card p { min-height:38px; color:#70817b; font-size:13px; line-height:1.55; }</style>
