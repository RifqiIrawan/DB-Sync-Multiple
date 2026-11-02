<script setup lang="ts">
withDefaults(defineProps<{ rows?: number; height?: string }>(), { rows: 4, height: '1.1rem' })
</script>
<template>
  <div class="p-3 d-grid gap-3">
    <div v-for="i in rows" :key="i" class="skel" :style="{ height, width: (60 + ((i * 17) % 40)) + '%' }"></div>
  </div>
</template>
