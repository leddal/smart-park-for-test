<script setup lang="ts">
import { computed } from 'vue'
import { productionHints, type ProductionTopic } from '@/content/productionHints'

const props = withDefaults(defineProps<{ topic: ProductionTopic; compact?: boolean }>(), { compact: false })
const hint = computed(() => productionHints[props.topic])
</script>

<template>
  <el-alert class="production-hint" type="info" :closable="false" show-icon>
    <template #title><strong>{{ hint.title }}</strong><span v-if="compact"> · {{ hint.current }}</span></template>
    <template v-if="!compact" #default>
      <dl>
        <div><dt>当前实现</dt><dd>{{ hint.current }}</dd></div>
        <div><dt>降级原因</dt><dd>{{ hint.reason }}</dd></div>
        <div><dt>正常生产要求</dt><dd>{{ hint.production }}</dd></div>
        <div><dt>本版支持</dt><dd>{{ hint.supported }}</dd></div>
      </dl>
      <router-link :to="`/admin/help/production/${topic}`">查看生产要求详情</router-link>
    </template>
  </el-alert>
</template>

<style scoped>
.production-hint { margin-top:16px; align-items:flex-start; }
dl { margin:10px 0; display:grid; gap:6px; } div { display:grid; grid-template-columns:80px 1fr; gap:6px; } dt { color:#536b65; font-weight:650; } dd { margin:0; color:#536b65; line-height:1.55; }
</style>
