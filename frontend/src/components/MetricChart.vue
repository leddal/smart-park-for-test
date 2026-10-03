<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts'

interface Point { time: string; value: number }
const props = withDefaults(defineProps<{ points: Point[]; unit?: string; color?: string }>(), { unit: '', color: '#1a8d7c' })
const host = ref<HTMLDivElement>()
let chart: echarts.ECharts | undefined
let observer: ResizeObserver | undefined
function render(): void {
  if (!chart) return
  chart.setOption({
    grid: { left: 45, right: 18, top: 22, bottom: 30 },
    tooltip: { trigger: 'axis', valueFormatter: (value: number) => `${value} ${props.unit}` },
    xAxis: { type: 'category', boundaryGap: false, data: props.points.map((point) => new Date(point.time).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', timeZone: 'Asia/Shanghai' })), axisLine: { lineStyle: { color: '#d6e2dd' } }, axisLabel: { color: '#70817c' } },
    yAxis: { type: 'value', splitLine: { lineStyle: { color: '#edf2ef' } }, axisLabel: { color: '#70817c' } },
    series: [{ type: 'line', smooth: true, showSymbol: false, data: props.points.map((point) => point.value), lineStyle: { color: props.color, width: 3 }, areaStyle: { color: `${props.color}22` } }],
  }, true)
}
onMounted(() => { if (host.value) { chart = echarts.init(host.value); observer = new ResizeObserver(() => chart?.resize()); observer.observe(host.value); render() } })
watch(() => [props.points, props.unit, props.color], render, { deep: true })
onBeforeUnmount(() => { observer?.disconnect(); chart?.dispose() })
</script>
<template><div ref="host" class="metric-chart" aria-label="指标趋势图" /></template>
<style scoped>.metric-chart { width:100%; height:245px; }</style>
