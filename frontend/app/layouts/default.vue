<script setup lang="ts">
const auth = useAuth(); const route = useRoute(); const api = useApi()
const theme = useTheme(); const cmd = useCommandPalette()
const collapsed = ref(false); const drawer = ref(false); const userMenu = ref(false)
const dash = ref<any>(null); const apiDown = ref(false)

onMounted(() => {
  theme.init()
  collapsed.value = localStorage.getItem('dbsync.collapsed') === '1'
  poll(); const t = setInterval(poll, 8000); onBeforeUnmount(() => clearInterval(t))
})
watch(collapsed, v => localStorage.setItem('dbsync.collapsed', v ? '1' : '0'))
watch(() => route.fullPath, () => { drawer.value = false; userMenu.value = false })
async function poll() {
  try { dash.value = await api.get('/monitoring/dashboard', undefined, true); apiDown.value = false } catch { apiDown.value = true }
}

const conflicts = computed(() => dash.value?.pendingConflicts ?? 0)
const failed = computed(() => (dash.value?.events?.deadLetter ?? 0))
const workersOnline = computed(() => (dash.value?.workers ?? []).some((w: any) => w.online))
const health = computed(() => {
  if (apiDown.value) return { cls: 's-bad', icon: 'wifi-off', text: 'API tidak terjangkau' }
  if (dash.value && !workersOnline.value) return { cls: 's-warn', icon: 'cpu', text: 'Worker offline' }
  if (conflicts.value || failed.value) return { cls: 's-warn', icon: 'exclamation-triangle', text: 'Perlu perhatian' }
  return { cls: 's-ok', icon: 'check2-circle', text: 'Sistem normal' }
})

const links = computed(() => [
  { section: 'Ringkasan' },
  { to: '/dashboard', icon: 'speedometer2', label: 'Dashboard' },
  { section: 'Sumber data' },
  { to: '/connections', icon: 'hdd-network', label: 'Koneksi Database' },
  { section: 'Sinkronisasi' },
  { to: '/sync-groups', icon: 'diagram-2', label: 'Grup Sinkronisasi', exact: true },
  { to: '/sync-groups/new', icon: 'plus-circle', label: 'Buat Grup Baru', hide: !auth.canManage.value },
  { to: '/scheduler', icon: 'clock-history', label: 'Job & Jadwal' },
  { section: 'Pemantauan' },
  { to: '/monitoring', icon: 'activity', label: 'Live Monitoring' },
  { to: '/conflicts', icon: 'exclamation-diamond', label: 'Konflik', badge: conflicts.value },
  { to: '/logs', icon: 'list-check', label: 'Antrean & Gagal', badge: failed.value },
  { to: '/workers', icon: 'cpu', label: 'Status Worker' },
  { section: 'Catatan' },
  { to: '/history', icon: 'journal-text', label: 'Riwayat' },
  { to: '/audit', icon: 'shield-check', label: 'Audit Log', hide: !auth.canAudit.value },
  { section: 'Admin', hide: !auth.isSuper.value },
  { to: '/users', icon: 'people', label: 'Pengguna & Role', hide: !auth.isSuper.value },
  { to: '/settings', icon: 'gear', label: 'Pengaturan' },
].filter(l => !l.hide))

