<script setup lang="ts">
definePageMeta({ layout: false })
const auth = useAuth()
const toast = useToast()
// Prefill hanya saat development (npm run dev); tidak ada di build production.
const username = ref(import.meta.dev ? 'admin' : '')
const password = ref(import.meta.dev ? 'Admin#12345' : '')
const busy = ref(false)
const error = ref('')

async function submit() {
  busy.value = true; error.value = ''
  try {
    await auth.login(username.value, password.value)
    await navigateTo('/dashboard')
  } catch (e: any) {
    error.value = e?.data?.message ?? 'Tidak dapat terhubung ke API. Pastikan backend berjalan.'
    toast.error('Login gagal', error.value)
  } finally { busy.value = false }
}
</script>
<template>
  <div class="login-wrap">
    <form class="card p-4" style="width: 380px" @submit.prevent="submit">
      <div class="text-center mb-3">
        <div class="fs-2"><i class="bi bi-arrow-left-right text-primary"></i></div>
        <h4 class="mb-0">DBSync Platform</h4>
        <div class="text-muted small">Masuk untuk mengelola sinkronisasi</div>
      </div>
      <div class="mb-3"><label class="form-label">Username</label>
        <input v-model="username" class="form-control" autocomplete="username" required autofocus></div>
      <div class="mb-3"><label class="form-label">Password</label>
        <input v-model="password" type="password" class="form-control" autocomplete="current-password" required></div>
      <div v-if="error" class="alert alert-danger py-2 small">{{ error }}</div>
      <button class="btn btn-primary w-100" :disabled="busy">{{ busy ? 'Memproses...' : 'Login' }}</button>
    </form>
  </div>
</template>
