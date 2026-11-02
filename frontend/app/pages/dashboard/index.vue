<script setup lang="ts">
const { data: d, live } = useMonitor()
const cards = computed(() => !d.value ? [] : [
  { label: 'Total Connections', v: d.value.connections.total, icon: 'hdd-network', cls: 'text-primary' },
  { label: 'Connected / Disconnected', v: `${d.value.connections.connected} / ${d.value.connections.disconnected}`, icon: 'plug', cls: 'text-success' },
  { label: 'Active Groups', v: `${d.value.groups.active} / ${d.value.groups.total}`, icon: 'diagram-2', cls: 'text-primary' },
  { label: 'Scheduled Jobs', v: d.value.groups.scheduled, icon: 'clock-history', cls: 'text-info' },
  { label: 'CDC Groups', v: d.value.groups.cdc, icon: 'lightning-charge', cls: 'text-warning' },
  { label: 'Events Processed', v: fmtNum(d.value.events.processed), icon: 'check2-circle', cls: 'text-success' },
  { label: 'Pending Events', v: fmtNum(d.value.events.pending + d.value.events.retry), icon: 'hourglass-split', cls: 'text-warning' },
  { label: 'Failed / Dead Letter', v: `${d.value.events.failed} / ${d.value.events.deadLetter}`, icon: 'x-octagon', cls: 'text-danger' },
  { label: 'Pending Conflicts', v: d.value.pendingConflicts, icon: 'exclamation-diamond', cls: 'text-danger' },
])
</script>
<template>
  <div>
    <CommonPageHeader title="Dashboard" subtitle="Ringkasan status seluruh synchronization group">
      <span class="badge" :class="live ? 'text-bg-success' : 'text-bg-secondary'"><i class="bi bi-broadcast"></i> {{ live ? 'Live (SignalR)' : 'Polling' }}</span>
    </CommonPageHeader>
    <div v-if="!d" class="text-muted">Memuat...</div>
    <template v-else>
      <div class="row g-3 mb-3">
        <div class="col-6 col-lg-4 col-xl-3" v-for="c in cards" :key="c.label">
          <div class="card h-100"><div class="card-body d-flex justify-content-between align-items-center">
            <div><div class="stat">{{ c.v }}</div><div class="stat-label">{{ c.label }}</div></div>
            <i :class="`bi bi-${c.icon} fs-1 ${c.cls}`" style="opacity: .7"></i>
          </div></div>
        </div>
      </div>
      <div class="row g-3">
        <div class="col-lg-7">
          <div class="card mb-3" v-if="d.runningRuns.length">
            <div class="card-header">Job berjalan</div>
            <div class="card-body">
              <div v-for="r in d.runningRuns" :key="r.id" class="mb-3">
                <div class="d-flex justify-content-between"><strong>{{ r.groupName }}</strong><span><span class="badge text-bg-light border me-1">{{ r.runType }}</span><CommonStatusBadge :status="r.status" /></span></div>
                <div v-for="(i, k) in r.items" :key="k" class="small mt-1">
                  <div class="d-flex justify-content-between"><span class="mono">{{ i.tableLabel }}</span><span>{{ fmtNum(i.recordsRead) }}<template v-if="i.totalRows"> / {{ fmtNum(i.totalRows) }}</template> baris · <CommonStatusBadge :status="i.status" /></span></div>
                  <div class="progress" style="height: 5px" v-if="i.totalRows && r.runType === 'INITIAL'"><div class="progress-bar" :style="{ width: Math.min(100, 100 * i.recordsRead / i.totalRows) + '%' }"></div></div>
                </div>
              </div>
            </div>
          </div>
          <div class="card">
            <div class="card-header">Aktivitas sinkronisasi terbaru</div>
            <div class="table-responsive"><table class="table table-sm mb-0 align-middle">
              <thead><tr><th>Group</th><th>Tipe</th><th>Status</th><th>Mulai</th><th class="text-end">Baca</th><th class="text-end">Gagal</th></tr></thead>
              <tbody>
                <tr v-for="r in d.recentRuns" :key="r.id">
                  <td><NuxtLink :to="`/history/${r.id}`">{{ r.groupName }}</NuxtLink></td><td>{{ r.runType }}</td>
                  <td><CommonStatusBadge :status="r.status" /></td><td class="small">{{ fmtDate(r.startedAt ?? r.queuedAt) }}</td>
                  <td class="text-end">{{ fmtNum(r.recordsRead) }}</td><td class="text-end">{{ fmtNum(r.recordsFailed) }}</td>
                </tr>
                <tr v-if="!d.recentRuns.length"><td colspan="6" class="text-center text-muted py-3">Belum ada aktivitas.</td></tr>
              </tbody>
            </table></div>
          </div>
        </div>
        <div class="col-lg-5">
          <div class="card">
            <div class="card-header">Worker health</div>
            <ul class="list-group list-group-flush">
              <li v-for="w in d.workers" :key="w.workerId" class="list-group-item d-flex justify-content-between align-items-center">
                <div><i class="bi bi-circle-fill me-2" :class="w.online ? 'text-success' : 'text-secondary'" style="font-size: .6rem"></i>
                  <strong>{{ w.workerType }}</strong> <span class="text-muted small">{{ w.hostname }}</span>
                  <div class="small text-muted">heartbeat {{ fmtDate(w.lastHeartbeatAt) }}</div></div>
                <CommonStatusBadge :status="w.online ? 'ACTIVE' : (w.status === 'RUNNING' ? 'STALE' : w.status)" />
              </li>
              <li v-if="!d.workers.length" class="list-group-item text-muted">Belum ada worker terdaftar. Jalankan DBSync.Worker (atau Worker:Embedded=true pada API).</li>
            </ul>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>
