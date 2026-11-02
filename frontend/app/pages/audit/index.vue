<script setup lang="ts">
const api = useApi()
const f = ref({ action: '', resourceType: '', username: '', from: '', to: '' }); const page = ref(1); const size = 30
const list = ref<any>({ items: [], total: 0 }); const open = ref<any>(null)
async function load() {
  list.value = await api.get('/audit-logs', { action: f.value.action || undefined, resourceType: f.value.resourceType || undefined, username: f.value.username || undefined,
    from: f.value.from ? new Date(f.value.from).toISOString() : undefined, to: f.value.to ? new Date(f.value.to).toISOString() : undefined, page: page.value, pageSize: size })
}
onMounted(load); watch(page, load); watch(f, () => { page.value = 1; load() }, { deep: true })
</script>
<template>
  <div>
    <CommonPageHeader title="Audit Log" icon="shield-check" subtitle="Aktivitas user. Password dan token tidak pernah direkam." />
    <div class="card mb-3"><div class="card-body d-flex gap-2 flex-wrap">
      <input v-model.lazy="f.username" class="form-control form-control-sm" style="width: 160px" placeholder="Username">
      <input v-model.lazy="f.action" class="form-control form-control-sm" style="width: 200px" placeholder="Aksi (mis. LOGIN)">
      <input v-model.lazy="f.resourceType" class="form-control form-control-sm" style="width: 180px" placeholder="Resource (mis. SyncGroup)">
      <input v-model="f.from" type="datetime-local" class="form-control form-control-sm" style="width: 200px"><input v-model="f.to" type="datetime-local" class="form-control form-control-sm" style="width: 200px">
    </div></div>
    <div class="card"><div class="table-responsive"><table class="table table-sm table-hover align-middle mb-0">
      <thead><tr><th>Waktu</th><th>User</th><th>Aksi</th><th>Resource</th><th>ID</th><th>IP</th><th></th></tr></thead>
      <tbody>
        <tr v-for="a in list.items" :key="a.id"><td class="small">{{ fmtDate(a.createdAt) }}</td><td>{{ a.username }}</td><td><span class="badge text-bg-light border">{{ a.action }}</span></td><td>{{ a.resourceType }}</td><td class="mono small">{{ a.resourceId?.slice(0, 8) }}</td><td class="small">{{ a.ipAddress }}</td>
          <td><button v-if="a.beforeValue || a.afterValue" class="btn btn-sm btn-link" @click="open = a">detail</button></td></tr>
        <tr v-if="!list.items.length"><td colspan="7" class="text-center text-muted py-4">Tidak ada data.</td></tr>
      </tbody></table></div><div class="px-3 pb-2"><CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div></div>
    <div v-if="open" class="modal-backdrop-custom" @click.self="open = null"><div class="modal-panel wide">
      <div class="p-3 border-bottom fw-semibold">{{ open.action }} · {{ open.resourceType }}</div>
      <div class="p-3 row g-2"><div class="col-md-6"><div class="small text-muted">Sebelum</div><pre class="json">{{ JSON.stringify(open.beforeValue, null, 2) }}</pre></div>
        <div class="col-md-6"><div class="small text-muted">Sesudah</div><pre class="json">{{ JSON.stringify(open.afterValue, null, 2) }}</pre></div></div>
      <div class="p-3 border-top text-end"><button class="btn btn-light" @click="open = null">Tutup</button></div></div></div>
  </div>
</template>
