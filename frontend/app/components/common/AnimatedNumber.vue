<script setup lang="ts">
// Angka yang berhitung halus ke nilai baru (menghidupkan dashboard saat data live berubah).
const props = defineProps<{ value: number | string | null | undefined }>()
const shown = ref(0)
let raf = 0
function animate(to: number) {
  cancelAnimationFrame(raf)
  const from = shown.value, start = performance.now(), dur = 650
  const step = (t: number) => {
    const p = Math.min(1, (t - start) / dur), e = 1 - Math.pow(1 - p, 3)
    shown.value = Math.round(from + (to - from) * e)
    if (p < 1) raf = requestAnimationFrame(step)
  }
  raf = requestAnimationFrame(step)
}
const num = computed(() => (typeof props.value === 'number' ? props.value : NaN))
watch(num, v => { if (!Number.isNaN(v)) animate(v) }, { immediate: true })
onBeforeUnmount(() => cancelAnimationFrame(raf))
</script>
<template>
  <span v-if="Number.isNaN(num)">{{ value ?? '-' }}</span>
  <span v-else>{{ shown.toLocaleString('id-ID') }}</span>
</template>
