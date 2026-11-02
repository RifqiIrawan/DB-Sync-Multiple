<script setup lang="ts">
const props = defineProps<{ groupId: string; locked?: boolean; groupDirection?: string }>()
const emit = defineEmits<{ (e: 'changed'): void; (e: 'next', tab: string): void }>()
const api = useApi(); const toast = useToast(); const confirm = useConfirm(); const auth = useAuth()

const tables = ref<any[]>([]); const rules = ref<any[]>([]); const loading = ref(true)
const picked = ref<Set<string>>(new Set()); const filter = ref(''); const chip = ref<'all' | 'ready' | 'selected' | 'blocked'>('all')
const schemaA = ref(''); const schemaB = ref(''); const showAdv = ref(false); const showHow = ref(true)
const detail = ref<any>(null); const adding = ref(false); const expanded = ref<Set<string>>(new Set())

async function load() {
  loading.value = true
  try {
    const q = { schemaA: schemaA.value || undefined, schemaB: schemaB.value || undefined }
    const [t, r] = await Promise.all([api.get(`/sync-groups/${props.groupId}/available-tables`, q), api.get(`/sync-groups/${props.groupId}/table-rules`)])
    tables.value = t; rules.value = r; picked.value = new Set()
    if (r.length) showHow.value = false
  } finally { loading.value = false }
}
onMounted(load)

// ---- bahasa awam untuk status tiap tabel ----
const reasonText = (t: any): string => {
  if (t.status === 'ONLY_A') return 'Hanya ada di Database A — buat dulu tabelnya di Database B (DBSync tidak membuat tabel otomatis).'
  if (t.status === 'ONLY_B') return 'Hanya ada di Database B — buat dulu tabelnya di Database A.'
  const i = (t.issues ?? [])[0]
  if (!i) return 'Tidak memenuhi syarat.'
  if (i.code === 'PRIMARY_KEY_REQUIRED') return 'Tidak punya primary key — DBSync butuh primary key untuk mengenali baris yang sama.'
  return i.message
}
const kind = (t: any) => t.selected ? 'selected' : t.eligible ? 'ready' : 'blocked'
const counts = computed(() => ({
  all: tables.value.length,
  ready: tables.value.filter(t => kind(t) === 'ready').length,
  selected: tables.value.filter(t => kind(t) === 'selected').length,
  blocked: tables.value.filter(t => kind(t) === 'blocked').length,
}))
const shown = computed(() => tables.value.filter(t =>
  (chip.value === 'all' || kind(t) === chip.value) && (!filter.value || t.name.toLowerCase().includes(filter.value.toLowerCase()))))
const selectable = computed(() => shown.value.filter(t => kind(t) === 'ready'))
const allPicked = computed(() => selectable.value.length > 0 && selectable.value.every(t => picked.value.has(t.name)))
function toggle(n: string) { picked.value.has(n) ? picked.value.delete(n) : picked.value.add(n); picked.value = new Set(picked.value) }
function toggleAll() { picked.value = allPicked.value ? new Set() : new Set(selectable.value.map(t => t.name)) }