const names: Record<string, string> = { dashboard: 'Dashboard', connections: 'Koneksi', 'sync-groups': 'Grup Sinkronisasi', new: 'Buat Grup', scheduler: 'Job & Jadwal', monitoring: 'Live Monitoring', conflicts: 'Konflik', logs: 'Antrean & Gagal', workers: 'Worker', history: 'Riwayat', audit: 'Audit Log', users: 'Pengguna', settings: 'Pengaturan', 'table-mapping': 'Mapping Kolom' }
const crumbs = computed(() => {
  const seg = route.path.split('/').filter(Boolean); let acc = ''
  return seg.map((s, i) => { acc += '/' + s; return { to: acc, label: names[s] ?? (s.length > 12 ? s.slice(0, 8) + '…' : s), last: i === seg.length - 1 } })
})
const initials = computed(() => (auth.user.value?.username ?? '?').slice(0, 2).toUpperCase())
</script>
<template>
  <div class="layout" :class="{ collapsed, drawer }">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark"><i class="bi bi-arrow-left-right"></i></span><span class="label">DBSync</span></div>
      <nav>
        <template v-for="(l, i) in links" :key="i">
          <div v-if="'section' in l" class="section">{{ l.section }}</div>
          <NuxtLink v-else :to="l.to" class="nav-link" :class="{ 'active-exact': l.exact && route.path === l.to }" :title="collapsed ? l.label : undefined"
            :exact-active-class="l.exact ? '' : undefined" :active-class="l.exact ? '' : 'router-link-active'">
            <i :class="`bi bi-${l.icon}`"></i><span class="label">{{ l.label }}</span>
            <span v-if="'badge' in l && l.badge" class="nav-badge">{{ l.badge }}</span>
          </NuxtLink>
        </template>
      </nav>
      <div class="sidebar-foot">
        <button class="nav-link w-100 border-0 bg-transparent d-flex gap-3 align-items-center" style="color: var(--side-text); padding: .55rem .8rem" @click="collapsed = !collapsed">
          <i :class="`bi bi-${collapsed ? 'chevron-double-right' : 'chevron-double-left'}`"></i><span class="label">Ciutkan menu</span>
        </button>
      </div>
    </aside>
    <div v-if="drawer" class="position-fixed top-0 start-0 w-100 h-100" style="background: rgba(0,0,0,.5); z-index: 35" @click="drawer = false"></div>

    <div class="main">
      <header class="topbar">
        <button class="icon-btn d-lg-none" aria-label="Menu" @click="drawer = !drawer"><i class="bi bi-list fs-5"></i></button>
        <div class="crumbs d-none d-md-flex">
          <NuxtLink to="/dashboard"><i class="bi bi-house-door"></i></NuxtLink>
          <template v-for="c in crumbs" :key="c.to"><i class="bi bi-chevron-right" style="font-size: .65rem"></i>
            <span v-if="c.last" class="text-body fw-semibold">{{ c.label }}</span><NuxtLink v-else :to="c.to">{{ c.label }}</NuxtLink></template>
        </div>
        <div class="flex-grow-1"></div>
        <div class="search-pill" role="button" @click="cmd.show()"><i class="bi bi-search"></i><span class="txt flex-grow-1">Cari halaman, grup, aksi…</span><kbd>Ctrl K</kbd></div>
        <span class="health-pill d-none d-sm-inline-flex" :class="health.cls" :title="`Worker online: ${workersOnline ? 'ya' : 'tidak'}`"><i :class="`bi bi-${health.icon}`"></i>{{ health.text }}</span>
        <button class="icon-btn" :title="theme.theme.value === 'dark' ? 'Tema terang' : 'Tema gelap'" @click="theme.toggle()"><i :class="`bi bi-${theme.theme.value === 'dark' ? 'sun' : 'moon-stars'}`"></i></button>
        <div class="position-relative">
          <button class="icon-btn" style="width: auto; padding: 0 .35rem 0 .35rem; gap: .5rem; display: inline-flex; align-items: center" @click="userMenu = !userMenu">
            <span class="brand-mark" style="width: 28px; height: 28px; font-size: .72rem; font-weight: 800; border-radius: 8px">{{ initials }}</span>
            <i class="bi bi-chevron-down pe-1" style="font-size: .7rem"></i>
          </button>
          <div v-if="userMenu" class="card position-absolute end-0 mt-2" style="width: 240px; z-index: 50; animation: pop .15s">
            <div class="p-3 border-bottom"><div class="fw-semibold">{{ auth.user.value?.username }}</div><div class="help">{{ auth.user.value?.email }}</div>
              <span v-for="r in auth.user.value?.roles" :key="r" class="badge text-bg-light border mt-2 me-1">{{ r }}</span></div>
            <NuxtLink to="/settings" class="d-block px-3 py-2 text-body"><i class="bi bi-key me-2"></i>Ganti password</NuxtLink>
            <a href="#" class="d-block px-3 py-2 text-body" @click.prevent="auth.logout()"><i class="bi bi-box-arrow-right me-2"></i>Keluar</a>
          </div>
        </div>
      </header>
      <main class="content"><slot /></main>
    </div>
    <CommonCommandPalette />
  </div>
</template>
