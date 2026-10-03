<script setup lang="ts">
defineProps<{ loading?: boolean; error?: string; empty?: boolean; emptyText?: string }>()
const emit = defineEmits<{ retry: [] }>()
</script>

<template>
  <div v-if="loading || error || empty" class="state-block">
    <el-skeleton v-if="loading" :rows="4" animated style="width:55%" />
    <el-result v-else-if="error" icon="error" title="加载失败" :sub-title="error"><template #extra><el-button @click="emit('retry')">重新加载</el-button></template></el-result>
    <el-empty v-else :description="emptyText || '暂无数据'" />
  </div>
  <slot v-else />
</template>
