<script setup lang="ts">
const auth = useAuth()
const route = useRoute()
const links = computed(() => [
  { section: 'Overview' },
  { to: '/dashboard', icon: 'speedometer2', label: 'Dashboard' },
  { section: 'Database Connections' },
  { to: '/connections', icon: 'hdd-network', label: 'Connections' },
  { section: 'Synchronization' },
  { to: '/sync-groups', icon: 'diagram-2', label: 'Synchronization Groups' },
  { to: '/sync-groups/new', icon: 'plus-circle', label: 'Create Group', hide: !auth.canManage.value },
  { section: 'Jobs' },
  { to: '/scheduler', icon: 'clock-history', label: 'Initial / Scheduled / Running' },
  { section: 'Monitoring' },
  { to: '/monitoring', icon: 'activity', label: 'Live Monitoring' },
  { to: '/conflicts', icon: 'exclamation-diamond', label: 'Conflict Management' },
  { to: '/logs', icon: 'list-check', label: 'Event Queue & Failed' },
  { to: '/workers', icon: 'cpu', label: 'Worker Status' },
  { section: 'History' },
  { to: '/history', icon: 'journal-text', label: 'Sync History' },
  { to: '/audit', icon: 'shield-check', label: 'Audit Logs', hide: !auth.canAudit.value },
  { section: 'Administration', hide: !auth.isSuper.value },
  { to: '/users', icon: 'people', label: 'Users & Roles', hide: !auth.isSuper.value },
  { to: '/settings', icon: 'gear', label: 'System Settings' },
].filter(l => !l.hide))
</script>
<template>
  <div class="layout">
    <aside class="sidebar">
      <div class="brand"><i class="bi bi-arrow-left-right me-2"></i>DBSync</div>
      <template v-for="(l, i) in links" :key="i">
        <div v-if="'section' in l" class="section">{{ l.section }}</div>
        <NuxtLink v-else :to="l.to" class="nav-link" :exact-active-class="route.path === l.to ? 'router-link-active' : ''">
          <i :class="`bi bi-${l.icon}`"></i><span>{{ l.label }}</span>
        </NuxtLink>
      </template>
    </aside>
    <div class="main">
      <div class="topbar">
        <span class="text-muted small">Platform sinkronisasi database lintas vendor</span>
        <div class="d-flex align-items-center gap-3">
          <span class="small"><i class="bi bi-person-circle me-1"></i>{{ auth.user.value?.username }}
            <span class="badge text-bg-light border ms-1">{{ auth.user.value?.roles.join(', ') }}</span></span>
          <button class="btn btn-sm btn-outline-secondary" @click="auth.logout()"><i class="bi bi-box-arrow-right"></i> Logout</button>
        </div>
      </div>
      <div class="content"><slot /></div>
    </div>
  </div>
</template>
