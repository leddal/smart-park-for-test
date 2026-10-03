<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getList, getPage, post } from '@/api/resources'
import { problemMessage } from '@/api/http'
import { useSessionStore } from '@/stores/session'
import ProductionHint from '@/components/ProductionHint.vue'
import StatusTag from '@/components/StatusTag.vue'
import { formatDate } from '@/utils/format'

interface ControlDevice { id: string; name?: string; assetName?: string; code: string; type: 'Irrigation' | 'Lamp' | 'Broadcast'; enabled: boolean; controlState?: string; ControlState?: string; state?: string }
interface Command { id: string; deviceId: string; action: string; outcome: string; status: string; createdAt?: string; processedAt?: string; text?: string; durationSeconds?: number; brightness?: number; receipt?: string }
const session = useSessionStore()
const isAdmin = computed(() => session.hasAnyRole(['Administrator']))
const devices = ref<ControlDevice[]>([])
const commands = ref<Command[]>([])
const loading = ref(true)
const error = ref('')
const dialog = ref(false)
const sending = ref(false)
const form = reactive({ deviceId: '', action: 'Start', durationSeconds: 300, brightness: 60, text: '', outcome: 'Success' })
const selected = computed(() => devices.value.find((item) => item.id === form.deviceId))
const deviceName = (device: ControlDevice): string => device.assetName || device.name || device.code
const state = (device: ControlDevice): string => device.controlState || device.ControlState || device.state || 'Unknown'
const commandDeviceName = (command: Command): string => devices.value.find((item) => item.id === command.deviceId) ? deviceName(devices.value.find((item) => item.id === command.deviceId)!) : command.deviceId
const hasPending = computed(() => commands.value.some((item) => item.status === 'Pending'))
let poller: number | undefined
async function load(): Promise<void> { loading.value = !devices.value.length; error.value = ''; try { const [deviceData, commandData] = await Promise.all([getList<ControlDevice>('/control/devices'), getPage<Command>('/control/commands', { pageSize: 100 })]); devices.value = deviceData; commands.value = commandData.items } catch (exception) { error.value = problemMessage(exception) } finally { loading.value = false } }
function openCommand(device: ControlDevice, action: string): void { Object.assign(form, { deviceId: device.id, action, durationSeconds: 300, brightness: 60, text: '', outcome: 'Success' }); dialog.value = true }
async function send(): Promise<void> {
  if (!form.deviceId) return
  if (form.action === 'Start' && selected.value?.type === 'Irrigation' && (form.durationSeconds < 1 || form.durationSeconds > 3600)) return ElMessage.warning('灌溉时长必须为 1 到 3600 秒。')
  if (form.action === 'Broadcast' && !form.text.trim()) return ElMessage.warning('请填写广播文本。')
  sending.value = true
  try { await post('/control/commands', { ...form, outcome: isAdmin.value ? form.outcome : 'Success' }); dialog.value = false; await load(); ElMessage.success('模拟指令已提交，正在等待本地模拟回执。') } catch (exception) { ElMessage.error(problemMessage(exception)) } finally { sending.value = false }
}
async function playAudio(): Promise<void> { try { await new Audio('/api/platform/samples/broadcast.wav').play() } catch { ElMessage.warning('本地演示音频暂不可播放。') } }
async function confirmStop(device: ControlDevice): Promise<void> { try { await ElMessageBox.confirm(`停止“${deviceName(device)}”的模拟控制？`, '确认模拟操作', { type: 'warning' }); openCommand(device, 'Stop') } catch { /* dismissed */ } }
function schedulePolling(): void { poller = window.setInterval(() => { if (!document.hidden && hasPending.value) void load() }, 2_000) }
onMounted(() => { void load(); schedulePolling() })
onBeforeUnmount(() => window.clearInterval(poller))
</script>
<template><div class="page"><div class="page-heading"><div><h1>智能控制</h1><p>所有操作只提交本地模拟指令；成功回执不代表现场设备已动作。</p></div><el-button @click="load">刷新模拟状态</el-button></div><el-alert v-if="error" :title="error" type="error" :closable="false" /><section class="device-grid" v-loading="loading"><article v-for="device in devices" :key="device.id" class="surface device-card"><div class="device-top"><div><span class="device-type">{{ device.type }}</span><h3>{{ deviceName(device) }}</h3><small>{{ device.code }}</small></div><StatusTag :status="state(device)" /></div><div class="control-state"><span>当前演示状态</span><b>{{ state(device) }}</b><small>{{ device.enabled ? '台账已启用' : '台账已停用，不可发起控制' }}</small></div><div class="device-actions"><template v-if="device.type === 'Irrigation'"><el-button type="success" :disabled="!device.enabled" @click="openCommand(device, 'Start')">模拟启动</el-button><el-button :disabled="!device.enabled" @click="confirmStop(device)">模拟停止</el-button></template><template v-else-if="device.type === 'Lamp'"><el-button type="success" :disabled="!device.enabled" @click="openCommand(device, 'Start')">模拟开启</el-button><el-button type="primary" :disabled="!device.enabled" @click="openCommand(device, 'Brightness')">模拟亮度</el-button><el-button :disabled="!device.enabled" @click="confirmStop(device)">模拟关闭</el-button></template><template v-else><el-button type="warning" :disabled="!device.enabled" @click="openCommand(device, 'Broadcast')">发送模拟广播</el-button><el-button :disabled="!device.enabled" @click="confirmStop(device)">模拟停止广播</el-button><el-button @click="playAudio">预听本地音频</el-button></template></div></article><el-empty v-if="!loading && !devices.length" description="暂无可控制的示例设备" /></section><section class="surface command-log"><div class="data-title">模拟指令回执</div><p class="muted">待处理指令每 2 秒刷新；仅显示服务端实际回执字段。</p><el-table :data="commands" empty-text="暂无指令记录"><el-table-column label="目标设备" min-width="170"><template #default="scope">{{ commandDeviceName(scope.row) }}</template></el-table-column><el-table-column prop="action" label="动作" width="130" /><el-table-column label="参数" min-width="180"><template #default="scope"><span v-if="scope.row.durationSeconds">时长 {{ scope.row.durationSeconds }} 秒</span><span v-else-if="scope.row.brightness !== undefined">亮度 {{ scope.row.brightness }}%</span><span v-else-if="scope.row.text">{{ scope.row.text }}</span><span v-else>—</span></template></el-table-column><el-table-column label="状态" width="140"><template #default="scope"><StatusTag :status="scope.row.status" /></template></el-table-column><el-table-column label="结果" width="130"><template #default="scope"><StatusTag :status="scope.row.outcome" /></template></el-table-column><el-table-column prop="receipt" label="实际回执" min-width="220" /><el-table-column label="时间" width="180"><template #default="scope">{{ formatDate(scope.row.processedAt || scope.row.createdAt) }}</template></el-table-column></el-table></section><ProductionHint topic="control" /></div><el-dialog v-model="dialog" :title="`模拟${form.action}指令`" width="520px" :close-on-click-modal="false"><el-form label-position="top"><el-form-item label="目标设备"><el-input :model-value="selected ? deviceName(selected) : ''" disabled /></el-form-item><el-form-item v-if="selected?.type === 'Irrigation' && form.action === 'Start'" label="模拟运行时长（秒）"><el-input-number v-model="form.durationSeconds" :min="1" :max="3600" /></el-form-item><el-form-item v-if="form.action === 'Brightness'" label="模拟亮度（%）"><el-input-number v-model="form.brightness" :min="0" :max="100" /></el-form-item><el-form-item v-if="form.action === 'Broadcast'" label="广播文本" required><el-input v-model="form.text" type="textarea" :rows="3" /></el-form-item><el-form-item v-if="isAdmin" label="模拟结果注入"><el-select v-model="form.outcome"><el-option label="模拟成功" value="Success" /><el-option label="模拟失败" value="Fail" /><el-option label="模拟超时" value="Timeout" /></el-select></el-form-item><el-button type="primary" :loading="sending" @click="send">提交模拟指令</el-button></el-form></el-dialog></template>
<style scoped>.device-grid { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:16px; }.device-card { padding:18px; }.device-top { display:flex; justify-content:space-between; }.device-type { color:#548276; font-size:11px; text-transform:uppercase; }.device-top h3 { margin:6px 0 4px; color:#284c43; }.device-top small,.muted { color:#83918d; font-size:11px; }.control-state { margin:20px 0; padding:12px; border-radius:9px; background:#f3f8f5; }.control-state span,.control-state small { display:block; color:#72847e; font-size:11px; }.control-state b { display:block; margin:5px 0; color:#287963; font-size:19px; }.device-actions { display:flex; flex-wrap:wrap; gap:8px; }.command-log { margin-top:18px; padding:18px; }.command-log .data-title { margin-bottom:5px; }</style>
