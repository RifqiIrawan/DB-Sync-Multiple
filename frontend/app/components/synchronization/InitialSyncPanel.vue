<script setup lang="ts">
// Panel Initial Sync: pilih cara -> CEK DATA (read-only) -> pilih model penyelesaian -> jalankan.
const props = defineProps<{ groupId: string; groupDirection: string; rules: any[]; disabled?: boolean }>()
const emit = defineEmits<{ (e: 'started'): void }>()
const api = useApi(); const toast = useToast(); const confirm = useConfirm()

const bidir = computed(() => props.groupDirection === 'BIDIRECTIONAL')
const canAB = computed(() => props.groupDirection !== 'B_TO_A')
const canBA = computed(() => props.groupDirection !== 'A_TO_B')
const mode = ref<'BOTH' | 'A' | 'B'>(bidir.value ? 'BOTH' : canAB.value ? 'A' : 'B')
watch(() => props.groupDirection, () => { mode.value = bidir.value ? 'BOTH' : canAB.value ? 'A' : 'B' })

const selected = ref<string[]>([]); const restart = ref(false); const showAdv = ref(false)
const preview = ref<any>(null); const previewing = ref(false); const starting = ref(false)
const differentPolicy = ref<'CONFLICT' | 'FILL_EMPTY' | 'A_WINS' | 'B_WINS'>('CONFLICT')
const fillFallback = ref<'CONFLICT' | 'A_WINS' | 'B_WINS'>('CONFLICT')
const overwriteMode = ref<'SKIP_EXISTING' | 'OVERWRITE'>('SKIP_EXISTING')
watch([mode, selected], () => { preview.value = null }, { deep: true })

const showAllTables = ref(false)
const needsAttention = (t: any) => !!(t.onlyInSource || t.onlyInTarget || t.different || t.error)
const visibleTables = computed(() => (preview.value?.tables ?? []).filter((t: any) => showAllTables.value || needsAttention(t)))
const hiddenCount = computed(() => (preview.value?.tables ?? []).length - (preview.value?.tables ?? []).filter(needsAttention).length)
const srcName = computed(() => (mode.value === 'B' ? 'B' : 'A')); const tgtName = computed(() => (srcName.value === 'A' ? 'B' : 'A'))
const tot = computed(() => {
  const t = preview.value?.tables ?? []; const s = (k: string) => t.reduce((a: number, x: any) => a + (x[k] ?? 0), 0)
  return { onlySrc: s('onlyInSource'), onlyTgt: s('onlyInTarget'), same: s('identical'), diff: s('different'), fill: s('fillable') }
})

async function runPreview() {
  previewing.value = true
  try {
    preview.value = await api.post(`/sync-groups/${props.groupId}/initial-sync/preview`, { sourceRole: mode.value, tableRuleIds: selected.value.length ? selected.value : null })
    overwriteMode.value = 'SKIP_EXISTING'
    differentPolicy.value = 'CONFLICT'
  } finally { previewing.value = false }
}

const summary = computed(() => {
  if (!preview.value) return ''
  const t = tot.value; const parts: string[] = []
  if (mode.value === 'BOTH') {
    if (t.onlySrc) parts.push(`${fmtNum(t.onlySrc)} baris hanya di A akan disalin ke B`)
    if (t.onlyTgt) parts.push(`${fmtNum(t.onlyTgt)} baris hanya di B akan disalin ke A`)
    if (t.diff) {
      if (differentPolicy.value === 'CONFLICT') parts.push(`${fmtNum(t.diff)} baris berbeda akan menjadi konflik untuk Anda putuskan (tidak ada yang ditimpa)`)
      else if (differentPolicy.value === 'FILL_EMPTY') parts.push(`${fmtNum(t.fill)} baris berbeda diisi saling melengkapi; ${fmtNum(t.diff - t.fill)} sisanya ${fillFallback.value === 'CONFLICT' ? 'menjadi konflik' : `memakai data ${fillFallback.value === 'A_WINS' ? 'A' : 'B'}`}`)
      else parts.push(`${fmtNum(t.diff)} baris berbeda akan ditimpa dengan data ${differentPolicy.value === 'A_WINS' ? 'A' : 'B'} di kedua sisi`)
    }
  } else {
    parts.push(`${fmtNum(t.onlySrc)} baris baru akan disalin ${srcName.value} → ${tgtName.value}`)
    if (t.diff) parts.push(overwriteMode.value === 'OVERWRITE' ? `${fmtNum(t.diff)} baris di ${tgtName.value} akan DITIMPA (isi lama hilang)` : `${fmtNum(t.diff)} baris di ${tgtName.value} dibiarkan (tidak ditimpa)`)
  }
  if (!parts.length) parts.push('Kedua sisi sudah identik; hanya penandaan sinkron yang dilakukan')
  return parts.join(' · ')
})
const danger = computed(() => (mode.value === 'BOTH' ? ['A_WINS', 'B_WINS'].includes(differentPolicy.value) && tot.value.diff > 0 : overwriteMode.value === 'OVERWRITE' && tot.value.diff > 0))

