<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { http, problemMessage } from '@/api/http'
import type { Metric } from '@/types/domain'
import MetricChart from '@/components/MetricChart.vue'
import ParkMap, { type MapAsset, type MapLayer } from '@/components/ParkMap.vue'
import SourceBadge from '@/components/SourceBadge.vue'
import SectionState from '@/components/SectionState.vue'
import { formatDate, formatNumber } from '@/utils/format'

interface WorkOrders { active: number; open: number; pendingReview: number; completed: number }
interface Summary { parkName: string; generatedAt: string; simulationEnabled: boolean; visitors: { inside: number; todayIn: number; todayOut: number; source: 'Seed' | 'Simulation' | 'Manual' }; devices: { enabled: number; disabled: number; fresh: number; stale: number }; alerts: number; events: number; workOrders?: WorkOrders | number; workOrderSummary?: WorkOrders; assetCategories: { category: string; count: number }[]; metrics: Metric[]; eco?: { score?: number | null; generatedAt?: string | null; source?: 'Seed' | 'Simulation' | 'Manual' | null }; carbon?: { totalKgCo2e?: number; validCount?: number; missingCount?: number }; cacheStatus?: string }
interface Trend { time: string; value: number }
const router = useRouter()
const summary = ref<Summary>()
const trend = ref<Trend[]>([])
const layers = ref<MapLayer[]>([])
const assets = ref<MapAsset[]>([])
const loading = ref(true)
const error = ref('')
const metricCode = ref('')
let timer: number | undefined
const orderCounts = computed<WorkOrders>(() => typeof summary.value?.workOrders === 'object' ? summary.value.workOrders : summary.value?.workOrderSummary ?? { active: 0, open: 0, pendingReview: 0, completed: 0 })
const pendingOrders = computed(() => orderCounts.value.open + orderCounts.value.pendingReview)
const selectedMetric = computed(() => summary.value?.metrics.find((item) => item.metricCode === metricCode.value) ?? summary.value?.metrics[0])
const kpis = computed(() => summary.value ? [
  { label: '在园人数', value: formatNumber(summary.value.visitors.inside), hint: `今日入园 ${formatNumber(summary.value.visitors.todayIn)} · 出园 ${formatNumber(summary.value.visitors.todayOut)}`, tone: 'green' },
  { label: '未恢复告警', value: formatNumber(summary.value.alerts), hint: '需在告警中心确认与跟踪', tone: 'orange' },
  { label: '处置中事件', value: formatNumber(summary.value.events), hint: '事件办结需人工确认', tone: 'red' },
  { label: '待办工单', value: formatNumber(pendingOrders.value), hint: '含待接单、处理中、待验收', tone: 'blue' },
] : [])
async function loadTrend(): Promise<void> {
  if (!metricCode.value) { trend.value = []; return }
  trend.value = (await http.get<Trend[]>('/overview/trends', { params: { metricCode: metricCode.value, hours: 24 } })).data
}
async function load(): Promise<void> {
  loading.value = !summary.value
  error.value = ''
  try {
    const [overview, map] = await Promise.all([http.get<Summary>('/overview/summary'), http.get<{ layers: MapLayer[]; assets: MapAsset[] }>('/public/map')])
    summary.value = overview.data
    metricCode.value ||= overview.data.metrics[0]?.metricCode ?? ''
    await loadTrend()
    layers.value = map.data.layers ?? []
    assets.value = map.data.assets ?? []
  } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false }
}
async function changeMetric(): Promise<void> { try { await loadTrend() } catch (exception) { error.value = problemMessage(exception) } }
function schedule(): void { window.clearInterval(timer); timer = window.setInterval(() => { if (!document.hidden) void load() }, 10_000) }
function jumpAsset(asset: MapAsset): void { void router.push(`/admin/assets/${asset.id}`) }
watch(metricCode, () => { void changeMetric() })
onMounted(() => { void load(); schedule() })
onBeforeUnmount(() => window.clearInterval(timer))
</script>
<template><div class="page overview-page"><div class="page-heading"><div><h1>公园运行总览</h1><p>聚合指标每 10 秒刷新；页面隐藏时暂停。采集数据以来源和时间为准。</p></div><div class="overview-meta"><el-tag :type="summary?.simulationEnabled ? 'warning' : 'info'">{{ summary?.simulationEnabled ? '模拟器已显式开启' : '模拟器未开启' }}</el-tag><span>{{ formatDate(summary?.generatedAt) }}</span></div></div><SectionState :loading="loading" :error="error" @retry="load"><template v-if="summary"><div class="kpi-grid"><article v-for="kpi in kpis" :key="kpi.label" class="kpi-card" :class="kpi.tone"><span>{{ kpi.label }}</span><strong>{{ kpi.value }}</strong><small>{{ kpi.hint }}</small></article></div><div class="grid-3 overview-row"><section class="surface chart-panel"><div class="panel-heading"><div><b>环境指标趋势</b><span>后端聚合 · 24 小时</span></div><SourceBadge :source="selectedMetric?.source" /></div><el-select v-model="metricCode" aria-label="选择趋势指标" size="small" class="metric-select"><el-option v-for="metric in summary.metrics" :key="metric.metricCode" :label="`${metric.metricCode} (${metric.unit})`" :value="metric.metricCode" /></el-select><MetricChart :points="trend" :unit="selectedMetric?.unit" /></section><section class="surface metric-panel"><div class="panel-heading"><div><b>运行状态</b><span>不是硬件在线状态</span></div></div><div class="status-pairs"><div><span>启用台账设备</span><b>{{ summary.devices.enabled }}</b></div><div><span>停用台账设备</span><b>{{ summary.devices.disabled }}</b></div><div><span>新鲜数据</span><b>{{ summary.devices.fresh }}</b></div><div><span>过期数据</span><b>{{ summary.devices.stale }}</b></div></div><div class="eco-score"><span>生态综合分（演示）</span><strong>{{ formatNumber(summary.eco?.score, 1) }}</strong><small>数据不足时不补零 · {{ formatDate(summary.eco?.generatedAt) }}</small></div></section><section class="surface metric-panel"><div class="panel-heading"><div><b>资产与碳储量</b><span>非权威估算</span></div></div><div class="category-list"><div v-for="category in summary.assetCategories" :key="category.category"><span>{{ category.category }}</span><b>{{ category.count }}</b></div></div><div class="carbon"><b>{{ formatNumber(summary.carbon?.totalKgCo2e, 1) }} kg CO₂e</b><span>有效植物 {{ summary.carbon?.validCount ?? 0 }} 株 · 缺失 {{ summary.carbon?.missingCount ?? 0 }} 株</span></div></section></div><section class="surface map-panel"><div class="panel-heading"><div><b>公园一张图</b><span>本地 GeoJSON / DOM 图层，点击资产查看关联信息</span></div><span class="muted">缓存：{{ summary.cacheStatus || '未知' }}</span></div><ParkMap :layers="layers" :assets="assets" @asset-click="jumpAsset" /></section></template></SectionState></div></template>
<style scoped>.overview-meta { display:flex; gap:12px; align-items:center; color:#71837d; font-size:12px; }.kpi-grid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:16px; }.kpi-card { min-height:132px; border-radius:14px; padding:19px; color:#f5fffb; display:flex; flex-direction:column; background:#177766; }.kpi-card.orange { background:#bb7e27; }.kpi-card.red { background:#b45747; }.kpi-card.blue { background:#3a7d93; }.kpi-card span { font-size:13px; opacity:.85; }.kpi-card strong { margin:8px 0 auto; font-size:34px; }.kpi-card small { font-size:11px; opacity:.85; }.overview-row { margin:18px 0; }.chart-panel { grid-column:span 2; padding:18px; }.metric-panel { padding:18px; }.panel-heading { display:flex; justify-content:space-between; gap:15px; align-items:start; }.panel-heading b { display:block; color:#25473f; }.panel-heading span { display:block; margin-top:4px; font-size:11px; color:#74857f; }.metric-select { width:220px; margin:14px 0 -4px; }.status-pairs { display:grid; grid-template-columns:1fr 1fr; gap:12px; margin:24px 0; }.status-pairs div { padding:10px; border-radius:8px; background:#f4f8f6; }.status-pairs span,.carbon span { display:block; color:#758680; font-size:11px; }.status-pairs b { display:block; margin-top:4px; font-size:21px; color:#2b5b50; }.eco-score { padding:12px; border-left:3px solid #60a98c; background:#f3faf6; }.eco-score span,.eco-score small { display:block; color:#61746e; font-size:11px; }.eco-score strong { display:block; margin:4px 0; color:#176c59; font-size:26px; }.category-list { display:grid; gap:8px; margin:18px 0; }.category-list div { display:flex; justify-content:space-between; padding-bottom:7px; border-bottom:1px dashed #e2ebe8; color:#61736d; font-size:13px; }.category-list b { color:#2b5a50; }.carbon { margin-top:17px; padding:12px; border-radius:8px; background:#f0f6ed; }.carbon b { display:block; color:#39724e; font-size:16px; margin-bottom:5px; }.map-panel { height:440px; padding:18px; }.map-panel :deep(.park-map) { height:375px; margin-top:12px; }</style>
