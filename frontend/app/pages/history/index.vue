<script setup lang="ts">
const api = useApi()
const f = ref({ groupId: '', status: '', runType: '' }); const groups = ref<any[]>([]); const page = ref(1); const size = 20
const list = ref<any>({ items: [], total: 0 })
async function load() { list.value = await api.get('/sync-runs', { groupId: f.value.groupId || undefined, status: f.value.status || undefined, runType: f.value.runType || undefined, page: page.value, pageSize: size }) }
onMounted(async () => { groups.value = await api.get('/sync-groups'); load() })
watch(page, load); watch(f, () => { page.value = 1; load() }, { deep: true }); usePolling(load, 8000)
</script>
<template>
  <div>
    <CommonPageHeader title="Riwayat Sinkronisasi" icon="journal-text" subtitle="Riwayat eksekusi sinkronisasi" />
    <div class="card mb-3"><div class="card-body d-flex gap-2 flex-wrap">
      <select v-model="f.groupId" class="form-select form-select-sm" style="width: 220px"><option value="">Semua group</option><option v-for="g in groups" :key="g.id" :value="g.id">{{ g.groupName }}</option></select>
      <select v-model="f.status" class="form-select form-select-sm" style="width: 150px"><option value="">Semua status</option><option v-for="s in ['QUEUED', 'RUNNING', 'SUCCESS', 'PARTIAL', 'FAILED', 'CANCELLED']" :key="s">{{ s }}</option></select>
      <select v-model="f.runType" class="form-select form-select-sm" style="width: 150px"><option value="">Semua tipe</option><option>INITIAL</option><option>SCHEDULED</option><option>MANUAL</option></select>
    </div></div>
    <div class="card"><div class="table-responsive"><table class="table table-sm table-hover align-middle mb-0">
      <thead><tr><th>Mulai</th><th>Group</th><th>Tipe</th><th>Status</th><th>Durasi</th><th class="text-end">Dibaca</th><th class="text-end">Ins</th><th class="text-end">Upd</th><th class="text-end">Del</th><th class="text-end">Gagal</th><th class="text-end">Konflik</th></tr></thead>
      <tbody>
        <tr v-for="r in list.items" :key="r.id">
          <td class="small"><NuxtLink :to="`/history/${r.id}`">{{ fmtDate(r.startedAt ?? r.queuedAt) }}</NuxtLink></td><td>{{ r.groupName }}</td><td>{{ r.runType }}</td><td><CommonStatusBadge :status="r.status" /></td>
          <td class="small">{{ fmtDuration(r.startedAt, r.finishedAt) }}</td>
          <td class="text-end">{{ fmtNum(r.recordsRead) }}</td><td class="text-end">{{ fmtNum(r.recordsInserted) }}</td><td class="text-end">{{ fmtNum(r.recordsUpdated) }}</td>
          <td class="text-end">{{ fmtNum(r.recordsDeleted) }}</td><td class="text-end" :class="{ 'text-danger': r.recordsFailed }">{{ fmtNum(r.recordsFailed) }}</td><td class="text-end">{{ r.conflictsDetected }}</td>
        </tr>
        <tr v-if="!list.items.length"><td colspan="11"><CommonEmptyState icon="journal-text" title="Belum ada riwayat" text="Setiap eksekusi sinkronisasi akan tercatat di sini." /></td></tr>
      </tbody></table></div><div class="px-3 pb-2"><CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div></div>
  </div>
</template>
