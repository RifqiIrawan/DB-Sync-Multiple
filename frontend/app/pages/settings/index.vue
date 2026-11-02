<script setup lang="ts">
const api = useApi(); const toast = useToast(); const auth = useAuth(); const config = useRuntimeConfig()
const f = ref({ currentPassword: '', newPassword: '', confirm: '' }); const busy = ref(false)
const health = ref<string>('...')
onMounted(async () => { try { await $fetch(`${config.public.apiBase}/health/db`); health.value = 'OK — API & database konfigurasi terhubung' } catch { health.value = 'Tidak dapat dijangkau' } })
async function change() {
  if (f.value.newPassword !== f.value.confirm) return toast.warn('Konfirmasi password tidak sama')
  busy.value = true
  try { await api.post('/auth/change-password', { currentPassword: f.value.currentPassword, newPassword: f.value.newPassword }); toast.success('Password diubah; silakan login ulang'); await auth.logout() } finally { busy.value = false }
}
</script>
<template>
  <div>
    <CommonPageHeader title="Pengaturan" icon="gear" />
    <div class="row g-3">
      <div class="col-lg-5"><div class="card"><div class="card-header">Ganti password</div><form class="card-body" @submit.prevent="change">
        <div class="mb-2"><label class="form-label">Password saat ini</label><input v-model="f.currentPassword" type="password" class="form-control" required autocomplete="current-password"></div>
        <div class="mb-2"><label class="form-label">Password baru (min. 10 karakter)</label><input v-model="f.newPassword" type="password" class="form-control" required minlength="10" autocomplete="new-password"></div>
        <div class="mb-3"><label class="form-label">Konfirmasi</label><input v-model="f.confirm" type="password" class="form-control" required autocomplete="new-password"></div>
        <button class="btn btn-primary" :disabled="busy">Ubah password</button></form></div></div>
      <div class="col-lg-7"><div class="card"><div class="card-header">Informasi sistem</div><div class="card-body">
        <dl class="row mb-0"><dt class="col-4">Versi</dt><dd class="col-8">DBSync Platform 1.0</dd>
          <dt class="col-4">API</dt><dd class="col-8 mono">{{ config.public.apiBase }}</dd><dt class="col-4">Status API</dt><dd class="col-8">{{ health }}</dd>
          <dt class="col-4">User</dt><dd class="col-8">{{ auth.user.value?.username }} ({{ auth.user.value?.roles.join(', ') }})</dd></dl></div></div></div>
    </div>
  </div>
</template>
