<script setup lang="ts">
const props = defineProps<{ page: number; pageSize: number; total: number }>()
const emit = defineEmits<{ (e: 'update:page', v: number): void }>()
const pages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
</script>
<template>
  <div class="d-flex justify-content-between align-items-center mt-2 small text-muted" v-if="total > 0">
    <span>{{ total.toLocaleString('id-ID') }} data · halaman {{ page }} / {{ pages }}</span>
    <div class="btn-group btn-group-sm">
      <button class="btn btn-outline-secondary" :disabled="page <= 1" @click="emit('update:page', page - 1)">‹ Prev</button>
      <button class="btn btn-outline-secondary" :disabled="page >= pages" @click="emit('update:page', page + 1)">Next ›</button>
    </div>
  </div>
</template>
