<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getPage, post } from '@/api/resources'
import { http, problemMessage } from '@/api/http'
import type { ParkEvent } from '@/types/domain'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

interface Worker { id: string; displayName: string; userName: string }
interface Detail extends ParkEvent { alertId?: string; logs?: { action: string; text?: string; occurredAt?: string; userId?: string }[]; workOrders?: { id: string; number?: string; title: string; status: string; assigneeId?: string; dueAt?: string }[] }
const route = useRoute()
const router = useRouter()
const event = ref<Detail>()
const workers = ref<Worker[]>([])
const loading = ref(true)
const error = ref('')
const submitting = ref(false)
const actionText = ref('')
const dispatchDialog = ref(false)
const dispatch = reactive({ title: '', type: 'Inspection', assigneeId: '', dueAt: '', priority: 'High', details: '', checklist: [] as string[] })
const isFinal = computed(() => event.value?.status === 'Closed' || event.value?.status === 'FalseAlarm')
async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const [eventData, workerData] = await Promise.all([http.get<Detail>(`/emergency/events/${route.params.id}`), getPage<Worker>('/auth/workers', { pageSize: 100 })])
    event.value = eventData.data
    workers.value = workerData.items
  } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false }
}
async function transition(action: 'RequestClosure' | 'Close' | 'FalseAlarm'): Promise<void> {
  if (!event.value || !actionText.value.trim()) return ElMessage.warning('请填写本次操作的处置说明或原因。')
  if (action === 'FalseAlarm') try { await ElMessageBox.confirm('误报处理会按服务端规则抑制本轮告警并取消相关活动工单，确定继续？', '确认误报', { type: 'warning' }) } catch { return }
  submitting.value = true
  try { await post(`/emergency/events/${event.value.id}/transition`, { action, version: event.value.version, text: actionText.value.trim() }); actionText.value = ''; await load(); ElMessage.success('事件状态已更新。') } catch (exception) { ElMessage.error(problemMessage(exception)) } finally { submitting.value = false }
}
function openDispatch(): void { if (event.value) Object.assign(dispatch, { title: `处置：${event.value.title}`, type: 'Inspection', assigneeId: '', dueAt: '', priority: 'High', details: event.value.description ?? '', checklist: [] }); dispatchDialog.value = true }
async function createWorkOrder(): Promise<void> {
  if (!event.value || !dispatch.title || !dispatch.assigneeId || !dispatch.dueAt) return ElMessage.warning('请完整填写工单标题、处理人和截止时间。')
  submitting.value = true
  try { await post('/operations/work-orders', { title: dispatch.title, type: dispatch.type, assigneeId: dispatch.assigneeId, dueAt: dispatch.dueAt, priority: dispatch.priority, eventId: event.value.id, detailsJson: JSON.stringify({ eventNumber: event.value.number, eventTitle: event.value.title, eventDescription: dispatch.details }), checklist: dispatch.checklist.filter(Boolean) }); dispatchDialog.value = false; await load(); ElMessage.success('关联工单已创建。') } catch (exception) { ElMessage.error(problemMessage(exception)) } finally { submitting.value = false }
}
onMounted(() => { void load() })
</script>
<template><div class="page"><el-button text type="primary" @click="router.push('/admin/emergency')">← 返回事件列表</el-button><div v-if="loading" class="state-block"><el-skeleton :rows="8" /></div><el-result v-else-if="error" icon="error" title="事件加载失败" :sub-title="error" /><template v-else-if="event"><div class="detail-head"><div><p>{{ event.number || event.id }}</p><h1>{{ event.title }}</h1><el-space><StatusTag :status="event.status" /><el-tag type="warning">{{ event.severity }}</el-tag><el-tag>{{ event.category }}</el-tag></el-space></div><el-button v-if="!isFinal" type="primary" @click="openDispatch">分派关联工单</el-button></div><div class="grid-2"><section class="surface detail-card"><div class="data-title">事件信息</div><p>{{ event.description || '暂无描述。' }}</p><dl><div><dt>位置</dt><dd>{{ event.longitude ?? '—' }}, {{ event.latitude ?? '—' }}</dd></div><div><dt>关联告警</dt><dd>{{ event.alertId || '无' }}</dd></div><div><dt>状态说明</dt><dd>告警恢复、工单完成不自动办结事件。</dd></div></dl></section><section class="surface detail-card"><div class="data-title">事件状态操作</div><template v-if="!isFinal"><el-input v-model="actionText" type="textarea" :rows="4" placeholder="填写处置说明、办结依据或误报原因（必填）" /><div class="event-actions"><el-button v-if="event.status !== 'PendingClosure'" :loading="submitting" @click="transition('RequestClosure')">申请办结</el-button><el-button v-if="event.status === 'PendingClosure'" type="success" :loading="submitting" @click="transition('Close')">人工办结</el-button><el-button type="danger" plain :loading="submitting" @click="transition('FalseAlarm')">标记误报</el-button></div></template><el-alert v-else type="info" :closable="false" :title="`事件已${event.status === 'Closed' ? '办结' : '标记为误报'}，不能再发起处置操作。`" /></section></div><div class="grid-2 bottom"><section class="surface detail-card"><div class="data-title">关联工单</div><el-table :data="event.workOrders" empty-text="尚未分派任务"><el-table-column prop="number" label="编号" /><el-table-column prop="title" label="任务" /><el-table-column label="状态"><template #default="scope"><StatusTag :status="scope.row.status" /></template></el-table-column><el-table-column label=""><template #default="scope"><el-button link type="primary" @click="router.push(`/admin/operations/work-orders/${scope.row.id}`)">查看</el-button></template></el-table-column></el-table></section><section class="surface detail-card"><div class="data-title">事件日志</div><el-timeline><el-timeline-item v-for="log in event.logs" :key="`${log.occurredAt}-${log.action}`" :timestamp="formatDate(log.occurredAt)"><b>{{ log.action }}</b><p>{{ log.text || '—' }}</p></el-timeline-item></el-timeline><el-empty v-if="!event.logs?.length" description="暂无事件日志" :image-size="50" /></section></div><el-dialog v-model="dispatchDialog" title="分派关联工单" width="680px" :close-on-click-modal="false"><el-form label-position="top"><div class="form-row"><el-form-item label="工单标题" required><el-input v-model="dispatch.title" /></el-form-item><el-form-item label="工单类型"><el-select v-model="dispatch.type"><el-option v-for="item in ['Maintenance','Repair','Flood','PlantCare','Patrol','Cleaning','Inspection']" :key="item" :value="item" /></el-select></el-form-item></div><div class="form-row"><el-form-item label="处理人" required><el-select v-model="dispatch.assigneeId"><el-option v-for="worker in workers" :key="worker.id" :label="worker.displayName || worker.userName" :value="worker.id" /></el-select></el-form-item><el-form-item label="截止时间" required><el-date-picker v-model="dispatch.dueAt" type="datetime" value-format="YYYY-MM-DDTHH:mm:ssZ" /></el-form-item></div><el-form-item label="事件关联处置说明"><el-input v-model="dispatch.details" type="textarea" :rows="3" /></el-form-item><p class="muted">未指定检查项时，服务端会按工单类型使用受控默认检查表。</p><el-button type="primary" :loading="submitting" @click="createWorkOrder">创建并分派工单</el-button></el-form></el-dialog></template></div></template>
<style scoped>.detail-head { display:flex; justify-content:space-between; align-items:end; margin:15px 0 20px; }.detail-head p { margin:0; color:#788983; font-size:12px; }.detail-head h1 { margin:5px 0 10px; color:#294b42; }.detail-card { padding:19px; }.detail-card > p { color:#63766f; line-height:1.7; }.detail-card dl div { display:grid; grid-template-columns:100px 1fr; padding:8px 0; border-bottom:1px dashed #e2ebe7; font-size:13px; }.detail-card dt { color:#71837c; }.detail-card dd { margin:0; color:#435c54; }.event-actions { display:flex; gap:8px; margin-top:13px; }.bottom { margin-top:18px; }.detail-card :deep(.el-timeline-item__content) p { margin:4px 0; color:#64766f; }.form-row { display:grid; grid-template-columns:1fr 1fr; gap:14px; }</style>
