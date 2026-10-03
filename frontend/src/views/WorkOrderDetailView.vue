<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getPage, post } from '@/api/resources'
import { http, problemMessage } from '@/api/http'
import { useSessionStore } from '@/stores/session'
import type { WorkOrder } from '@/types/domain'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

interface Worker { id: string; displayName: string; userName: string }
interface Detail extends WorkOrder { details?: unknown; checklist?: { id?: string; name: string; result?: string; completed?: boolean }[]; logs?: { action: string; text?: string; occurredAt?: string; userId?: string }[]; feedbacks?: { text: string; submittedAt?: string; authorId?: string }[]; attachments?: { fileId?: string; id?: string; fileName?: string; url?: string }[] }
const route = useRoute()
const router = useRouter()
const session = useSessionStore()
const order = ref<Detail>()
const workers = ref<Worker[]>([])
const loading = ref(true)
const submitting = ref(false)
const error = ref('')
const files = ref<File[]>([])
const uploadedIds = ref<string[]>([])
const transition = reactive({ text: '', assigneeId: '' })
const isWorker = computed(() => session.hasAnyRole(['Worker']) && !session.hasAnyRole(['Administrator', 'Dispatcher']))
const isManager = computed(() => session.hasAnyRole(['Administrator', 'Dispatcher']))
const workerActions = computed(() => order.value?.status === 'Assigned' ? ['Accept'] : order.value?.status === 'Accepted' ? ['Start'] : order.value?.status === 'InProgress' ? ['Submit'] : [])
const managerActions = computed(() => !isManager.value || !order.value || ['Completed', 'Cancelled'].includes(order.value.status) ? [] : order.value.status === 'PendingReview' ? ['Approve', 'Reject'] : ['Cancel', 'Reassign'])
const detailsRows = computed(() => {
  const details = order.value?.details
  if (!details || typeof details !== 'object' || Array.isArray(details)) return [] as { label: string; value: string }[]
  return Object.entries(details as Record<string, unknown>).filter(([, value]) => ['string', 'number', 'boolean'].includes(typeof value) && String(value).trim()).map(([label, value]) => ({ label, value: String(value) }))
})
function readyToSubmit(): boolean { return !!order.value?.checklist?.length && order.value.checklist.every((item) => !!item.result?.trim()) }
async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const detail = await http.get<Detail>(`/operations/work-orders/${route.params.id}`)
    order.value = detail.data
    transition.assigneeId = detail.data.assigneeId ?? ''
    workers.value = isManager.value ? (await getPage<Worker>('/auth/workers', { pageSize: 100 })).items : []
  } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false }
}
function selectFiles(event: Event): void { files.value = Array.from((event.target as HTMLInputElement).files ?? []).slice(0, 5); uploadedIds.value = [] }
async function uploadPhotos(): Promise<string[]> { for (const file of files.value.slice(uploadedIds.value.length, 5)) { const body = new FormData(); body.append('file', file); const { data } = await http.post<{ id: string }>(`/operations/work-orders/${route.params.id}/photos`, body); uploadedIds.value.push(data.id) } return [...uploadedIds.value] }
async function transit(action: string): Promise<void> {
  if (!order.value) return
  if (action === 'Submit' && !readyToSubmit()) return ElMessage.warning('请为每一项检查项明确选择结果后再提交。')
  if (['Submit', 'Reject', 'Cancel', 'Reassign'].includes(action) && !transition.text.trim()) return ElMessage.warning('该操作必须填写处置或原因说明。')
  if (action === 'Reassign' && !transition.assigneeId) return ElMessage.warning('请选择新的处理人。')
  if (action === 'Cancel') try { await ElMessageBox.confirm('取消后工单不可继续处理，确定继续？', '确认取消', { type: 'warning' }) } catch { return }
  submitting.value = true
  try {
    const attachmentIds = action === 'Submit' ? await uploadPhotos() : []
    const checklistResults = action === 'Submit' ? (order.value.checklist ?? []).map((item) => ({ name: item.name, result: item.result!.trim() })) : undefined
    await post(`/operations/work-orders/${order.value.id}/transition`, { action, version: order.value.version, text: transition.text || undefined, assigneeId: action === 'Reassign' ? transition.assigneeId : undefined, checklistResults, attachmentIds })
    ElMessage.success('状态已更新。')
    transition.text = ''
    files.value = []
    uploadedIds.value = []
    await load()
  } catch (exception) { ElMessage.error(problemMessage(exception)) } finally { submitting.value = false }
}
function download(attachment: { fileId?: string; id?: string; url?: string }): void { window.open(attachment.url || `/api/files/${attachment.fileId ?? attachment.id}`, '_blank', 'noopener') }
onMounted(() => { void load() })
</script>
<template><div class="page"><el-button text type="primary" @click="router.push(isWorker ? '/admin/operations/my-tasks' : '/admin/operations')">← 返回列表</el-button><div v-if="loading" class="state-block"><el-skeleton :rows="8" animated /></div><el-result v-else-if="error" icon="error" title="加载工单失败" :sub-title="error"><template #extra><el-button @click="load">重试</el-button></template></el-result><template v-else-if="order"><div class="detail-head"><div><p>{{ order.number || order.id }}</p><h1>{{ order.title }}</h1><div class="detail-tags"><StatusTag :status="order.status" /><el-tag>{{ order.type }}</el-tag><el-tag type="warning">{{ order.priority }}</el-tag><el-button v-if="order.assetId" link type="primary" @click="router.push(`/admin/assets/${order.assetId}`)">关联资产</el-button></div></div><div><span class="muted">截止时间</span><b>{{ formatDate(order.dueAt) }}</b></div></div><div class="grid-2"><section class="surface detail-section"><div class="data-title">任务说明与检查</div><dl v-if="detailsRows.length" class="details"><div v-for="row in detailsRows" :key="row.label"><dt>{{ row.label }}</dt><dd>{{ row.value }}</dd></div></dl><p v-else class="muted">暂无补充说明。</p><div class="checklist"><label v-for="(item, index) in order.checklist" :key="item.id || item.name"><span>{{ item.name }}</span><el-select v-model="order.checklist![index].result" :disabled="!isWorker || order.status !== 'InProgress'" placeholder="请选择结果"><el-option label="已检查正常" value="Pass" /><el-option label="发现问题" value="Issue" /><el-option label="不适用" value="N/A" /></el-select></label><el-empty v-if="!order.checklist?.length" description="该任务无预置检查项" :image-size="60" /></div></section><section class="surface detail-section"><div class="data-title">状态流转</div><div class="flow"><span>Assigned</span><i /><span>Accepted</span><i /><span>InProgress</span><i /><span>PendingReview</span><i /><span>Completed</span></div><p class="muted">验收驳回会回到处理中；工单完成不等于事件已办结。</p><el-form label-position="top"><el-form-item v-if="isManager && managerActions.includes('Reassign')" label="重新派员"><el-select v-model="transition.assigneeId" placeholder="选择新的处理人"><el-option v-for="worker in workers" :key="worker.id" :value="worker.id" :label="worker.displayName || worker.userName" /></el-select></el-form-item><el-form-item v-if="[...workerActions, ...managerActions].some((action) => ['Submit','Reject','Cancel','Reassign'].includes(action))" label="处置说明 / 原因"><el-input v-model="transition.text" type="textarea" :rows="3" placeholder="提交、驳回或取消时必填" /></el-form-item><el-form-item v-if="isWorker && order.status === 'InProgress'" label="可选照片（最多 5 张）"><input type="file" accept="image/png,image/jpeg,image/webp" multiple @change="selectFiles" /></el-form-item><div class="action-row"><el-button v-for="action in workerActions" :key="action" type="primary" :disabled="action === 'Submit' && !readyToSubmit()" :loading="submitting" @click="transit(action)">{{ action }}</el-button><el-button v-for="action in managerActions" :key="action" :type="action === 'Approve' ? 'success' : action === 'Cancel' ? 'danger' : 'primary'" :plain="action === 'Cancel'" :loading="submitting" @click="transit(action)">{{ action }}</el-button></div></el-form></section></div><div class="grid-2 bottom"><section class="surface detail-section"><div class="data-title">反馈记录</div><article v-for="feedback in order.feedbacks" :key="`${feedback.submittedAt}-${feedback.text}`" class="feedback"><b>{{ formatDate(feedback.submittedAt) }}</b><p>{{ feedback.text }}</p></article><el-empty v-if="!order.feedbacks?.length" description="暂无反馈" :image-size="50" /></section><section class="surface detail-section"><div class="data-title">操作日志与附件</div><article v-for="log in order.logs" :key="`${log.occurredAt}-${log.action}`" class="log"><b>{{ log.action }}</b><span>{{ log.text || '—' }}</span><small>{{ formatDate(log.occurredAt) }}</small></article><div class="attachments"><div v-for="attachment in order.attachments" :key="attachment.fileId || attachment.id" class="attachment"><img :src="attachment.url || `/api/files/${attachment.fileId ?? attachment.id}`" :alt="attachment.fileName || '工单附件'" /><el-button link type="primary" @click="download(attachment)">{{ attachment.fileName || '下载附件' }}</el-button></div></div><el-empty v-if="!order.logs?.length && !order.attachments?.length" description="暂无日志或附件" :image-size="50" /></section></div></template></div></template>
<style scoped>.detail-head { margin:12px 0 20px; display:flex; justify-content:space-between; align-items:end; }.detail-head p { margin:0; color:#80908b; font-size:12px; }.detail-head h1 { margin:5px 0 9px; color:#254a40; font-size:25px; }.detail-tags { display:flex; gap:8px; }.detail-head > div:last-child { display:grid; gap:6px; text-align:right; color:#395e54; }.detail-section { padding:20px; }.details { color:#61746e; line-height:1.7; }.details div { display:grid; grid-template-columns:130px 1fr; gap:8px; padding:7px 0; border-bottom:1px dashed #e4ece8; }.details dt { color:#72847d; }.details dd { margin:0; word-break:break-word; }.checklist { margin-top:17px; display:grid; gap:10px; }.checklist label { display:grid; grid-template-columns:1fr 150px; gap:12px; align-items:center; padding:10px; border-radius:8px; background:#f5f8f6; color:#3e5a52; font-size:13px; }.flow { display:flex; align-items:center; justify-content:space-between; margin:24px 0 11px; color:#488374; font-size:11px; }.flow i { flex:1; height:1px; margin:0 5px; background:#a6d2c4; }.action-row { display:flex; flex-wrap:wrap; gap:8px; }.bottom { margin-top:18px; }.feedback,.log { padding:11px 0; border-bottom:1px dashed #e2eae7; }.feedback b,.log small { display:block; color:#778780; font-size:11px; }.feedback p,.log span { display:block; margin:5px 0; color:#4c665e; font-size:13px; }.attachments { margin-top:10px; display:grid; gap:8px; justify-items:start; }.attachment { display:flex; align-items:center; gap:8px; }.attachment img { width:82px; height:62px; border:1px solid #dfe9e4; border-radius:4px; object-fit:cover; }</style>
