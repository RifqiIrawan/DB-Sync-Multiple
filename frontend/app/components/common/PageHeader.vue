<script setup lang="ts">
defineProps<{ title: string; subtitle?: string }>()
</script>
<template>
  <div class="d-flex justify-content-between align-items-start mb-3 flex-wrap gap-2">
    <div>
      <h4 class="mb-0">{{ title }}</h4>
      <div v-if="subtitle" class="text-muted small">{{ subtitle }}</div>
    </div>
    <div class="d-flex gap-2 align-items-center"><slot /></div>
  </div>
</template>
