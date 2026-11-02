<script setup lang="ts">
const props = defineProps<{ groupId: string; locked?: boolean }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const api = useApi(); const toast = useToast(); const confirm = useConfirm(); const auth = useAuth()
const tables = ref<any[]>([]); const rules = ref<any[]>([]); const loading = ref(false)
const picked = ref<Set<string>>(new Set()); const filter = ref(''); const schemaA = ref(''); const schemaB = ref('')
const detail = ref<any>(null)

async function load() {
  loading.value = true
  try {
    const q = { schemaA: schemaA.value || undefined, schemaB: schemaB.value || undefined }
    const [t, r] = await Promise.all([api.get(`/sync-groups/${props.groupId}/available-tables`, q), api.get(`/sync-groups/${props.groupId}/table-rules`)])
    tables.value = t; rules.value = r; picked.value = new Set()
  } finally { loading.value = false }
}
onMounted(load)

const shown = computed(() => tables.value.filter(t => !filter.value || t.name.toLowerCase().includes(filter.value.toLowerCase())))
const selectable = computed(() => shown.value.filter(t => t.eligible && !t.selected))
function toggle(n: string) { picked.value.has(n) ? picked.value.delete(n) : picked.value.add(n); picked.value = new Set(picked.value) }
function toggleAll() { picked.value = picked.value.size === selectable.value.length ? new Set() : new Set(selectable.value.map(t => t.name)) }