async function addPicked() {
  if (!picked.value.size) return
  adding.value = true
  try {
    const r = await api.post(`/sync-groups/${props.groupId}/table-rules/bulk`, { schemaA: schemaA.value || null, schemaB: schemaB.value || null, tables: [...picked.value], selectAllEligible: false })
    toast.success(`${r.added.length} tabel ditambahkan`, r.skipped.length ? `${r.skipped.length} dilewati` : undefined); await load(); emit('changed')
  } finally { adding.value = false }
}
async function addAllReady() {
  adding.value = true
  try {
    const r = await api.post(`/sync-groups/${props.groupId}/table-rules/bulk`, { schemaA: schemaA.value || null, schemaB: schemaB.value || null, selectAllEligible: true })
    toast.success(`${r.added.length} tabel ditambahkan`, r.skipped.length ? `${r.skipped.length} dilewati karena belum memenuhi syarat` : undefined); await load(); emit('changed')
  } finally { adding.value = false }
}
async function removeRule(r: any) {
  if (!(await confirm.ask('Keluarkan tabel dari grup?', `${r.endpointATable}: pengaturan, baseline, dan riwayatnya untuk grup ini dihapus. Data di kedua database TIDAK berubah.`, true))) return
  await api.del(`/table-rules/${r.id}`); await load(); emit('changed')
}
async function patchRule(r: any, patch: any) { await api.put(`/table-rules/${r.id}`, patch); Object.assign(r, patch); emit('changed') }
async function revalidate(r: any) { const rep = await api.post(`/table-rules/${r.id}/validate`); r.validationStatus = rep.status; r.validationResult = rep.issues; detail.value = { rule: r, issues: rep.issues } }
const issuesOf = (r: any) => (r.validationResult as any[]) ?? []

