<script setup lang="ts">
import { computed } from 'vue'
import type { Source } from '@/types/domain'
import { sourceLabel } from '@/utils/format'

const props = defineProps<{ source?: Source }>()
const label = computed(() => props.source ? sourceLabel[props.source] : '来源未知')
</script>

<template>
  <span class="source-badge" :class="source?.toLowerCase()">{{ label }}</span>
</template>

<style scoped>
.source-badge { display:inline-flex; border-radius:99px; padding:3px 8px; font-size:11px; line-height:1; color:#55706a; background:#edf3f1; }
.source-badge.simulation { color:#9a6514; background:#fff4df; }
.source-badge.manual { color:#286b86; background:#e4f3f9; }
</style>
