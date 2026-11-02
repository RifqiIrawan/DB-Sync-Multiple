<script setup lang="ts">
const route = useRoute(); const id = route.params.id as string
const api = useApi(); const toast = useToast(); const auth = useAuth()
const data = ref<any>(null); const rows = ref<any[]>([]); const saving = ref(false); const rep = ref<any>(null)

async function load() {
  data.value = await api.get(`/table-rules/${id}/column-mapping`)
  rows.value = data.value.mappings.map((m: any) => ({ ...m }))
}
onMounted(load)
const colB = computed(() => data.value?.columnsB ?? [])
const usedA = computed(() => new Set(rows.value.map(r => r.endpointAColumn.toLowerCase())))
const freeA = computed(() => (data.value?.columnsA ?? []).filter((c: any) => !usedA.value.has(c.name.toLowerCase())))
function add(name: string) {
  const b = colB.value.find((c: any) => c.name.toLowerCase() === name.toLowerCase())
  const a = data.value.columnsA.find((c: any) => c.name === name)
  rows.value.push({ endpointAColumn: name, endpointBColumn: b?.name ?? '', isPrimaryKey: !!a?.isPrimaryKey, isEnabled: !!b, dataTypeStrategy: null })
}
const typeOf = (side: 'A' | 'B', n: string) => (side === 'A' ? data.value.columnsA : data.value.columnsB).find((c: any) => c.name === n)?.nativeType ?? '?'
async function save() {
  if (rows.value.some(r => r.isEnabled && !r.endpointBColumn)) return toast.warn('Pilih kolom B untuk setiap kolom aktif')
  saving.value = true
  try {
    await api.put(`/table-rules/${id}/column-mapping`, rows.value)
    rep.value = await api.post(`/table-rules/${id}/validate`)
    toast.success('Mapping disimpan'); await load()
  } finally { saving.value = false }
}
</script>
<template>
  <div v-if="data">
    <CommonPageHeader title="Column Mapping" subtitle="Petakan kolom Endpoint A ke Endpoint B. Primary key harus sama dengan primary key tabel.">
      <button class="btn btn-light" @click="$router.back()">‹ Kembali</button>
      <button v-if="auth.canManage.value" class="btn btn-primary" :disabled="saving" @click="save">Simpan & validasi</button>
    </CommonPageHeader>
    <div class="card mb-3"><div class="table-responsive"><table class="table table-sm align-middle mb-0">
      <thead><tr><th style="width: 70px">Aktif</th><th>Kolom A</th><th>Tipe A</th><th>⇄</th><th>Kolom B</th><th>Tipe B</th><th class="text-center">PK</th><th></th></tr></thead>
      <tbody>
        <tr v-for="(r, i) in rows" :key="i" :class="{ 'text-muted': !r.isEnabled }">
          <td><input v-model="r.isEnabled" type="checkbox" class="form-check-input" :disabled="!auth.canManage.value"></td>
          <td class="mono">{{ r.endpointAColumn }}</td><td class="small">{{ typeOf('A', r.endpointAColumn) }}</td><td>→</td>
          <td><select v-model="r.endpointBColumn" class="form-select form-select-sm" style="max-width: 220px" :disabled="!auth.canManage.value"><option value=""></option><option v-for="c in colB" :key="c.name" :value="c.name">{{ c.name }}</option></select></td>
          <td class="small">{{ r.endpointBColumn ? typeOf('B', r.endpointBColumn) : '' }}</td>
          <td class="text-center"><input v-model="r.isPrimaryKey" type="checkbox" class="form-check-input" :disabled="!auth.canManage.value"></td>
          <td><button v-if="auth.canManage.value" class="btn btn-sm btn-link text-danger" @click="rows.splice(i, 1)">hapus</button></td>
        </tr>
      </tbody></table></div>
      <div class="card-footer bg-white d-flex gap-2 align-items-center flex-wrap" v-if="freeA.length && auth.canManage.value">
        <span class="small text-muted">Kolom A belum dipetakan:</span>
        <button v-for="c in freeA" :key="c.name" class="btn btn-sm btn-outline-secondary" @click="add(c.name)">+ {{ c.name }}</button></div></div>
    <div class="card" v-if="rep"><div class="card-header">Hasil validasi <CommonStatusBadge :status="rep.status" /></div>
      <table class="table table-sm mb-0"><tbody><tr v-for="(i, k) in rep.issues" :key="k"><td style="width: 80px"><span class="badge" :class="i.severity === 'error' ? 'text-bg-danger' : 'text-bg-warning'">{{ i.severity }}</span></td><td class="mono small" style="width: 220px">{{ i.code }}</td><td>{{ i.message }}</td></tr>
        <tr v-if="!rep.issues.length"><td class="text-success p-3">Tidak ada catatan.</td></tr></tbody></table></div>
  </div>
</template>
