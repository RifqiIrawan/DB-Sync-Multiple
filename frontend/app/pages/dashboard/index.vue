<script setup lang="ts">
const { data: d, live } = useMonitor()
const auth = useAuth()
const hour = new Date().getHours()
const greet = hour < 11 ? 'Selamat pagi' : hour < 15 ? 'Selamat siang' : hour < 18 ? 'Selamat sore' : 'Selamat malam'

const attention = computed(() => {
  if (!d.value) return []
  const a: { icon: string; text: string; to: string; cta: string }[] = []
  if (d.value.pendingConflicts) a.push({ icon: 'exclamation-diamond', text: `${d.value.pendingConflicts} konflik menunggu keputusan Anda`, to: '/conflicts', cta: 'Selesaikan' })
  if (d.value.events.deadLetter) a.push({ icon: 'x-octagon', text: `${d.value.events.deadLetter} event gagal permanen (dead letter)`, to: '/logs', cta: 'Tinjau & coba ulang' })
  if (d.value.groups.error) a.push({ icon: 'bug', text: `${d.value.groups.error} grup berstatus error`, to: '/sync-groups', cta: 'Lihat grup' })
  if (d.value.connections.disconnected) a.push({ icon: 'plug', text: `${d.value.connections.disconnected} koneksi terputus`, to: '/connections', cta: 'Periksa koneksi' })
  if (!d.value.workers.some((w: any) => w.online)) a.push({ icon: 'cpu', text: 'Tidak ada worker yang online — job tidak akan berjalan', to: '/workers', cta: 'Cek worker' })
  return a
})
const hero = computed(() => attention.value.length
  ? { c: 'var(--warn)', icon: 'exclamation-triangle', title: 'Ada hal yang perlu perhatian Anda', sub: 'Selesaikan item di bawah agar sinkronisasi tetap sehat.' }
  : { c: 'var(--ok)', icon: 'check2-circle', title: 'Semua sistem berjalan normal', sub: `${d.value?.groups.active ?? 0} grup aktif · ${d.value?.workers.filter((w: any) => w.online).length ?? 0} worker online` })

const steps = computed(() => {
  const x = d.value; if (!x) return []
  const initialDone = x.recentRuns.some((r: any) => r.runType === 'INITIAL' && r.status === 'SUCCESS')
  return [
    { done: x.connections.total >= 2, title: 'Tambahkan dua koneksi database', text: 'Sumber (A) dan tujuan (B) — MySQL, PostgreSQL, atau SQL Server.', to: '/connections', cta: 'Tambah koneksi' },
    { done: x.groups.total > 0, title: 'Buat grup sinkronisasi', text: 'Pilih tabel, arah, dan mode lewat wizard 6 langkah.', to: '/sync-groups/new', cta: 'Buat grup' },
    { done: initialDone, title: 'Jalankan initial sync', text: 'Salin data awal ke database tujuan.', to: '/sync-groups', cta: 'Buka grup' },
    { done: x.groups.active > 0, title: 'Aktifkan grup', text: 'Setelah aktif, perubahan disinkronkan otomatis.', to: '/sync-groups', cta: 'Aktifkan' },
  ]
})
const onboardingDone = computed(() => steps.value.length > 0 && steps.value.every(s => s.done))