async function start() {
  if (!(await confirm.ask('Jalankan Initial Sync?', summary.value + (restart.value ? '\nMulai ULANG dari awal (checkpoint diabaikan).' : ''), danger.value))) return
  starting.value = true
  try {
    await api.post(`/sync-groups/${props.groupId}/initial-sync`, {
      sourceRole: mode.value, restart: restart.value, tableRuleIds: selected.value.length ? selected.value : null,
      conflictMode: mode.value === 'BOTH' ? null : overwriteMode.value,
      differentPolicy: mode.value === 'BOTH' ? differentPolicy.value : null, fillFallback: mode.value === 'BOTH' ? fillFallback.value : null,
    })
    toast.info('Initial sync diantrekan'); preview.value = null; emit('started')
  } finally { starting.value = false }
}

// ---- rincian baris (read-only) ----
const detail = ref<any>(null); const detailLoading = ref(false)
async function openDetail(t: any, kind: 'ONLY_A' | 'ONLY_B' | 'DIFFERENT') {
  detail.value = { table: t.table, kind, total: 0, items: [] }; detailLoading.value = true
  try { const r = await api.post(`/sync-groups/${props.groupId}/compare`, { tableRuleId: t.ruleId, kind, limit: 100 }); detail.value = { ...detail.value, ...r } }
  catch { detail.value = null } finally { detailLoading.value = false }
}
const kindFor = (side: 'src' | 'tgt'): 'ONLY_A' | 'ONLY_B' => ((side === 'src' ? srcName.value : tgtName.value) === 'A' ? 'ONLY_A' : 'ONLY_B')
const kindTitle: Record<string, string> = { ONLY_A: 'Baris yang hanya ada di Database A (belum ada di B)', ONLY_B: 'Baris yang hanya ada di Database B (belum ada di A)', DIFFERENT: 'Baris dengan ID sama tetapi isi tidak sama' }
const cell = (v: any) => v === null || v === undefined || v === '' ? '∅' : typeof v === 'object' ? JSON.stringify(v) : String(v)
const rowCols = computed(() => detail.value?.items?.[0]?.row ? Object.keys(detail.value.items[0].row) : [])
</script>
<template>
  <div class="card mb-3">
    <div class="card-header"><i class="bi bi-cloud-arrow-up me-2"></i>Data awal: salin atau gabungkan</div>
    <div class="card-body d-grid gap-3">
      <!-- 1. cara -->
      <div>
        <div class="fw-semibold mb-2">1. Pilih cara</div>
        <div class="d-grid gap-2">
          <label v-if="bidir" class="check-item" :class="{ 'border-primary': mode === 'BOTH' }" style="cursor: pointer">
            <input v-model="mode" type="radio" value="BOTH" class="form-check-input mt-0">
            <div><div class="fw-semibold">Dua arah — saling melengkapi <span class="badge text-bg-success ms-1">disarankan</span></div>
              <div class="help">Data yang belum ada di satu sisi disalin dari sisi lain (A ⇄ B). Data yang sama dibiarkan. Data ber-ID sama tapi isi berbeda <strong>tidak ditimpa</strong>; Anda pilih cara menyelesaikannya.</div></div>
          </label>
          <label v-if="canAB" class="check-item" :class="{ 'border-primary': mode === 'A' }" style="cursor: pointer">
            <input v-model="mode" type="radio" value="A" class="form-check-input mt-0">
            <div><div class="fw-semibold">Salin <span class="ep ep-a">A</span> → <span class="ep ep-b">B</span> saja</div><div class="help">Database B dijadikan salinan A. Isi B yang tidak ada di A tidak disentuh.</div></div>
          </label>
          <label v-if="canBA" class="check-item" :class="{ 'border-primary': mode === 'B' }" style="cursor: pointer">
            <input v-model="mode" type="radio" value="B" class="form-check-input mt-0">
            <div><div class="fw-semibold">Salin <span class="ep ep-b">B</span> → <span class="ep ep-a">A</span> saja</div><div class="help">Database A dijadikan salinan B.</div></div>
          </label>
        </div>
      </div>

      <!-- 2. cek data -->
      <div>
        <div class="fw-semibold mb-2">2. Cek data dulu <span class="help fw-normal">(hanya membaca, tidak mengubah apa pun)</span></div>
        <button class="btn btn-outline-primary w-100" :disabled="previewing || !rules.length" @click="runPreview">
          <span v-if="previewing" class="spinner-border spinner-border-sm"></span><i v-else class="bi bi-search"></i> {{ previewing ? 'Membandingkan kedua database…' : (preview ? 'Cek ulang' : 'Cek data A vs B') }}</button>

        <div v-if="preview" class="mt-3">
          <div class="table-responsive"><table class="table table-sm align-middle mb-0">
            <thead><tr><th>Tabel</th><th class="text-end">Hanya di {{ srcName }}</th><th class="text-end">Hanya di {{ tgtName }}</th><th class="text-end">Identik</th><th class="text-end">Berbeda</th></tr></thead>
            <tbody>
              <tr v-for="t in visibleTables" :key="t.ruleId">
                <td><strong>{{ t.table }}</strong><div class="help">{{ fmtNum(t.sourceRows) }} baris di {{ srcName }} · {{ fmtNum(t.targetRows) }} di {{ tgtName }}</div>
                  <div v-if="t.error" class="text-danger small">{{ t.error }}</div></td>
                <td class="text-end"><a v-if="t.onlyInSource" href="#" @click.prevent="openDetail(t, kindFor('src'))">{{ fmtNum(t.onlyInSource) }}</a><span v-else class="text-muted">0</span></td>
                <td class="text-end"><a v-if="t.onlyInTarget" href="#" @click.prevent="openDetail(t, kindFor('tgt'))">{{ fmtNum(t.onlyInTarget) }}</a><span v-else class="text-muted">0</span></td>
                <td class="text-end text-success">{{ fmtNum(t.identical) }}</td>
                <td class="text-end"><a v-if="t.different" href="#" class="text-danger fw-semibold" @click.prevent="openDetail(t, 'DIFFERENT')">{{ fmtNum(t.different) }}</a><span v-else class="text-muted">0</span>
                  <div v-if="t.different && t.fillable != null" class="help">{{ fmtNum(t.fillable) }} bisa saling mengisi</div></td>
              </tr>
            </tbody></table></div>
          <div v-if="!visibleTables.length" class="alert alert-success py-2 mt-2 mb-0"><i class="bi bi-check-circle me-1"></i>Semua tabel sudah identik di kedua database — tidak ada yang perlu disalin.</div>
          <div class="help mt-1">Klik angka untuk melihat baris yang bersangkutan.
            <template v-if="hiddenCount > 0 || showAllTables"> · <a href="#" @click.prevent="showAllTables = !showAllTables">{{ showAllTables ? 'sembunyikan tabel yang sudah identik' : `tampilkan ${hiddenCount} tabel yang sudah identik/kosong` }}</a></template></div>

          <!-- 3. model penyelesaian -->
          <div v-if="tot.diff > 0" class="mt-3">
            <div class="alert alert-warning py-2 small mb-2"><i class="bi bi-exclamation-triangle me-1"></i>
              Ada <strong>{{ fmtNum(tot.diff) }} baris</strong> yang ID-nya sama di kedua database tetapi isinya <strong>tidak sama</strong>. Pilih cara menanganinya:</div>
            <div v-if="mode === 'BOTH'" class="d-grid gap-2">
              <label class="check-item" style="cursor: pointer"><input v-model="differentPolicy" type="radio" value="CONFLICT" class="form-check-input mt-0"><div><div class="fw-semibold">Putuskan manual (aman)</div><div class="help">Tidak ada yang ditimpa. Tiap baris jadi konflik di menu Konflik; di sana Anda bisa memilih satu per satu atau “selesaikan semua”.</div></div></label>
              <label class="check-item" style="cursor: pointer"><input v-model="differentPolicy" type="radio" value="FILL_EMPTY" class="form-check-input mt-0">
                <div class="flex-grow-1"><div class="fw-semibold">Saling mengisi kolom yang kosong <span v-if="tot.fill != null" class="badge text-bg-info ms-1">{{ fmtNum(tot.fill) }} baris bisa</span></div>
                  <div class="help">Kolom yang kosong di satu sisi diisi dari sisi lain, sehingga kedua sisi menjadi lengkap. Cocok bila perbedaannya hanya “kosong vs terisi”.</div>
                  <div v-if="differentPolicy === 'FILL_EMPTY'" class="mt-2 d-flex align-items-center gap-2 flex-wrap"><span class="help">Bila ada kolom yang sama-sama terisi tapi beda:</span>
                    <select v-model="fillFallback" class="form-select form-select-sm" style="width: auto"><option value="CONFLICT">jadikan konflik</option><option value="A_WINS">pakai data A</option><option value="B_WINS">pakai data B</option></select></div></div></label>
              <label class="check-item" style="cursor: pointer"><input v-model="differentPolicy" type="radio" value="A_WINS" class="form-check-input mt-0"><div><div class="fw-semibold">Pakai data A untuk semua</div><div class="help text-danger">Isi di B untuk baris-baris ini akan diganti data A.</div></div></label>
              <label class="check-item" style="cursor: pointer"><input v-model="differentPolicy" type="radio" value="B_WINS" class="form-check-input mt-0"><div><div class="fw-semibold">Pakai data B untuk semua</div><div class="help text-danger">Isi di A untuk baris-baris ini akan diganti data B.</div></div></label>
            </div>
            <div v-else class="d-grid gap-2">
              <label class="check-item" style="cursor: pointer"><input v-model="overwriteMode" type="radio" value="SKIP_EXISTING" class="form-check-input mt-0"><div><div class="fw-semibold">Biarkan data yang sudah ada di {{ tgtName }} (aman)</div><div class="help">Hanya baris baru yang disalin.</div></div></label>
              <label class="check-item" style="cursor: pointer"><input v-model="overwriteMode" type="radio" value="OVERWRITE" class="form-check-input mt-0"><div><div class="fw-semibold">Timpa dengan data {{ srcName }}</div><div class="help text-danger">{{ fmtNum(tot.diff) }} baris di {{ tgtName }} diganti dan isi lamanya hilang.</div></div></label>
            </div>
          </div>

          <!-- 4. jalankan -->
          <div class="alert mt-3 mb-2 py-2" :class="danger ? 'alert-danger' : 'alert-info'"><strong>Yang akan terjadi:</strong> {{ summary }}.</div>
          <button class="btn w-100" :class="danger ? 'btn-danger' : 'btn-primary'" :disabled="starting || disabled" @click="start">
            <span v-if="starting" class="spinner-border spinner-border-sm"></span><i v-else class="bi bi-play-fill"></i> 3. Jalankan</button>
        </div>
      </div>

      <div><a href="#" class="small" @click.prevent="showAdv = !showAdv"><i :class="`bi bi-chevron-${showAdv ? 'down' : 'right'}`"></i> Opsi lanjutan</a>
        <div v-if="showAdv" class="mt-2 d-grid gap-2">
          <div><label class="form-label">Tabel tertentu saja (kosong = semua tabel terpilih)</label>
            <select v-model="selected" multiple class="form-select form-select-sm" size="5"><option v-for="r in rules" :key="r.id" :value="r.id">{{ r.endpointATable }} — {{ r.initialSyncStatus }}</option></select></div>
          <div class="form-check"><input id="rs2" v-model="restart" class="form-check-input" type="checkbox"><label for="rs2" class="form-check-label">Mulai ulang dari awal (abaikan checkpoint; error lama dibersihkan)</label></div>
        </div></div>
    </div>

    <!-- rincian baris -->
    <div v-if="detail" class="modal-backdrop-custom" @click.self="detail = null">
      <div class="modal-panel wide">
        <div class="p-3 border-bottom"><strong>{{ detail.table }}</strong> — {{ kindTitle[detail.kind] }}</div>
        <div class="p-3" style="max-height: 60vh; overflow: auto">
          <CommonSkeleton v-if="detailLoading" :rows="4" />
          <template v-else>
            <div class="help mb-2">Menampilkan {{ detail.items.length }} dari {{ fmtNum(detail.total) }} baris. ∅ = kosong (NULL).</div>
            <table v-if="detail.kind !== 'DIFFERENT'" class="table table-sm"><thead><tr><th v-for="c in rowCols" :key="c">{{ c }}</th></tr></thead>
              <tbody><tr v-for="(i, k) in detail.items" :key="k"><td v-for="c in rowCols" :key="c" class="mono small">{{ cell(i.row[c]) }}</td></tr></tbody></table>
            <table v-else class="table table-sm"><thead><tr><th>ID</th><th>Kolom</th><th class="diff-a">Di A</th><th class="diff-b">Di B</th><th></th></tr></thead>
              <tbody><template v-for="(i, k) in detail.items" :key="k"><tr v-for="(d, j) in i.diffs" :key="j">
                <td class="mono small">{{ j === 0 ? Object.values(i.pk).join(', ') : '' }}</td><td class="mono small">{{ d.column }}</td>
                <td class="small">{{ cell(d.a) }}</td><td class="small">{{ cell(d.b) }}</td>
                <td class="small"><span v-if="j === 0" class="status" :class="i.fillable ? 's-ok' : 's-warn'">{{ i.fillable ? 'bisa saling mengisi' : 'ada kolom bentrok' }}</span></td></tr></template></tbody></table>
          </template>
        </div>
        <div class="p-3 border-top text-end"><button class="btn btn-light" @click="detail = null">Tutup</button></div>
      </div>
    </div>
  </div>
</template>
