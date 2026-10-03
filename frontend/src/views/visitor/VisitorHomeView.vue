<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getList, post } from '@/api/resources'
import { http, problemMessage } from '@/api/http'
import { useSessionStore } from '@/stores/session'
import type { Activity, Announcement } from '@/types/domain'
import ParkMap, { type MapAsset, type MapLayer } from '@/components/ParkMap.vue'
import ProductionHint from '@/components/ProductionHint.vue'
import { formatDate, formatNumber } from '@/utils/format'

interface Park { name: string; description?: string; openHours?: string; phone?: string }
interface Toilet { assetId: string; name?: string; longitude?: number; latitude?: number; occupancy?: number | null; unit?: string; collectedAt?: string; stale?: boolean; openHours?: string }
interface Eco { sufficient: boolean; score?: number | null; airScore?: number | null; noiseScore?: number | null; waterScore?: number | null; generatedAt?: string | null; formula: string; source?: string | null }
interface RouteInfo { distanceMeters?: number; geoJson?: { coordinates?: [number, number][] } }
const router = useRouter()
const session = useSessionStore()
const park = ref<Park>({ name: '智慧公园' })
const announcements = ref<Announcement[]>([])
const activities = ref<Activity[]>([])
const tags = ref<{ id: string; name: string }[]>([])
const map = ref<{ layers: MapLayer[]; assets: MapAsset[] }>({ layers: [], assets: [] })
const toilets = ref<Toilet[]>([])
const eco = ref<Eco>()
const loading = ref(true)
const error = ref('')
const from = ref('')
const to = ref('')
const routeInfo = ref<RouteInfo>()
const reserving = ref('')
const points = computed(() => (routeInfo.value?.geoJson?.coordinates ?? [])
  .filter((point) => Number.isFinite(point[0]) && Number.isFinite(point[1]))
  .map((point) => [point[1], point[0]] as [number, number]))
function reservedCount(session: NonNullable<Activity['sessions']>[number]): number { return Number(session.reservedCount ?? session.reserved ?? 0) }
function occupancy(toilet: Toilet): string {
  if (toilet.stale || toilet.occupancy === null || toilet.occupancy === undefined) return '数据过期 / 未知'
  return `${formatNumber(toilet.occupancy, 0)}${toilet.unit === '%' || !toilet.unit ? '%' : ` ${toilet.unit}`}`
}
async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const [parkData, notices, activityData, tagData, mapData, toiletData, ecoData] = await Promise.all([
      http.get<Park>('/public/park'), getList<Announcement>('/public/announcements'), getList<Activity>('/public/activities'),
      getList<{ id: string; name: string }>('/public/tags'), http.get<{ layers: MapLayer[]; assets: MapAsset[] }>('/public/map'),
      getList<Toilet>('/public/toilets'), http.get<Eco>('/public/eco'),
    ])
    park.value = parkData.data
    announcements.value = notices
    activities.value = activityData
    tags.value = tagData
    map.value = mapData.data
    toilets.value = toiletData
    eco.value = ecoData.data
    from.value ||= map.value.assets[0]?.id ?? ''
    to.value ||= map.value.assets[1]?.id ?? ''
  } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false }
}
async function guide(): Promise<void> {
  if (!from.value || !to.value || from.value === to.value) return ElMessage.warning('请选择两个不同地点。')
  try {
    routeInfo.value = (await http.get<RouteInfo>('/public/route', { params: { from: from.value, to: to.value } })).data
    if (!routeInfo.value.geoJson?.coordinates?.length) ElMessage.warning('暂无可用园路，未绘制直线。')
  } catch (exception) { routeInfo.value = undefined; ElMessage.error(problemMessage(exception, '暂无可用园路。')) }
}
async function reserve(sessionId: string): Promise<void> {
  if (!session.isAuthenticated) { await router.push('/login?next=/visitor'); return }
  if (!session.hasAnyRole(['Visitor'])) return ElMessage.warning('仅游客账户可以预约公开活动。')
  reserving.value = sessionId
  try {
    const result = await post<{ reserveCode?: string }>(`/public/sessions/${sessionId}/reserve`)
    ElMessage.success(`预约成功${result.reserveCode ? `，预约码：${result.reserveCode}` : ''}`)
    await load()
  } catch (exception) { ElMessage.error(problemMessage(exception)) } finally { reserving.value = '' }
}
function assetClick(asset: MapAsset): void { if (asset.publicCode) void router.push(`/visitor/assets/${asset.publicCode}`) }
onMounted(() => { void load() })
</script>