async function addPicked() {
  if (!picked.value.size) return
  const r = await api.post(`/sync-groups/${props.groupId}/table-rules/bulk`, { schemaA: schemaA.value || null, schemaB: schemaB.value || null, tables: [...picked.value], selectAllEligible: false })
  toast.success(`${r.added.length} tabel ditambahkan`, r.skipped.length ? `${r.skipped.length} dilewati` : undefined); await load(); emit('changed')
}
async function addAllEligible() {
  if (!(await confirm.ask('Pilih semua tabel yang memenuhi syarat?', 'Tabel tanpa primary key / tipe tidak didukung akan dilewati.'))) return
  const r = await api.post(`/sync-groups/${props.groupId}/table-rules/bulk`, { schemaA: schemaA.value || null, schemaB: schemaB.value || null, selectAllEligible: true })
  toast.success(`${r.added.length} tabel ditambahkan`, r.skipped.length ? `${r.skipped.length} dilewati` : undefined); await load(); emit('changed')
}
async function removeRule(r: any) {
  if (!(await confirm.ask('Hapus tabel dari group?', `${r.endpointATable}: baseline, checkpoint, event, dan konflik tabel ini ikut dihapus (data database tidak berubah).`, true))) return
  await api.del(`/table-rules/${r.id}`); await load(); emit('changed')
}
async function patchRule(r: any, patch: any) {
  await api.put(`/table-rules/${r.id}`, patch); Object.assign(r, patch); emit('changed')
}
async function revalidate(r: any) { const rep = await api.post(`/table-rules/${r.id}/validate`); r.validationStatus = rep.status; r.validationResult = rep.issues; detail.value = { rule: r, issues: rep.issues } }
const issuesOf = (r: any) => (r.validationResult as any[]) ?? []
</script>
<template>
  <div>
    <div class="card mb-3">
      <div class="card-header d-flex justify-content-between align-items-center flex-wrap gap-2">
        <span>Tabel tersedia (A ⇄ B)</span>
        <div class="d-flex gap-2 align-items-center">
          <input v-model="schemaA" class="form-control form-control-sm" style="width: 130px" placeholder="schema A (opsional)" @change="load">
          <input v-model="schemaB" class="form-control form-control-sm" style="width: 130px" placeholder="schema B (opsional)" @change="load">
          <input v-model="filter" class="form-control form-control-sm" style="width: 160px" placeholder="Filter nama...">
          <button class="btn btn-sm btn-outline-secondary" :disabled="loading" @click="load"><i class="bi bi-arrow-repeat"></i></button>
        </div>
      </div>
      <div class="table-responsive"><table class="table table-sm align-middle mb-0">
        <thead><tr><th style="width: 36px"><input type="checkbox" class="form-check-input" :checked="selectable.length > 0 && picked.size === selectable.length" :disabled="locked || !selectable.length" @change="toggleAll"></th>
          <th>Tabel</th><th>A</th><th>B</th><th>Primary key</th><th>FK</th><th>Struktur</th><th></th></tr></thead>
        <tbody>
          <tr v-if="loading"><td colspan="8" class="text-center text-muted py-3">Membaca schema kedua database...</td></tr>
          <tr v-for="t in shown" :key="t.name" :class="{ 'table-light': t.selected }">
            <td><input type="checkbox" class="form-check-input" :disabled="locked || !t.eligible || t.selected" :checked="picked.has(t.name)" @change="toggle(t.name)"></td>
            <td><strong>{{ t.name }}</strong></td>
            <td class="small">{{ t.tableA ? `${t.aColumns} kolom` : '—' }}<div class="text-muted">~{{ fmtNum(t.aRows) }} baris</div></td>
            <td class="small">{{ t.tableB ? `${t.bColumns} kolom` : '—' }}<div class="text-muted">~{{ fmtNum(t.bRows) }} baris</div></td>
            <td class="mono small">{{ (t.aPrimaryKey ?? []).join(', ') || '—' }}</td><td class="small">{{ t.aForeignKeys ?? 0 }}</td>
            <td><CommonStatusBadge :status="t.selected ? 'SELECTED' : t.validation" />
              <div class="small text-danger" v-if="!t.eligible && t.issues?.length">{{ t.issues[0].message }}</div></td>
            <td><button v-if="t.issues?.length && t.eligible" class="btn btn-link btn-sm p-0" @click="detail = { rule: { endpointATable: t.name }, issues: t.issues }">detail</button></td>
          </tr>
        </tbody>
      </table></div>
      <div class="card-footer bg-white d-flex justify-content-between" v-if="!locked && auth.canManage.value">
        <button class="btn btn-sm btn-outline-primary" @click="addAllEligible"><i class="bi bi-check2-all"></i> Pilih semua yang memenuhi syarat</button>
        <button class="btn btn-sm btn-primary" :disabled="!picked.size" @click="addPicked">Tambahkan {{ picked.size }} tabel terpilih</button>
      </div>
    </div>

    <div class="card">
      <div class="card-header">Tabel yang disinkronkan ({{ rules.length }})</div>
      <div class="table-responsive"><table class="table table-sm align-middle mb-0">
        <thead><tr><th>Tabel</th><th class="text-center">A→B</th><th class="text-center">B→A</th><th class="text-center">INS</th><th class="text-center">UPD</th><th class="text-center">DEL</th><th>Validasi</th><th>Initial sync</th><th></th></tr></thead>
        <tbody>
          <tr v-for="r in rules" :key="r.id">
            <td><strong>{{ r.endpointATable }}</strong><div class="small text-muted mono">{{ r.endpointASchema }} ⇄ {{ r.endpointBSchema }}.{{ r.endpointBTable }}</div></td>
            <td v-for="k in ['syncAToB', 'syncBToA', 'syncInsert', 'syncUpdate', 'syncDelete']" :key="k" class="text-center">
              <input type="checkbox" class="form-check-input" :checked="r[k]" :disabled="locked || !auth.canManage.value" @change="patchRule(r, { [k]: ($event.target as HTMLInputElement).checked })"></td>
            <td><CommonStatusBadge :status="r.validationStatus" /><button class="btn btn-link btn-sm" @click="detail = { rule: r, issues: issuesOf(r) }">{{ issuesOf(r).length }} catatan</button></td>
            <td><CommonStatusBadge :status="r.initialSyncStatus" /></td>
            <td class="text-end text-nowrap">
              <NuxtLink :to="`/table-mapping/${r.id}`" class="btn btn-sm btn-outline-secondary me-1" title="Column mapping"><i class="bi bi-columns-gap"></i></NuxtLink>
              <button v-if="auth.canManage.value" class="btn btn-sm btn-outline-primary me-1" title="Validasi ulang" @click="revalidate(r)"><i class="bi bi-check2-square"></i></button>
              <button v-if="!locked && auth.canManage.value" class="btn btn-sm btn-outline-danger" @click="removeRule(r)"><i class="bi bi-trash"></i></button>
            </td>
          </tr>
          <tr v-if="!rules.length"><td colspan="9" class="text-center text-muted py-3">Belum ada tabel dipilih.</td></tr>
        </tbody>
      </table></div>
    </div>

    <div v-if="detail" class="modal-backdrop-custom" @click.self="detail = null">
      <div class="modal-panel wide">
        <div class="p-3 border-bottom fw-semibold">Hasil validasi — {{ detail.rule.endpointATable }}</div>
        <div class="p-3"><table class="table table-sm mb-0"><thead><tr><th>Level</th><th>Kode</th><th>Pesan</th></tr></thead><tbody>
          <tr v-for="(i, k) in detail.issues" :key="k"><td><span class="badge" :class="i.severity === 'error' ? 'text-bg-danger' : 'text-bg-warning'">{{ i.severity }}</span></td><td class="mono small">{{ i.code }}</td><td>{{ i.message }}</td></tr>
          <tr v-if="!detail.issues.length"><td colspan="3" class="text-success">Tidak ada catatan. Struktur kompatibel.</td></tr></tbody></table></div>
        <div class="p-3 border-top text-end"><button class="btn btn-light" @click="detail = null">Tutup</button></div>
      </div>
    </div>
  </div>
</template>
