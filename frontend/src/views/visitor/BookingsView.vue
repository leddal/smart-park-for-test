<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getPage, post } from '@/api/resources'
import { problemMessage } from '@/api/http'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

interface Reservation { id: string; reserveCode: string; status: string; actualStatus?: string; createdAt?: string; session?: { title?: string; startsAt?: string; endsAt?: string } }
const reservations = ref<Reservation[]>([])
const loading = ref(true)
const error = ref('')
const cancelling = ref('')
const canCancel = (item: Reservation): boolean => (item.actualStatus ?? item.status) === 'Valid' && !!item.session?.startsAt && new Date(item.session.startsAt).getTime() > Date.now()
const displayStatus = (item: Reservation): string => item.status === 'Reserved' && item.actualStatus === 'Valid' ? 'Reserved' : item.status
const hasCancellable = computed(() => reservations.value.some(canCancel))
async function load(): Promise<void> { loading.value = true; error.value = ''; try { reservations.value = (await getPage<Reservation>('/public/reservations')).items } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false } }
async function cancel(item: Reservation): Promise<void> { try { await ElMessageBox.confirm('取消仅在场次开始前可用，取消后将释放名额。确定取消预约？', '确认取消', { type: 'warning' }); cancelling.value = item.id; await post(`/public/reservations/${item.id}/cancel`); ElMessage.success('预约已取消。'); await load() } catch (exception) { if (exception !== 'cancel' && exception !== 'close') ElMessage.error(problemMessage(exception)) } finally { cancelling.value = '' } }
onMounted(() => { void load() })
</script>
<template><div class="bookings"><div class="booking-title"><p>MY BOOKINGS</p><h1>我的活动预约</h1><span>预约码仅用于内部核销；仅有效且尚未开始的场次可取消。{{ hasCancellable ? '' : '当前没有可取消的预约。' }}</span></div><el-result v-if="error" icon="error" title="预约加载失败" :sub-title="error"><template #extra><el-button @click="load">重试</el-button></template></el-result><section v-else class="reservation-grid" v-loading="loading"><article v-for="item in reservations" :key="item.id" class="reservation"><div class="reservation-code"><span>预约码</span><b>{{ item.reserveCode }}</b></div><div><h3>{{ item.session?.title || '公园活动' }}</h3><p>{{ formatDate(item.session?.startsAt) }} — {{ formatDate(item.session?.endsAt) }}</p><StatusTag :status="displayStatus(item)" /></div><el-button v-if="canCancel(item)" type="danger" plain size="small" :loading="cancelling === item.id" @click="cancel(item)">取消预约</el-button><span v-else class="not-cancellable">{{ (item.actualStatus ?? item.status) === 'CheckedIn' ? '已核销，不可取消' : '当前不可取消' }}</span></article><el-empty v-if="!loading && !reservations.length" description="暂无活动预约，欢迎前往公园服务查看活动。" /></section></div></template>
<style scoped>.bookings { max-width:1050px; margin:0 auto; padding:65px 0 90px; }.booking-title p { color:#329172; letter-spacing:.15em; font-size:11px; font-weight:700; }.booking-title h1 { margin:9px 0; color:#285044; font-size:31px; }.booking-title span { color:#74867f; font-size:13px; }.reservation-grid { display:grid; grid-template-columns:repeat(2,1fr); gap:16px; margin-top:28px; }.reservation { display:grid; grid-template-columns:120px 1fr auto; gap:16px; align-items:center; padding:18px; border:1px solid #e1ebe6; border-radius:12px; background:#fff; }.reservation-code { padding-right:12px; border-right:1px dashed #d7e4de; }.reservation-code span,.reservation-code b { display:block; }.reservation-code span,.not-cancellable { color:#7b8e86; font-size:11px; }.reservation-code b { margin-top:5px; color:#1f7961; font-family:monospace; font-size:15px; }.reservation h3 { margin:0 0 7px; color:#395b51; }.reservation p { margin:0 0 9px; color:#768781; font-size:12px; }</style>
