<script setup lang="ts">
import { computed } from 'vue'
import { statusType } from '@/utils/format'

const props = defineProps<{ status?: string }>()
const type = computed(() => statusType(props.status))
</script>

<template>
  <el-tag size="small" effect="light" :type="type">{{ status || '未知' }}</el-tag>
</template>
