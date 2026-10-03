<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as L from 'leaflet'

export interface MapLayer { id: string; name: string; kind: string; geoJson?: GeoJSON.GeoJsonObject | string; imageUrl?: string; bounds?: [[number, number], [number, number]] | { southWest?: { lat?: number; lng?: number }; northEast?: { lat?: number; lng?: number } } | string }
export interface MapAsset { id: string; name: string; category: string; longitude: number; latitude: number; publicCode?: string }
const props = withDefaults(defineProps<{ layers?: MapLayer[]; assets?: MapAsset[]; routePoints?: [number, number][]; center?: [number, number] }>(), { layers: () => [], assets: () => [], routePoints: () => [], center: () => [31.19, 121.43] })
const emit = defineEmits<{ assetClick: [asset: MapAsset] }>()
const host = ref<HTMLDivElement>()
const visibleLayers = ref<Record<string, boolean>>({})
let map: L.Map | undefined
let layerGroup: L.LayerGroup | undefined

function parseGeoJson(value?: GeoJSON.GeoJsonObject | string): GeoJSON.GeoJsonObject | undefined {
  if (!value) return undefined
  try { return typeof value === 'string' ? JSON.parse(value) as GeoJSON.GeoJsonObject : value } catch { return undefined }
}
function parseBounds(value?: MapLayer['bounds']): [[number, number], [number, number]] | undefined {
  if (!value) return undefined
  try {
    const source = (typeof value === 'string' ? JSON.parse(value) : value) as unknown
    if (Array.isArray(source) && source.length === 2 && source.every((point) => Array.isArray(point) && point.length === 2)) {
      return [[Number(source[0][1]), Number(source[0][0])], [Number(source[1][1]), Number(source[1][0])]]
    }
    if (source && typeof source === 'object' && 'southWest' in source && 'northEast' in source) {
      const { southWest, northEast } = source as { southWest?: { lat?: number; lng?: number }; northEast?: { lat?: number; lng?: number } }
      if ([southWest?.lat, southWest?.lng, northEast?.lat, northEast?.lng].every(Number.isFinite)) return [[southWest!.lat!, southWest!.lng!], [northEast!.lat!, northEast!.lng!]]
    }
  } catch { /* malformed layer bounds are ignored */ }
  return undefined
}
function draw(): void {
  if (!map || !layerGroup) return
  layerGroup.clearLayers()
  props.layers.forEach((layer) => {
    if (visibleLayers.value[layer.id] === undefined) visibleLayers.value[layer.id] = true
    if (!visibleLayers.value[layer.id]) return
    const geoJson = parseGeoJson(layer.geoJson)
    const bounds = parseBounds(layer.bounds)
    if (geoJson) L.geoJSON(geoJson, { style: { color: '#168071', weight: 2, fillOpacity: .12 } }).addTo(layerGroup)
    if (layer.imageUrl && bounds) L.imageOverlay(layer.imageUrl, bounds, { opacity: .72 }).addTo(layerGroup)
  })
  if (props.routePoints.length > 1) L.polyline(props.routePoints, { color: '#286bd8', weight: 4, opacity: .9 }).addTo(layerGroup)
  props.assets.forEach((asset) => {
    if (!Number.isFinite(asset.latitude) || !Number.isFinite(asset.longitude)) return
    const marker = L.circleMarker([asset.latitude, asset.longitude], { radius: 7, color: '#fff', weight: 2, fillColor: asset.category === 'Plant' ? '#78a948' : '#d28d31', fillOpacity: 1 })
    const label = document.createElement('span')
    label.textContent = `${asset.name} · ${asset.category}`
    marker.bindTooltip(label, { direction: 'top' }).on('click', () => emit('assetClick', asset)).addTo(layerGroup)
  })
}
function toggleLayer(id: string): void { visibleLayers.value[id] = !visibleLayers.value[id]; draw() }
onMounted(() => { if (host.value) { map = L.map(host.value, { zoomControl: true, attributionControl: false }).setView(props.center, 16); layerGroup = L.layerGroup().addTo(map); draw(); setTimeout(() => map?.invalidateSize(), 50) } })
watch(() => [props.layers, props.assets, props.routePoints], draw, { deep: true })
onBeforeUnmount(() => map?.remove())
</script>
<template><div ref="host" class="park-map"><div class="layer-controls"><button v-for="layer in layers" :key="layer.id" type="button" :class="{ active: visibleLayers[layer.id] !== false }" @click="toggleLayer(layer.id)"><i />{{ layer.name }}</button></div><div class="legend"><span><i class="plant" />植物</span><span><i class="asset" />设施/设备</span><span v-if="routePoints.length"><i class="route" />园路路线</span></div><span class="map-note">离线本地底图</span></div></template>
<style scoped>.park-map { position:relative; height:100%; min-height:300px; border-radius:12px; overflow:hidden; background:linear-gradient(135deg,#e7f1e3,#d8ebe1); }.layer-controls { position:absolute; z-index:500; top:11px; right:11px; display:grid; gap:5px; }.layer-controls button { border:1px solid #d8e5df; border-radius:5px; padding:5px 8px; color:#74847f; background:rgba(255,255,255,.93); font-size:11px; cursor:pointer; }.layer-controls button.active { color:#1d6858; border-color:#91c6b3; }.layer-controls i,.legend i { display:inline-block; width:7px; height:7px; margin-right:5px; border-radius:50%; background:#a8b8b3; }.layer-controls button.active i { background:#178170; }.legend { position:absolute; z-index:500; left:12px; top:12px; display:flex; gap:9px; padding:5px 8px; border-radius:5px; color:#50655f; background:rgba(255,255,255,.9); font-size:10px; }.legend .plant { background:#78a948; }.legend .asset { background:#d28d31; }.legend .route { width:10px; height:3px; border-radius:0; background:#286bd8; }.map-note { position:absolute; z-index:500; left:12px; bottom:12px; padding:4px 8px; border-radius:5px; font-size:11px; background:rgba(255,255,255,.9); color:#50655f; }</style>