<template>
  <div class="visitor-page" v-loading="loading">
    <el-alert v-if="error" class="visitor-error" type="error" :closable="false" :title="error" />
    <section class="hero"><div><p>城市绿境 · 公共服务</p><h1>{{ park.name }}</h1><h2>{{ park.description || '在绿意之间，遇见更从容的公共空间。' }}</h2><div class="hero-info"><span>开放时间 {{ park.openHours || '以园内公示为准' }}</span><span>服务电话 {{ park.phone || '—' }}</span></div><div class="public-tags"><el-tag v-for="tag in tags" :key="tag.id" effect="plain">{{ tag.name }}</el-tag></div></div><div class="hero-card"><b>今日公园服务</b><span>公告、活动、导览与生态信息均来自公开接口。</span><a class="hero-action" href="#activities">查看活动预约</a></div></section>
    <section class="visitor-section"><div class="section-title"><div><p>PUBLIC NOTICE</p><h2>公园公告</h2></div></div><div class="notice-grid"><article v-for="notice in announcements" :key="`${notice.title}-${notice.startsAt ?? ''}-${notice.endsAt ?? ''}`" class="notice"><span>公园服务</span><h3>{{ notice.title }}</h3><p>{{ notice.body }}</p><small>{{ formatDate(notice.startsAt) }} — {{ formatDate(notice.endsAt) }}</small></article><el-empty v-if="!announcements.length && !loading" description="当前暂无生效公告" /></div></section>
    <section id="activities" class="visitor-section tinted"><div class="section-title"><div><p>PROGRAMS</p><h2>活动预约</h2></div><router-link to="/visitor/bookings">查看我的预约 →</router-link></div><div class="activity-grid"><article v-for="activity in activities" :key="activity.id" class="activity"><div><span>{{ activity.location }}</span><h3>{{ activity.title }}</h3><p>{{ activity.description }}</p></div><div class="sessions"><div v-for="item in activity.sessions" :key="item.id" class="session"><div><b>{{ formatDate(item.startsAt) }}</b><small>名额 {{ reservedCount(item) }} / {{ item.capacity }} · {{ item.status }}</small></div><el-button v-if="!session.isAuthenticated || session.hasAnyRole(['Visitor'])" type="primary" size="small" :loading="reserving === item.id" :disabled="item.status !== 'Published' || reservedCount(item) >= item.capacity" @click="reserve(item.id)">{{ reservedCount(item) >= item.capacity ? '已满' : '预约' }}</el-button><span v-else class="muted">仅游客可预约</span></div><p v-if="!activity.sessions?.length" class="muted">暂未开放场次</p></div></article><el-empty v-if="!activities.length && !loading" description="当前暂无可预约活动" /></div></section>
    <section class="visitor-section guide-grid"><div class="map-card"><div class="section-title"><div><p>PARK GUIDE</p><h2>智慧导览</h2></div><span v-if="routeInfo">约 {{ formatNumber(routeInfo.distanceMeters, 0) }} m</span></div><ParkMap :layers="map.layers" :assets="map.assets" :route-points="points" @asset-click="assetClick" /></div><div class="guide-form"><h3>选择园内起终点</h3><el-select v-model="from" placeholder="起点"><el-option v-for="asset in map.assets" :key="asset.id" :label="asset.name" :value="asset.id" /></el-select><el-select v-model="to" placeholder="终点"><el-option v-for="asset in map.assets" :key="asset.id" :label="asset.name" :value="asset.id" /></el-select><el-button type="primary" @click="guide">规划园路路线</el-button><p>仅沿已导入园路计算；无连通园路时不会绘制直线替代路线。</p></div></section>
    <section class="visitor-section service-grid"><article class="service-card"><p>ENVIRONMENT</p><h2>生态环境综合分</h2><template v-if="eco?.sufficient"><strong>{{ formatNumber(eco.score, 1) }}</strong><div><span>空气 {{ formatNumber(eco.airScore, 1) }}</span><span>噪声 {{ formatNumber(eco.noiseScore, 1) }}</span><span>水环境 {{ formatNumber(eco.waterScore, 1) }}</span></div></template><el-empty v-else description="数据不足，未计算综合分" :image-size="48" /><small>来源 {{ eco?.source || '—' }} · {{ formatDate(eco?.generatedAt) }}</small><p class="formula">{{ eco?.formula || '演示指标公式由服务端提供。' }}</p><ProductionHint topic="eco" compact /></article><article class="service-card"><p>PUBLIC FACILITIES</p><h2>公厕服务</h2><div class="toilet-list"><div v-for="toilet in toilets" :key="toilet.assetId"><b>{{ toilet.name || '公园公厕' }}</b><span>{{ occupancy(toilet) }}</span><small>{{ toilet.stale ? '数据已过期' : formatDate(toilet.collectedAt) }} · {{ toilet.openHours || '以园内公示为准' }}</small></div><el-empty v-if="!toilets.length && !loading" description="暂无公开公厕数据" :image-size="48" /></div></article></section>
  </div>
