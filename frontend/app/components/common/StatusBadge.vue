<script setup lang="ts">
// Status berupa pil + titik; status "hidup" (RUNNING dll.) berdenyut. Label dibuat ramah-pengguna.
const props = defineProps<{ status?: string | null }>()
const map: Record<string, [string, string, boolean?]> = {
  ACTIVE: ['s-ok', 'Aktif'], SUCCESS: ['s-ok', 'Sukses'], CONNECTED: ['s-ok', 'Terhubung'], VALID: ['s-ok', 'Valid'], COMPLETED: ['s-ok', 'Selesai'], OK: ['s-ok', 'OK'], RESOLVED: ['s-ok', 'Selesai'],
  RUNNING: ['s-info', 'Berjalan', true], QUEUED: ['s-warn', 'Antre', true], CANCELLING: ['s-warn', 'Membatalkan', true], PROCESSING: ['s-info', 'Diproses', true],
  PAUSED: ['s-warn', 'Dijeda'], WARNING: ['s-warn', 'Peringatan'], RETRY: ['s-warn', 'Coba ulang'], PARTIAL: ['s-warn', 'Sebagian'], PENDING: ['s-warn', 'Menunggu'], VALIDATING: ['s-warn', 'Memvalidasi', true],
  COMPLETED_WITH_ERRORS: ['s-warn', 'Selesai (ada error)'], STALE: ['s-warn', 'Tidak responsif'], SELECTED: ['s-info', 'Dipilih'],
  ERROR: ['s-bad', 'Error'], FAILED: ['s-bad', 'Gagal'], DISCONNECTED: ['s-bad', 'Terputus'], INVALID: ['s-bad', 'Tidak valid'], DEAD_LETTER: ['s-bad', 'Dead letter'], CONFLICT: ['s-bad', 'Konflik'],
  STOPPED: ['s-mute', 'Berhenti'], CANCELLED: ['s-mute', 'Dibatalkan'], SKIPPED: ['s-mute', 'Dilewati'], DRAFT: ['s-mute', 'Draft'], UNKNOWN: ['s-mute', 'Belum diuji'], UNVALIDATED: ['s-mute', 'Belum divalidasi'],
}
const info = computed(() => map[(props.status ?? '').toUpperCase()] ?? ['s-mute', props.status ?? '-'])
</script>
<template>
  <span class="status" :class="[info[0], info[2] ? 'live' : '']"><span class="dot"></span>{{ info[1] }}</span>
</template>
