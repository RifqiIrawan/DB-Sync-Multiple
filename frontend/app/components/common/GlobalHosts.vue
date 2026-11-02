<script setup lang="ts">
const { toasts, dismiss } = useToast()
const { state, answer } = useConfirm()
</script>
<template>
  <div class="toast-host">
    <div v-for="t in toasts" :key="t.id" class="alert shadow-sm mb-2 py-2" :class="`alert-${t.type}`" role="alert">
      <div class="d-flex justify-content-between">
        <strong>{{ t.title }}</strong>
        <button type="button" class="btn-close btn-sm" @click="dismiss(t.id)"></button>
      </div>
      <div v-if="t.text" class="small" style="white-space: pre-line">{{ t.text }}</div>
    </div>
  </div>
  <div v-if="state.open" class="modal-backdrop-custom" @click.self="answer(false)">
    <div class="modal-panel" style="max-width: 460px">
      <div class="p-3 border-bottom fw-semibold">{{ state.title }}</div>
      <div class="p-3" style="white-space: pre-line">{{ state.text }}</div>
      <div class="p-3 border-top d-flex justify-content-end gap-2">
        <button class="btn btn-light" @click="answer(false)">Batal</button>
        <button class="btn" :class="state.danger ? 'btn-danger' : 'btn-primary'" @click="answer(true)">Ya, lanjutkan</button>
      </div>
    </div>
  </div>
</template>
