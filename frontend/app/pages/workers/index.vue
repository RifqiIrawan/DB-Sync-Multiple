<script setup lang="ts">
const api = useApi(); const list = ref<any[]>([])
async function load() { list.value = await api.get('/monitoring/workers', undefined, true) }
usePolling(load, 4000)
</script>
<template>
  <div>
    <CommonPageHeader title="Worker Status" subtitle="Heartbeat setiap 10 detik; worker dianggap offline bila tidak ada heartbeat >30 detik" />
    <div class="card"><div class="table-responsive"><table class="table table-sm align-middle mb-0">
      <thead><tr><th>Worker</th><th>Tipe</th><th>Host</th><th>Status</th><th>Heartbeat terakhir</th><th>Info</th></tr></thead>
      <tbody>
        <tr v-for="w in list" :key="w.workerId">
          <td class="mono">{{ w.workerId }}</td><td>{{ w.workerType }}</td><td>{{ w.hostname }}</td>
          <td><CommonStatusBadge :status="w.online ? 'ACTIVE' : (w.status === 'RUNNING' ? 'STALE' : w.status)" /></td><td class="small">{{ fmtDate(w.lastHeartbeatAt) }}</td>
          <td class="small mono">{{ w.metadata ? JSON.stringify(w.metadata) : '' }}</td>
        </tr>
        <tr v-if="!list.length"><td colspan="6" class="text-center text-muted py-4">Belum ada worker. Jalankan <code>dotnet run --project backend/src/DBSync.Worker</code> atau set <code>Worker:Embedded=true</code>.</td></tr>
      </tbody></table></div></div>
  </div>
</template>