// arah per tabel: satu pilihan sederhana menggantikan dua kotak centang
const dirOf = (r: any) => r.syncAToB && r.syncBToA ? 'BOTH' : r.syncAToB ? 'AB' : r.syncBToA ? 'BA' : 'NONE'
function setDir(r: any, v: string) { patchRule(r, { syncAToB: v === 'BOTH' || v === 'AB', syncBToA: v === 'BOTH' || v === 'BA' }) }
const groupDirLabel = computed(() => props.groupDirection === 'A_TO_B' ? 'A → B saja' : props.groupDirection === 'B_TO_A' ? 'B → A saja' : '')
const needInitial = computed(() => rules.value.filter(r => !String(r.initialSyncStatus).startsWith('COMPLETED')).length)
</script>
<template>
  <div class="d-grid gap-3">
    <!-- penjelasan konsep -->
    <div class="card" v-if="showHow">
      <div class="card-body">
        <div class="d-flex justify-content-between align-items-start mb-2"><h5 class="mb-0"><i class="bi bi-lightbulb me-2"></i>Bagaimana ini bekerja?</h5>
          <button class="btn btn-sm btn-light" @click="showHow = false">Tutup</button></div>
        <div class="row g-3">
          <div class="col-md-4"><div class="d-flex gap-2"><span class="step-pill active">1</span><div><strong>Pilih tabel</strong><div class="help">DBSync mencocokkan tabel yang <em>namanya sama</em> di Database <span class="ep ep-a">A</span> dan <span class="ep ep-b">B</span>. Hanya tabel yang ada di keduanya dan punya primary key yang bisa dipilih.</div></div></div></div>
          <div class="col-md-4"><div class="d-flex gap-2"><span class="step-pill">2</span><div><strong>Salin data awal</strong><div class="help">Setelah tabel dipilih, jalankan <em>Initial Sync</em> (tab Job) untuk menyalin isi tabel dari satu database ke yang lain.</div></div></div></div>
          <div class="col-md-4"><div class="d-flex gap-2"><span class="step-pill">3</span><div><strong>Tetap sinkron</strong><div class="help">Setelah grup aktif, setiap tambah/ubah/hapus data otomatis disalin sesuai arah yang Anda pilih. Struktur tabel tidak diubah oleh DBSync.</div></div></div></div>
        </div>
      </div>
    </div>

    <!-- bilah aksi utama (menempel saat di-scroll) -->
    <div class="card" style="position: sticky; top: 66px; z-index: 6">
      <div class="card-body py-3">
        <div class="d-flex flex-wrap gap-2 align-items-center">
          <div class="flex-grow-1">
            <div class="fw-semibold" v-if="!loading">
              <template v-if="counts.ready">{{ counts.ready }} tabel siap ditambahkan</template>
              <template v-else-if="counts.selected && !counts.blocked">Semua tabel yang cocok sudah ditambahkan ✓</template>
              <template v-else>Tidak ada tabel baru yang siap</template>
            </div>
            <div class="help" v-if="!loading">{{ counts.all }} tabel ditemukan · {{ counts.selected }} sudah dipilih · {{ counts.blocked }} belum bisa dipakai
              <a href="#" class="ms-2" @click.prevent="showHow = !showHow">{{ showHow ? 'sembunyikan' : 'bagaimana ini bekerja?' }}</a></div>
            <div class="help" v-else><span class="spinner-border spinner-border-sm me-1"></span>Membaca struktur kedua database…</div>
          </div>
          <template v-if="!locked && auth.canManage.value">
            <button v-if="picked.size" class="btn btn-primary" :disabled="adding" @click="addPicked"><i class="bi bi-plus-circle"></i> Tambahkan {{ picked.size }} tabel terpilih</button>
            <button class="btn" :class="picked.size ? 'btn-light' : 'btn-primary'" :disabled="adding || !counts.ready" @click="addAllReady">
              <span v-if="adding" class="spinner-border spinner-border-sm"></span><i v-else class="bi bi-check2-all"></i> Tambahkan semua yang siap ({{ counts.ready }})</button>
          </template>
          <button class="btn btn-light" title="Baca ulang struktur database" aria-label="Baca ulang struktur database" :disabled="loading" @click="load"><i class="bi bi-arrow-repeat" :class="{ spin: loading }"></i></button>
        </div>
      </div>
    </div>

    <!-- lanjut ke langkah berikut -->
    <div v-if="rules.length && needInitial && !locked" class="alert alert-success d-flex align-items-center gap-3 mb-0">
      <i class="bi bi-check-circle-fill fs-4"></i>
      <div class="flex-grow-1"><strong>{{ rules.length }} tabel sudah dipilih.</strong> Langkah berikutnya: salin data awal ke database tujuan lewat <em>Initial Sync</em>.</div>
      <button class="btn btn-success" @click="emit('next', 'jobs')">Ke Initial Sync <i class="bi bi-arrow-right"></i></button>
    </div>

    <!-- daftar tabel yang ditemukan -->
    <div class="card">
      <div class="card-header d-flex flex-wrap gap-2 justify-content-between align-items-center">
        <span>Tabel yang ditemukan</span>
        <div class="d-flex gap-2 flex-wrap align-items-center">
          <div class="d-flex gap-1">
            <button v-for="c in ([['all', 'Semua'], ['ready', 'Siap'], ['selected', 'Sudah dipilih'], ['blocked', 'Belum bisa']] as const)" :key="c[0]" class="btn btn-sm" :class="chip === c[0] ? 'btn-primary' : 'btn-light'" @click="chip = c[0]">{{ c[1] }} <span class="opacity-75">{{ counts[c[0]] }}</span></button>
          </div>
          <div class="input-group" style="width: 190px"><span class="input-group-text"><i class="bi bi-search"></i></span><input v-model="filter" class="form-control form-control-sm" placeholder="Cari nama tabel…"></div>
          <a href="#" class="small" @click.prevent="showAdv = !showAdv">{{ showAdv ? 'tutup lanjutan' : 'lanjutan' }}</a>
        </div>
      </div>
      <div v-if="showAdv" class="card-body border-bottom py-2 d-flex flex-wrap gap-2 align-items-center">
        <span class="help">Nama schema (kosongkan untuk bawaan: MySQL = nama database, PostgreSQL = public, SQL Server = dbo):</span>
        <input v-model="schemaA" class="form-control form-control-sm" style="width: 140px" placeholder="schema A" @change="load">
        <input v-model="schemaB" class="form-control form-control-sm" style="width: 140px" placeholder="schema B" @change="load">
      </div>

      <CommonSkeleton v-if="loading" :rows="5" />
      <div v-else class="table-responsive"><table class="table align-middle mb-0">
        <thead><tr>
          <th style="width: 40px"><input type="checkbox" class="form-check-input" title="Centang semua yang siap pada daftar ini" :checked="allPicked" :disabled="locked || !selectable.length" @change="toggleAll"></th>
          <th>Tabel</th><th>Isi (perkiraan)</th><th>Primary key</th><th>Status</th><th></th></tr></thead>
        <tbody>
          <tr v-for="t in shown" :key="t.name" :class="{ 'table-light': t.selected }" :style="kind(t) === 'blocked' ? 'opacity:.75' : ''">
            <td><input type="checkbox" class="form-check-input" :disabled="locked || kind(t) !== 'ready'" :checked="picked.has(t.name)" @change="toggle(t.name)"></td>
            <td><strong>{{ t.name }}</strong>
              <div class="help">{{ t.aColumns ?? '–' }} kolom di <span class="ep ep-a">A</span> · {{ t.bColumns ?? '–' }} kolom di <span class="ep ep-b">B</span></div></td>
            <td class="small">{{ fmtNum(t.aRows) }} <span class="text-muted">di A</span><br>{{ fmtNum(t.bRows) }} <span class="text-muted">di B</span></td>
            <td class="mono small">{{ (t.aPrimaryKey ?? []).join(', ') || '—' }}</td>
            <td style="max-width: 360px">
              <span v-if="kind(t) === 'selected'" class="status s-info"><span class="dot"></span>Sudah dipilih</span>
              <template v-else-if="kind(t) === 'ready'">
                <span class="status" :class="t.validation === 'WARNING' ? 's-warn' : 's-ok'"><span class="dot"></span>{{ t.validation === 'WARNING' ? 'Siap (ada catatan)' : 'Siap' }}</span>
                <button v-if="t.issues?.length" class="btn btn-link btn-sm p-0 ms-2" @click="detail = { rule: { endpointATable: t.name }, issues: t.issues }">lihat catatan</button>
              </template>
              <template v-else><span class="status s-bad"><span class="dot"></span>Belum bisa</span><div class="help mt-1">{{ reasonText(t) }}</div></template>
            </td>
            <td class="text-end"><button v-if="!locked && auth.canManage.value && kind(t) === 'ready'" class="btn btn-sm btn-outline-primary" :disabled="adding" @click="picked = new Set([t.name]); addPicked()">Tambahkan</button></td>
          </tr>
          <tr v-if="!shown.length"><td colspan="6"><CommonEmptyState icon="funnel" title="Tidak ada tabel pada filter ini" text="Ganti filter di atas atau hapus kata pencarian." /></td></tr>
        </tbody>
      </table></div>
    </div>

    <!-- tabel yang sudah dipilih -->
    <div class="card">
      <div class="card-header d-flex justify-content-between align-items-center"><span>Tabel yang disinkronkan ({{ rules.length }})</span>
        <span v-if="groupDirLabel" class="help">Arah grup: <strong>{{ groupDirLabel }}</strong> — membatasi arah tiap tabel</span></div>
      <div v-if="!rules.length" class="card-body"><CommonEmptyState icon="table" title="Belum ada tabel yang dipilih" text="Tambahkan tabel dari daftar di atas. Klik “Tambahkan semua yang siap” untuk memilih semuanya sekaligus." /></div>
      <div v-else class="table-responsive"><table class="table align-middle mb-0">
        <thead><tr><th>Tabel</th><th>Arah salinan</th><th>Pemeriksaan</th><th>Data awal</th><th></th></tr></thead>
        <tbody>
          <template v-for="r in rules" :key="r.id">
            <tr>
              <td><strong>{{ r.endpointATable }}</strong><div class="help mono">{{ r.endpointASchema }} ⇄ {{ r.endpointBSchema }}.{{ r.endpointBTable }}</div></td>
              <td>
                <select class="form-select form-select-sm" style="width: 190px" :value="dirOf(r)" :disabled="locked || !auth.canManage.value" @change="setDir(r, ($event.target as HTMLSelectElement).value)">
                  <option value="BOTH">Dua arah (A ⇄ B)</option><option value="AB">Hanya A → B</option><option value="BA">Hanya B → A</option><option value="NONE" disabled>Tidak disalin</option>
                </select></td>
              <td><CommonStatusBadge :status="r.validationStatus" /><button class="btn btn-link btn-sm" @click="detail = { rule: r, issues: issuesOf(r) }">{{ issuesOf(r).length }} catatan</button></td>
              <td><CommonStatusBadge :status="r.initialSyncStatus === 'PENDING' ? 'PENDING' : r.initialSyncStatus" /><div class="help" v-if="r.initialSyncStatus === 'PENDING'">belum disalin</div></td>
              <td class="text-end text-nowrap">
                <button class="btn btn-sm btn-light" title="Pengaturan lanjutan" aria-label="Pengaturan lanjutan" @click="expanded.has(r.id) ? expanded.delete(r.id) : expanded.add(r.id); expanded = new Set(expanded)"><i class="bi bi-sliders"></i></button>
                <NuxtLink :to="`/table-mapping/${r.id}`" class="btn btn-sm btn-light" title="Petakan kolom (bila nama kolom berbeda)" aria-label="Petakan kolom (bila nama kolom berbeda)"><i class="bi bi-columns-gap"></i></NuxtLink>
                <button v-if="auth.canManage.value" class="btn btn-sm btn-light" title="Periksa ulang" aria-label="Periksa ulang" @click="revalidate(r)"><i class="bi bi-check2-square"></i></button>
                <button v-if="!locked && auth.canManage.value" class="btn btn-sm btn-outline-danger" title="Keluarkan dari grup" aria-label="Keluarkan dari grup" @click="removeRule(r)"><i class="bi bi-trash"></i></button>
              </td>
            </tr>
            <tr v-if="expanded.has(r.id)"><td colspan="5" style="background: var(--card-2)">
              <div class="d-flex flex-wrap gap-4 align-items-center">
                <span class="help fw-semibold">Jenis perubahan yang disalin:</span>
                <div class="form-check" v-for="o in ([['syncInsert', 'Data baru (INSERT)'], ['syncUpdate', 'Data diubah (UPDATE)'], ['syncDelete', 'Data dihapus (DELETE)']] as const)" :key="o[0]">
                  <input :id="`${r.id}${o[0]}`" type="checkbox" class="form-check-input" :checked="r[o[0]]" :disabled="locked || !auth.canManage.value" @change="patchRule(r, { [o[0]]: ($event.target as HTMLInputElement).checked })">
                  <label :for="`${r.id}${o[0]}`" class="form-check-label">{{ o[1] }}</label></div>
              </div>
              <div class="help mt-1">Matikan “Data dihapus” bila penghapusan di satu database tidak boleh ikut menghapus di database lain.</div>
            </td></tr>
          </template>
        </tbody>
      </table></div>
    </div>

    <div v-if="detail" class="modal-backdrop-custom" @click.self="detail = null">
      <div class="modal-panel wide">
        <div class="p-3 border-bottom fw-semibold">Hasil pemeriksaan — {{ detail.rule.endpointATable }}</div>
        <div class="p-3"><table class="table table-sm mb-0"><thead><tr><th>Level</th><th>Kode</th><th>Penjelasan</th></tr></thead><tbody>
          <tr v-for="(i, k) in detail.issues" :key="k"><td><span class="badge" :class="i.severity === 'error' ? 'text-bg-danger' : 'text-bg-warning'">{{ i.severity === 'error' ? 'harus diperbaiki' : 'catatan' }}</span></td><td class="mono small">{{ i.code }}</td><td>{{ i.message }}</td></tr>
          <tr v-if="!detail.issues.length"><td colspan="3" class="text-success">Tidak ada catatan. Struktur kedua database kompatibel.</td></tr></tbody></table></div>
        <div class="p-3 border-top text-end"><button class="btn btn-light" @click="detail = null">Tutup</button></div>
      </div>
    </div>
  </div>
</template>
<style scoped>
.spin { animation: spin .8s linear infinite; display: inline-block; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
