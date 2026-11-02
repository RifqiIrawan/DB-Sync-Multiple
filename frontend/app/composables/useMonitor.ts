import * as signalR from '@microsoft/signalr'

/**
 * Data dashboard real-time via SignalR (push tiap 3 detik). SignalR bukan sumber kebenaran:
 * jika koneksi terputus/tidak tersedia, composable ini otomatis polling REST API.
 */
export function useMonitor() {
  const config = useRuntimeConfig()
  const auth = useAuth()
  const api = useApi()
  const data = ref<any>(null)
  const live = ref(false)
  let conn: signalR.HubConnection | null = null
  let timer: any = null

  async function poll() {
    try { data.value = await api.get('/monitoring/dashboard', undefined, true) } catch { /* diam */ }
  }

  async function start() {
    await poll()
    try {
      conn = new signalR.HubConnectionBuilder()
        .withUrl(`${config.public.apiBase}/hubs/monitor`, { accessTokenFactory: () => auth.token.value ?? '' })
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build()
      conn.on('dashboard', (d: any) => { data.value = d; live.value = true })
      conn.onreconnecting(() => { live.value = false })
      conn.onreconnected(() => { live.value = true })
      conn.onclose(() => { live.value = false })
      await conn.start(); live.value = true
    } catch { live.value = false }
    timer = setInterval(() => { if (!live.value) poll() }, 5000)
  }

  function stop() {
    if (timer) clearInterval(timer)
    conn?.stop()
  }

  onMounted(start)
  onBeforeUnmount(stop)
  return { data, live, refresh: poll }
}

/** Polling generik yang berhenti saat komponen dilepas. */
export function usePolling(fn: () => any, ms = 3000) {
  let t: any
  onMounted(() => { fn(); t = setInterval(fn, ms) })
  onBeforeUnmount(() => clearInterval(t))
}
