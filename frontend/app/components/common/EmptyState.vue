<script setup lang="ts">
defineProps<{ icon?: string; title: string; text?: string }>()
</script>
<template>
  <div class="empty">
    <div class="ico"><i :class="`bi bi-${icon ?? 'inbox'}`"></i></div>
    <h5 class="mb-1">{{ title }}</h5>
    <p class="text-muted mx-auto mb-3" style="max-width: 420px">{{ text }}</p>
    <slot />
  </div>
</template>
