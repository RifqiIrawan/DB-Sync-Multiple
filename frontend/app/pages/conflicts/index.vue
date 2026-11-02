<script setup lang="ts">
const api = useApi(); const auth = useAuth(); const toast = useToast(); const confirm = useConfirm()
const status = ref('PENDING'); const page = ref(1); const size = 20
const list = ref<any>({ items: [], total: 0 }); const open = ref<any>(null)
async function load() { list.value = await api.get('/monitoring/conflicts', { status: status.value, page: page.value, pageSize: size }) }
watch([status, page], load); usePolling(load, 6000)

const cols = computed(() => open.value ? [...new Set([...Object.keys(open.value.endpointAValue ?? {}), ...Object.keys(open.value.endpointBValue ?? {})])] : [])
const differs = (c: string) => JSON.stringify(open.value.endpointAValue?.[c]) !== JSON.stringify(open.value.endpointBValue?.[c])
async function resolve(winner: 'A' | 'B') {
  const c = open.value
  const lose = winner === 'A' ? 'B' : 'A'
  const delTxt = (winner === 'A' ? c.endpointAValue : c.endpointBValue) == null ? `\nPerhatian: sisi ${winner} sudah TIDAK memiliki baris ini → baris di ${lose} akan DIHAPUS.` : ''
  if (!(await confirm.ask(`Pilih versi Endpoint ${winner}?`, `Versi ${winner} akan disebarkan ke Endpoint ${lose}.${delTxt}`, !!delTxt))) return
  await api.post(`/monitoring/conflicts/${c.id}/resolve`, { winner }); toast.success('Konflik diselesaikan'); open.value = null; await load()
}
const fmt = (v: any) => v === null || v === undefined ? '∅' : typeof v === 'object' ? JSON.stringify(v) : String(v)
</script>
<template>
  <div>
    <CommonPageHeader title="Conflict Management" subtitle="Perubahan bersamaan pada kedua endpoint. Pilih versi pemenang untuk disebarkan.">
      <select v-model="status" class="form-select form-select-sm" style="width: 160px"><option value="PENDING">Menunggu</option><option value="RESOLVED">Selesai</option></select>
    </CommonPageHeader>
    <div class="card"><div class="table-responsive"><table class="table table-hover table-sm align-middle mb-0">
      <thead><tr><th>Waktu</th><th>Group</th><th>Tabel</th><th>Primary key</th><th>Jenis</th><th>Status</th><th></th></tr></thead>
      <tbody>
        <tr v-for="c in list.items" :key="c.id">
          <td class="small">{{ fmtDate(c.createdAt) }}</td><td>{{ c.groupName }}</td><td>{{ c.table }}</td><td class="mono">{{ JSON.stringify(c.primaryKey) }}</td>
          <td><span class="badge text-bg-light border">{{ c.conflictType }}</span></td>
          <td><CommonStatusBadge :status="c.status" /><div class="small text-muted" v-if="c.resolvedByName">{{ c.resolutionStrategy }} oleh {{ c.resolvedByName }}</div></td>
          <td class="text-end"><button class="btn btn-sm btn-outline-primary" @click="open = c">{{ c.status === 'PENDING' ? 'Tinjau' : 'Detail' }}</button></td>
        </tr>
        <tr v-if="!list.items.length"><td colspan="7" class="text-center text-muted py-4">Tidak ada konflik.</td></tr>
      </tbody></table></div><div class="px-3 pb-2"><CommonPager v-model:page="page" :page-size="size" :total="list.total" /></div></div>

    <div v-if="open" class="modal-backdrop-custom" @click.self="open = null">
      <div class="modal-panel wide">
        <div class="p-3 border-bottom"><strong>{{ open.table }}</strong> · PK <span class="mono">{{ JSON.stringify(open.primaryKey) }}</span> · <span class="badge text-bg-light border">{{ open.conflictType }}</span></div>
        <div class="p-3"><table class="table table-sm mb-0">
          <thead><tr><th>Kolom</th><th class="diff-a">Endpoint A</th><th class="diff-b">Endpoint B</th></tr></thead>
          <tbody>
            <tr v-if="!open.endpointAValue"><td colspan="3" class="text-danger small">Baris tidak ada di Endpoint A (dihapus).</td></tr>
            <tr v-if="!open.endpointBValue"><td colspan="3" class="text-danger small">Baris tidak ada di Endpoint B (dihapus).</td></tr>
            <tr v-for="c in cols" :key="c" :class="{ 'table-warning': differs(c) }"><td class="mono">{{ c }}</td><td>{{ fmt(open.endpointAValue?.[c]) }}</td><td>{{ fmt(open.endpointBValue?.[c]) }}</td></tr>
          </tbody></table>
          <div class="small text-muted mt-2">Versi (hash): A {{ open.endpointAVersion?.hash?.slice(0, 10) }} · B {{ open.endpointBVersion?.hash?.slice(0, 10) }}</div>
          <div v-if="open.status === 'RESOLVED'" class="alert alert-success py-2 mt-2 mb-0 small">Diselesaikan {{ fmtDate(open.resolvedAt) }} oleh {{ open.resolvedByName }} ({{ open.resolutionStrategy }})</div>
        </div>
        <div class="p-3 border-top d-flex justify-content-between">
          <button class="btn btn-light" @click="open = null">Tutup</button>
          <div v-if="open.status === 'PENDING' && auth.canOperate.value" class="d-flex gap-2">
            <button class="btn btn-primary" @click="resolve('A')">Gunakan versi A → B</button><button class="btn btn-warning" @click="resolve('B')">Gunakan versi B → A</button></div>
        </div>
      </div>
    </div>
  </div>
</template>
