<script setup lang="ts">
// Palet perintah (Ctrl/⌘+K): lompat ke halaman atau grup mana pun, ganti tema, logout — tanpa menghafal menu.
const { open, hide } = useCommandPalette()
const auth = useAuth(); const api = useApi(); const theme = useTheme(); const router = useRouter()
const q = ref(''); const sel = ref(0); const inputEl = ref<HTMLInputElement>()
const groups = ref<any[]>([])

const pages = computed(() => [
  { g: 'Halaman', icon: 'speedometer2', label: 'Dashboard', to: '/dashboard', k: 'beranda home' },
  { g: 'Halaman', icon: 'hdd-network', label: 'Koneksi Database', to: '/connections', k: 'connection mysql postgres sql server' },
  { g: 'Halaman', icon: 'diagram-2', label: 'Grup Sinkronisasi', to: '/sync-groups', k: 'group sync' },
  ...(auth.canManage.value ? [{ g: 'Aksi', icon: 'plus-circle', label: 'Buat grup sinkronisasi baru', to: '/sync-groups/new', k: 'tambah create wizard' }] : []),
  { g: 'Halaman', icon: 'clock-history', label: 'Job & Jadwal', to: '/scheduler', k: 'initial scheduled running' },
  { g: 'Halaman', icon: 'activity', label: 'Pemantauan Live', to: '/monitoring', k: 'monitor realtime' },
  { g: 'Halaman', icon: 'exclamation-diamond', label: 'Konflik', to: '/conflicts', k: 'conflict resolve' },
  { g: 'Halaman', icon: 'list-check', label: 'Antrean Event & Gagal', to: '/logs', k: 'event dead letter retry' },
  { g: 'Halaman', icon: 'cpu', label: 'Status Worker', to: '/workers', k: 'worker heartbeat' },
  { g: 'Halaman', icon: 'journal-text', label: 'Riwayat Sinkronisasi', to: '/history', k: 'history run' },
  ...(auth.canAudit.value ? [{ g: 'Halaman', icon: 'shield-check', label: 'Audit Log', to: '/audit', k: 'audit' }] : []),
  ...(auth.isSuper.value ? [{ g: 'Halaman', icon: 'people', label: 'Pengguna & Role', to: '/users', k: 'user role' }] : []),
  { g: 'Halaman', icon: 'gear', label: 'Pengaturan', to: '/settings', k: 'password settings' },
  ...groups.value.map(x => ({ g: 'Grup', icon: 'diagram-2', label: x.groupName, to: `/sync-groups/${x.id}`, k: `${x.endpointA?.connectionName} ${x.endpointB?.connectionName}` })),
  { g: 'Aksi', icon: theme.theme.value === 'dark' ? 'sun' : 'moon-stars', label: theme.theme.value === 'dark' ? 'Ganti ke tema terang' : 'Ganti ke tema gelap', run: () => theme.toggle(), k: 'tema dark light mode' },
  { g: 'Aksi', icon: 'box-arrow-right', label: 'Keluar (logout)', run: () => auth.logout(), k: 'logout keluar' },
] as any[])

const results = computed(() => {
  const s = q.value.trim().toLowerCase()
  return pages.value.filter(p => !s || `${p.label} ${p.k ?? ''}`.toLowerCase().includes(s)).slice(0, 12)
})
watch(q, () => { sel.value = 0 })
watch(open, async v => {
  if (v) {
    q.value = ''; sel.value = 0; await nextTick(); inputEl.value?.focus()
    try { groups.value = await api.get('/sync-groups', undefined, true) } catch { /* abaikan */ }
  }
})
function go(p: any) { hide(); if (p.run) p.run(); else router.push(p.to) }
function onKey(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); open.value = !open.value; return }
  if (!open.value) return
  if (e.key === 'Escape') hide()
  else if (e.key === 'ArrowDown') { e.preventDefault(); sel.value = (sel.value + 1) % Math.max(1, results.value.length) }
  else if (e.key === 'ArrowUp') { e.preventDefault(); sel.value = (sel.value - 1 + results.value.length) % Math.max(1, results.value.length) }
  else if (e.key === 'Enter' && results.value[sel.value]) go(results.value[sel.value])
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
const showGroup = (g: string, i: number) => { const r = results.value[i - 1]; return !r || r.g !== g }
</script>
<template>
  <div v-if="open" class="modal-backdrop-custom" style="padding-top: 12vh" @click.self="hide">
    <div class="cmd-box">
      <input ref="inputEl" v-model="q" placeholder="Cari halaman, grup, atau aksi…  (↑ ↓ Enter · Esc untuk menutup)">
      <div style="max-height: 55vh; overflow-y: auto; padding-bottom: .5rem">
        <template v-for="(r, i) in results" :key="r.label + i">
          <div v-if="showGroup(r.g, i)" class="cmd-group">{{ r.g }}</div>
          <div class="cmd-item" :class="{ sel: i === sel }" @mouseenter="sel = i" @click="go(r)"><i :class="`bi bi-${r.icon}`"></i><span>{{ r.label }}</span></div>
        </template>
        <div v-if="!results.length" class="text-center text-muted py-4">Tidak ada hasil untuk “{{ q }}”.</div>
      </div>
    </div>
  </div>
</template>
