<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import ProductionHint from '@/components/ProductionHint.vue'
import { productionHints, type ProductionTopic } from '@/content/productionHints'
const route = useRoute(); const topic = computed<ProductionTopic>(() => route.params.topic in productionHints ? route.params.topic as ProductionTopic : 'desktop')
</script>
<template><div class="page help"><div class="page-heading"><div><h1>生产要求与降级边界</h1><p>本页内容来自受控静态文本，用于说明当前演示与正常生产所需条件。</p></div></div><section class="surface help-card"><ProductionHint :topic="topic" /><el-divider>其他主题</el-divider><div class="topic-links"><router-link v-for="(hint,key) in productionHints" :key="key" :to="`/admin/help/production/${key}`">{{ hint.title }}</router-link></div></section></div></template>
<style scoped>.help-card { padding:22px; }.topic-links { display:flex; flex-wrap:wrap; gap:10px; }.topic-links a { padding:8px 12px; border:1px solid #dce8e3; border-radius:7px; color:#397464; font-size:13px; }</style>