</template>

<style scoped>
.visitor-page { min-height:calc(100vh - 70px); }.visitor-error { margin:18px auto; max-width:1300px; }.hero { display:flex; justify-content:space-between; gap:50px; min-height:360px; padding:72px max(38px,calc((100vw - 1340px)/2)); background:linear-gradient(145deg,#e5f1e7,#fcfdfb 58%,#ebf4e9); }.hero > div:first-child { max-width:730px; }.hero p,.section-title p,.service-card > p { margin:0; color:#329172; font-size:11px; font-weight:700; letter-spacing:.14em; }.hero h1 { margin:12px 0; font-size:48px; color:#174a3c; }.hero h2 { margin:0; color:#496f62; font-size:20px; line-height:1.65; font-weight:500; }.hero-info,.public-tags { display:flex; gap:16px; margin-top:20px; color:#5e766d; font-size:13px; }.hero-card { align-self:center; width:290px; padding:26px; border-radius:16px; background:#fff; box-shadow:0 18px 45px rgba(48,101,74,.13); }.hero-card b,.hero-card span { display:block; }.hero-card span { margin:10px 0 20px; color:#71847d; font-size:13px; line-height:1.6; }.hero-action { display:inline-flex; padding:8px 14px; border-radius:4px; color:#fff; background:#188269; font-size:13px; }.visitor-section { max-width:1340px; margin:0 auto; padding:58px 0; }.visitor-section.tinted { max-width:none; padding-left:max(38px,calc((100vw - 1340px)/2)); padding-right:max(38px,calc((100vw - 1340px)/2)); background:#f3f8f4; }.section-title { display:flex; align-items:end; justify-content:space-between; gap:12px; margin-bottom:25px; }.section-title h2 { margin:7px 0 0; color:#254c41; font-size:27px; }.section-title > span,.section-title > a { color:#6f857d; font-size:13px; }.notice-grid,.activity-grid { display:grid; grid-template-columns:repeat(3,1fr); gap:16px; }.notice,.activity,.service-card,.guide-form { padding:20px; border:1px solid #e4ede8; border-radius:12px; background:#fff; }.notice h3,.activity h3 { margin:8px 0; color:#274e42; }.notice p,.activity p,.guide-form p,.formula { color:#71827d; font-size:13px; line-height:1.6; }.notice small,.session small,.toilet-list small { color:#83918d; font-size:11px; }.sessions { display:grid; gap:9px; margin-top:16px; }.session { display:flex; justify-content:space-between; gap:8px; padding-top:9px; border-top:1px dashed #e0e9e4; }.session b,.session small { display:block; font-size:12px; }.guide-grid,.service-grid { display:grid; grid-template-columns:2fr 1fr; gap:18px; }.map-card { min-height:400px; }.map-card :deep(.park-map) { height:350px; }.guide-form { display:grid; align-content:start; gap:12px; }.guide-form h3 { margin:0 0 8px; color:#31584b; }.service-grid { grid-template-columns:1fr 1fr; }.service-card h2 { margin:8px 0 18px; color:#2a5145; font-size:22px; }.service-card strong { display:block; color:#19745e; font-size:45px; }.service-card > div:not(.toilet-list) { display:flex; gap:12px; color:#59756c; font-size:12px; }.service-card small { display:block; margin-top:14px; color:#788982; font-size:11px; }.formula { font-size:11px; }.toilet-list { display:grid; gap:10px; }.toilet-list div { display:grid; grid-template-columns:1fr auto; gap:5px; padding:10px 0; border-bottom:1px dashed #e0e9e4; color:#376055; }.toilet-list small { grid-column:1 / -1; margin:0; }.muted { color:#7b8d86; }
</style>
