<script setup lang="ts">
definePageMeta({ layout: false })
const auth = useAuth(); const toast = useToast(); const theme = useTheme()
onMounted(() => theme.init())
// Prefill hanya saat development (npm run dev); tidak ada di build production.
const username = ref(import.meta.dev ? 'admin' : '')
const password = ref(import.meta.dev ? 'Admin#12345' : '')
const busy = ref(false); const error = ref(''); const showPw = ref(false)

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
const feats = [
  ['bi-lightning-charge', 'Real-time CDC', 'Perubahan tiba dalam hitungan detik lewat Debezium & Kafka.'],
  ['bi-shuffle', 'Dua arah, tanpa loop', 'Baseline per baris meredam echo; konflik ditampung, bukan ditimpa.'],
  ['bi-shield-lock', 'Aman by default', 'Password terenkripsi AES-256, RBAC, dan audit lengkap.'],
]
</script>
<template>
  <div class="login-wrap">
    <section class="login-art">
      <div class="d-flex align-items-center gap-2 fs-4 fw-bold display"><span class="brand-mark"><i class="bi bi-arrow-left-right"></i></span>DBSync</div>
      <div class="rise">
        <h1 class="display">Sinkronkan<br>database apa pun.<br><em>Tanpa drama.</em></h1>
        <div class="orbit">
          <div class="db"><span class="ep ep-a me-2">A</span>MySQL</div>
          <svg viewBox="0 0 120 30"><path class="rail" d="M2 15 H118" /><path class="packet" d="M2 15 H118" /></svg>
          <div class="db"><span class="ep ep-b me-2">B</span>PostgreSQL</div>
        </div>
        <div class="d-grid gap-3 mt-5" style="max-width: 460px">
          <div v-for="f in feats" :key="f[1]" class="d-flex gap-3"><i :class="`bi ${f[0]} fs-4`" style="color: var(--lime)"></i>
            <div><div class="fw-semibold">{{ f[1] }}</div><div style="color: #9aa3b5; font-size: .88rem">{{ f[2] }}</div></div></div>
        </div>
      </div>
      <div style="color: #6b7488; font-size: .8rem">MySQL · PostgreSQL · SQL Server</div>
    </section>

    <section class="login-form">
      <form style="width: 100%; max-width: 390px" class="rise" @submit.prevent="submit">
        <div>
          <h2 class="display mb-1" style="font-size: 2rem">Selamat datang</h2>
          <p class="text-muted mb-4">Masuk untuk mengelola sinkronisasi database Anda.</p>
        </div>
        <div class="mb-3"><label class="form-label">Username</label>
          <div class="input-group"><span class="input-group-text"><i class="bi bi-person"></i></span>
            <input v-model="username" class="form-control" autocomplete="username" required autofocus></div></div>
        <div class="mb-3"><label class="form-label">Password</label>
          <div class="input-group"><span class="input-group-text"><i class="bi bi-lock"></i></span>
            <input v-model="password" :type="showPw ? 'text' : 'password'" class="form-control" autocomplete="current-password" required>
            <button type="button" class="btn btn-light" tabindex="-1" @click="showPw = !showPw"><i :class="`bi bi-eye${showPw ? '-slash' : ''}`"></i></button></div></div>
        <div v-if="error" class="alert alert-danger py-2 small"><i class="bi bi-exclamation-circle me-1"></i>{{ error }}</div>
        <button class="btn btn-primary w-100 py-2 mt-1" :disabled="busy"><span v-if="busy" class="spinner-border spinner-border-sm"></span>{{ busy ? 'Memproses…' : 'Masuk' }} <i v-if="!busy" class="bi bi-arrow-right"></i></button>
        <div class="d-flex justify-content-between align-items-center mt-4">
          <span class="help">Mode dev: akun terisi otomatis.</span>
          <button type="button" class="icon-btn" @click="theme.toggle()"><i :class="`bi bi-${theme.theme.value === 'dark' ? 'sun' : 'moon-stars'}`"></i></button>
        </div>
      </form>
    </section>
  </div>
</template>
