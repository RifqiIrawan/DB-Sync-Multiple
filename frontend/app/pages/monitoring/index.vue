<script setup lang="ts">
const { data: d, live } = useMonitor()
</script>
<template>
  <div>
    <CommonPageHeader title="Live Monitoring" icon="activity" subtitle="Antrean event, job berjalan, dan kesehatan worker (real-time)">
      <span class="badge" :class="live ? 'text-bg-success' : 'text-bg-secondary'"><i class="bi bi-broadcast"></i> {{ live ? 'Live (SignalR)' : 'Polling' }}</span>
    </CommonPageHeader>
    <div v-if="!d" class="text-muted">Memuat...</div>
    <template v-else>
      <div class="row g-3 mb-3">
        <div class="col-6 col-md-3 col-xl-2" v-for="x in [['Success', d.events.processed, 'success'], ['Success 24 jam', d.events.processed24h, 'success'], ['Pending', d.events.pending, 'warning'], ['Retry', d.events.retry, 'warning'], ['Failed', d.events.failed, 'danger'], ['Dead letter', d.events.deadLetter, 'danger'], ['Conflict', d.pendingConflicts, 'danger']]" :key="x[0] as string">
          <div class="card"><div class="card-body py-2"><div class="stat" :class="`text-${x[2]}`">{{ fmtNum(x[1] as number) }}</div><div class="stat-label">{{ x[0] }}</div></div></div></div>
      </div>
      <div class="row g-3">
        <div class="col-lg-7"><div class="card"><div class="card-header">Job aktif</div><div class="card-body">
          <div v-if="!d.runningRuns.length" class="text-muted">Tidak ada job aktif.</div>
          <div v-for="r in d.runningRuns" :key="r.id" class="mb-3">
            <div class="d-flex justify-content-between"><NuxtLink :to="`/sync-groups/${r.syncGroupId}`"><strong>{{ r.groupName }}</strong></NuxtLink><span><span class="badge text-bg-light border me-1">{{ r.runType }}</span><CommonStatusBadge :status="r.status" /></span></div>
            <div v-for="(i, k) in r.items" :key="k" class="mt-1">
              <div class="d-flex justify-content-between small"><span class="mono">{{ i.tableLabel }}</span><span>{{ fmtNum(i.recordsRead) }}<template v-if="i.totalRows && r.runType === 'INITIAL'"> / {{ fmtNum(i.totalRows) }}</template> · gagal {{ i.recordsFailed }} · {{ i.status }}</span></div>
              <div class="progress" style="height: 5px" v-if="i.totalRows && r.runType === 'INITIAL'"><div class="progress-bar progress-bar-striped progress-bar-animated" :style="{ width: Math.min(100, 100 * i.recordsRead / i.totalRows) + '%' }"></div></div>
            </div>
          </div></div></div></div>
        <div class="col-lg-5"><div class="card"><div class="card-header">Worker</div><ul class="list-group list-group-flush">
          <li v-for="w in d.workers" :key="w.workerId" class="list-group-item d-flex justify-content-between"><span><i class="bi bi-circle-fill me-2" :class="w.online ? 'text-success' : 'text-secondary'" style="font-size: .6rem"></i>{{ w.workerType }} · {{ w.hostname }}</span><span class="small text-muted">{{ fmtDate(w.lastHeartbeatAt) }}</span></li>
          <li v-if="!d.workers.length" class="list-group-item text-muted">Tidak ada worker.</li></ul></div></div>
      </div>
    </template>
  </div>
</template>