const kpis = computed(() => !d.value ? [] : [
  { label: 'Koneksi', v: d.value.connections.total, sub: `${d.value.connections.connected} terhubung`, icon: 'hdd-network', t: 'a' },
  { label: 'Grup aktif', v: d.value.groups.active, sub: `dari ${d.value.groups.total} grup · ${d.value.groups.cdc} CDC`, icon: 'diagram-2', t: 'lime' },
  { label: 'Event sukses', v: d.value.events.processed, sub: `${fmtNum(d.value.events.processed24h)} dalam 24 jam`, icon: 'check2-circle', t: 'ok' },
  { label: 'Menunggu / retry', v: d.value.events.pending + d.value.events.retry, sub: 'dalam antrean', icon: 'hourglass-split', t: 'warn' },
  { label: 'Dead letter', v: d.value.events.deadLetter, sub: 'butuh tindakan', icon: 'x-octagon', t: d.value.events.deadLetter ? 'bad' : 'ok' },
  { label: 'Konflik', v: d.value.pendingConflicts, sub: 'menunggu resolusi', icon: 'exclamation-diamond', t: d.value.pendingConflicts ? 'bad' : 'ok' },
])
const feedIcon = (s: string) => s === 'SUCCESS' ? ['bi-check-lg', 's-ok'] : s === 'FAILED' ? ['bi-x-lg', 's-bad'] : s === 'RUNNING' ? ['bi-arrow-repeat', 's-info'] : s === 'PARTIAL' ? ['bi-exclamation-lg', 's-warn'] : ['bi-clock', 's-mute']
const typeName: Record<string, string> = { INITIAL: 'Initial sync', SCHEDULED: 'Sinkron terjadwal', MANUAL: 'Sinkron manual' }
</script>
<template>
  <div>
    <CommonPageHeader :title="`${greet}, ${auth.user.value?.username ?? ''} 👋`" subtitle="Ringkasan kesehatan seluruh sinkronisasi Anda">
      <span class="status" :class="live ? 's-ok live' : 's-mute'"><span class="dot"></span>{{ live ? 'Live' : 'Polling' }}</span>
      <NuxtLink v-if="auth.canManage.value" to="/sync-groups/new" class="btn btn-primary"><i class="bi bi-plus-lg"></i> Grup baru</NuxtLink>
    </CommonPageHeader>

    <div v-if="!d" class="row g-3"><div class="col-12"><div class="card"><CommonSkeleton :rows="3" height="2rem" /></div></div>
      <div v-for="i in 6" :key="i" class="col-6 col-lg-4"><div class="card"><CommonSkeleton :rows="2" /></div></div></div>

    <div v-else class="rise">
      <div class="hero-health mb-3" :style="{ '--hc': hero.c }">
        <div class="orb"><i :class="`bi bi-${hero.icon}`"></i></div>
        <div class="flex-grow-1"><h4 class="mb-0">{{ hero.title }}</h4><div class="text-muted">{{ hero.sub }}</div></div>
      </div>

      <div v-if="attention.length" class="card mb-3"><div class="card-body py-2">
        <div v-for="a in attention" :key="a.text" class="d-flex align-items-center gap-3 py-2">
          <span class="status s-warn" style="padding: .5rem"><i :class="`bi bi-${a.icon}`"></i></span><span class="flex-grow-1 fw-medium">{{ a.text }}</span>
          <NuxtLink :to="a.to" class="btn btn-sm btn-outline-primary">{{ a.cta }} <i class="bi bi-arrow-right"></i></NuxtLink></div>
      </div></div>

      <div v-if="!onboardingDone" class="card mb-3"><div class="card-header d-flex justify-content-between"><span><i class="bi bi-rocket-takeoff me-2"></i>Mulai dalam 4 langkah</span>
        <span class="help">{{ steps.filter(s => s.done).length }}/4 selesai</span></div>
        <div class="card-body d-grid gap-2">
          <div v-for="(s, i) in steps" :key="s.title" class="check-item" :class="{ done: s.done }">
            <span class="box"><i v-if="s.done" class="bi bi-check-lg"></i><span v-else style="font-size: .7rem; font-weight: 800">{{ i + 1 }}</span></span>
            <div class="flex-grow-1"><div class="fw-semibold">{{ s.title }}</div><div class="help">{{ s.text }}</div></div>
            <NuxtLink v-if="!s.done" :to="s.to" class="btn btn-sm btn-primary">{{ s.cta }}</NuxtLink></div>
        </div></div>

      <div class="row g-3 mb-3 rise">
        <div v-for="k in kpis" :key="k.label" class="col-6 col-md-4 col-xl-2">
          <div class="card kpi h-100" :class="`tone-${k.t}`"><div class="card-body">
            <div class="kpi-ico"><i :class="`bi bi-${k.icon}`"></i></div>
            <div class="stat"><CommonAnimatedNumber :value="k.v" /></div><div class="stat-label">{{ k.label }}</div><div class="help mt-1">{{ k.sub }}</div>
          </div></div></div>
      </div>

      <div class="row g-3">
        <div class="col-lg-7 d-grid gap-3 align-content-start">
          <div class="card" v-if="d.runningRuns.length"><div class="card-header"><span class="status s-info live me-2"><span class="dot"></span>Berjalan</span>Job aktif</div>
            <div class="card-body d-grid gap-3">
              <div v-for="r in d.runningRuns" :key="r.id">
                <div class="d-flex justify-content-between align-items-center mb-1"><NuxtLink :to="`/sync-groups/${r.syncGroupId}`" class="fw-semibold text-body">{{ r.groupName }}</NuxtLink>
                  <span><span class="dbchip me-1">{{ typeName[r.runType] ?? r.runType }}</span><CommonStatusBadge :status="r.status" /></span></div>
                <div v-for="(i, k) in r.items" :key="k" class="mb-2">
                  <div class="d-flex justify-content-between help"><span class="mono">{{ i.tableLabel }}</span><span>{{ fmtNum(i.recordsRead) }}<template v-if="i.totalRows && r.runType === 'INITIAL'"> / {{ fmtNum(i.totalRows) }}</template> baris</span></div>
                  <div class="progress" style="height: 7px" v-if="i.totalRows && r.runType === 'INITIAL'"><div class="progress-bar progress-bar-striped progress-bar-animated" :style="{ width: Math.min(100, 100 * i.recordsRead / i.totalRows) + '%' }"></div></div>
                </div></div>
            </div></div>

          <div class="card"><div class="card-header d-flex justify-content-between">Aktivitas terbaru<NuxtLink to="/history" class="btn btn-sm btn-light">Semua riwayat</NuxtLink></div>
            <div class="card-body py-2">
              <CommonEmptyState v-if="!d.recentRuns.length" icon="journal-text" title="Belum ada aktivitas" text="Aktivitas sinkronisasi akan muncul di sini setelah job pertama berjalan." />
              <NuxtLink v-for="r in d.recentRuns" :key="r.id" :to="`/history/${r.id}`" class="feed-item text-body">
                <span class="ic" :class="feedIcon(r.status)[1]"><i class="bi" :class="feedIcon(r.status)[0]"></i></span>
                <div class="flex-grow-1"><div class="fw-semibold">{{ r.groupName }} <span class="text-muted fw-normal">· {{ typeName[r.runType] ?? r.runType }}</span></div>
                  <div class="help">{{ fmtDate(r.startedAt ?? r.queuedAt) }} · {{ fmtNum(r.recordsRead) }} baris dibaca<span v-if="r.recordsFailed" class="text-danger"> · {{ r.recordsFailed }} gagal</span></div></div>
                <CommonStatusBadge :status="r.status" /></NuxtLink>
            </div></div>
        </div>
        <div class="col-lg-5 d-grid gap-3 align-content-start">
          <div class="card"><div class="card-header">Kesehatan worker</div>
            <div class="card-body py-2">
              <div v-for="w in d.workers" :key="w.workerId" class="feed-item align-items-center">
                <span class="ic" :class="w.online ? 's-ok' : 's-mute'"><i class="bi bi-cpu"></i></span>
                <div class="flex-grow-1"><div class="fw-semibold">{{ w.workerType === 'CDC' ? 'CDC consumer' : 'Sync worker' }}</div><div class="help">{{ w.hostname }} · {{ fmtDate(w.lastHeartbeatAt) }}</div></div>
                <CommonStatusBadge :status="w.online ? 'ACTIVE' : (w.status === 'RUNNING' ? 'STALE' : w.status)" /></div>
              <CommonEmptyState v-if="!d.workers.length" icon="cpu" title="Belum ada worker" text="Jalankan DBSync.Worker, atau set Worker:Embedded=true pada API." />
            </div></div>
          <div class="card"><div class="card-header">Pintasan</div><div class="card-body d-grid gap-2">
            <NuxtLink to="/conflicts" class="btn btn-light justify-content-start"><i class="bi bi-exclamation-diamond"></i> Selesaikan konflik</NuxtLink>
            <NuxtLink to="/logs" class="btn btn-light justify-content-start"><i class="bi bi-list-check"></i> Lihat event gagal</NuxtLink>
            <NuxtLink to="/connections" class="btn btn-light justify-content-start"><i class="bi bi-hdd-network"></i> Kelola koneksi</NuxtLink>
            <div class="help mt-1">Tips: tekan <kbd>Ctrl</kbd> <kbd>K</kbd> untuk mencari apa saja.</div>
          </div></div>
        </div>
      </div>
    </div>
  </div>
</template>
