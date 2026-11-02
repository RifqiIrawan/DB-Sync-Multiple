<script setup lang="ts">
defineProps<{ title: string; subtitle?: string; icon?: string }>()
</script>
<template>
  <div class="d-flex justify-content-between align-items-end mb-4 flex-wrap gap-3 rise-head">
    <div class="d-flex align-items-center gap-3">
      <div v-if="icon" class="brand-mark" style="width: 46px; height: 46px; border-radius: 14px; font-size: 1.3rem"><i :class="`bi bi-${icon}`"></i></div>
      <div>
        <h2 class="mb-0" style="font-size: 1.7rem">{{ title }}</h2>
        <div v-if="subtitle" class="text-muted">{{ subtitle }}</div>
      </div>
    </div>
    <div class="d-flex gap-2 align-items-center flex-wrap"><slot /></div>
  </div>
</template>
