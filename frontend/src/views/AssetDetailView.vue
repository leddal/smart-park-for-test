<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { http, problemMessage } from '@/api/http'
import type { Asset } from '@/types/domain'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

interface WorkOrderRow { id: string; number?: string; title: string; status: string; dueAt?: string }
interface EventRow { id: string; number?: string; title: string; status: string; severity?: string }
interface MaintenanceRow { completedAt?: string; summary?: string }
interface DetailResponse { asset: Asset; workOrders?: WorkOrderRow[]; events?: EventRow[]; maintenance?: MaintenanceRow[] }
interface Detail extends Asset { workOrders: WorkOrderRow[]; events: EventRow[]; maintenance: MaintenanceRow[] }
const route = useRoute()
const router = useRouter()
const asset = ref<Detail>()
const error = ref('')
const loading = ref(true)
const profileRows = computed(() => {
  const item = asset.value
  if (!item) return [] as { label: string; value: string | number | boolean | undefined }[]
  if (item.device) return [{ label: '设备编码', value: String(item.device.code ?? '—') }, { label: '设备类型', value: String(item.device.type ?? '—') }, { label: '台账启用', value: item.device.enabled === true ? '是' : item.device.enabled === false ? '否' : '—' }, { label: '型号', value: String(item.device.model ?? '—') }, { label: '厂家', value: String(item.device.manufacturer ?? '—') }]
  if (item.plant) return [{ label: '树种', value: String(item.plant.species ?? '—') }, { label: '胸径（cm）', value: Number(item.plant.diameterCm ?? 0) || '—' }, { label: '树高（m）', value: Number(item.plant.heightM ?? 0) || '—' }, { label: '健康状态', value: String(item.plant.health ?? '—') }]
  return [{ label: '设施类别', value: String(item.facility?.kind ?? '—') }, { label: '规格', value: String(item.facility?.specification ?? '—') }, { label: '数量', value: Number(item.facility?.quantity ?? 0) || '—' }]
})
async function load(): Promise<void> { loading.value = true; error.value = ''; try { const data = (await http.get<DetailResponse>(`/assets/${route.params.id}`)).data; asset.value = { ...data.asset, workOrders: data.workOrders ?? [], events: data.events ?? [], maintenance: data.maintenance ?? [] } } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false } }
onMounted(() => { void load() })
</script>
<template><div class="page"><el-button text type="primary" @click="router.back()">← 返回资产列表</el-button><div v-if="loading" class="state-block"><el-skeleton :rows="8" /></div><el-result v-else-if="error" icon="error" title="资产加载失败" :sub-title="error" /><template v-else-if="asset"><div class="page-heading"><div><p>{{ asset.code }}</p><h1>{{ asset.name }}</h1><el-space><StatusTag :status="asset.status" /><el-tag>{{ asset.category }}</el-tag></el-space></div><el-button v-if="asset.publicCode" @click="router.push(`/visitor/assets/${asset.publicCode}`)">查看公开页面</el-button></div><div class="grid-3"><section class="surface detail-card"><b>位置与公开资料</b><dl><div><dt>区域</dt><dd>{{ asset.zoneName || asset.zoneId || '—' }}</dd></div><div><dt>坐标</dt><dd>{{ asset.longitude ?? '—' }}, {{ asset.latitude ?? '—' }}</dd></div><div><dt>公开码</dt><dd>{{ asset.publicCode || '—' }}</dd></div></dl><p>{{ asset.publicDescription || '未设置游客公开介绍。' }}</p></section><section class="surface detail-card"><b>{{ asset.device ? '设备台账' : asset.plant ? '植物档案' : '设施资料' }}</b><dl><div v-for="row in profileRows" :key="row.label"><dt>{{ row.label }}</dt><dd>{{ row.value ?? '—' }}</dd></div></dl><p v-if="asset.device" class="muted">设备资料不代表真实接入或在线。</p></section><section class="surface detail-card"><b>维护记录</b><div v-for="record in asset.maintenance" :key="`${record.completedAt}-${record.summary}`" class="record"><span>{{ record.summary || '已完成维护' }}</span><small>{{ formatDate(record.completedAt) }}</small></div><el-empty v-if="!asset.maintenance.length" description="暂无维护记录" :image-size="50" /></section></div><div class="grid-2 relation"><section class="surface detail-card"><b>关联工单</b><el-table :data="asset.workOrders" empty-text="暂无关联工单"><el-table-column prop="number" label="编号" /><el-table-column prop="title" label="任务" /><el-table-column label="状态"><template #default="scope"><StatusTag :status="scope.row.status" /></template></el-table-column><el-table-column label=""><template #default="scope"><el-button link type="primary" @click="router.push(`/admin/operations/my-tasks/${scope.row.id}`)">查看</el-button></template></el-table-column></el-table></section><section class="surface detail-card"><b>关联事件</b><el-table :data="asset.events" empty-text="暂无关联事件"><el-table-column prop="number" label="编号" /><el-table-column prop="title" label="事件" /><el-table-column label="状态"><template #default="scope"><StatusTag :status="scope.row.status" /></template></el-table-column></el-table></section></div></template></div></template>
<style scoped>.page-heading p { margin:0; color:#758680; font-size:12px; }.detail-card { padding:19px; }.detail-card > b { color:#31564c; }.detail-card > p { color:#687b74; font-size:13px; line-height:1.6; }.detail-card dl { margin:15px 0; }.detail-card dl div { display:grid; grid-template-columns:100px 1fr; padding:7px 0; border-bottom:1px dashed #e5ece9; font-size:13px; }.detail-card dt { color:#73847e; }.detail-card dd { margin:0; color:#405b53; word-break:break-word; }.relation { margin-top:18px; }.record { display:flex; justify-content:space-between; align-items:center; gap:8px; padding:9px 0; border-bottom:1px dashed #e5ece9; font-size:13px; color:#5f706c; }.record small { color:#788a83; }</style>
