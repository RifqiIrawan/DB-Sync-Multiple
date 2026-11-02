export interface AuthUser { id: string; username: string; email: string; roles: string[] }

const KEY = 'dbsync.auth'

export function useAuth() {
  const config = useRuntimeConfig()
  const state = useState<{ token: string | null; refresh: string | null; user: AuthUser | null }>('auth', () => {
    if (import.meta.client) {
      try { return JSON.parse(localStorage.getItem(KEY) || '') } catch { /* ignore */ }
    }
    return { token: null, refresh: null, user: null }
  })

  const persist = () => { if (import.meta.client) localStorage.setItem(KEY, JSON.stringify(state.value)) }
  const token = computed(() => state.value.token)
  const refreshToken = computed(() => state.value.refresh)
  const user = computed(() => state.value.user)
  const isLoggedIn = computed(() => !!state.value.token)
  const hasRole = (...roles: string[]) => !!state.value.user?.roles.some(r => roles.includes(r))
  const canOperate = computed(() => hasRole('SuperAdmin', 'Administrator', 'Operator'))
  const canManage = computed(() => hasRole('SuperAdmin', 'Administrator'))
  const canAudit = computed(() => hasRole('SuperAdmin', 'Administrator', 'Auditor'))
  const isSuper = computed(() => hasRole('SuperAdmin'))

  function setSession(d: any) {
    state.value = { token: d.accessToken, refresh: d.refreshToken, user: d.user }
    persist()
  }

  async function login(username: string, password: string) {
    const r: any = await $fetch(`${config.public.apiBase}/api/v1/auth/login`, { method: 'POST', body: { username, password } })
    setSession(r.data)
  }

  let refreshing: Promise<boolean> | null = null
  async function refresh(): Promise<boolean> {
    if (!state.value.refresh) return false
    refreshing ??= (async () => {
      try {
        const r: any = await $fetch(`${config.public.apiBase}/api/v1/auth/refresh`, { method: 'POST', body: { refreshToken: state.value.refresh } })
        setSession(r.data); return true
      } catch { clear(); return false } finally { refreshing = null }
    })()
    return refreshing
  }

  function clear() {
    state.value = { token: null, refresh: null, user: null }
    if (import.meta.client) localStorage.removeItem(KEY)
  }

  async function logout() {
    try {
      if (state.value.refresh) await $fetch(`${config.public.apiBase}/api/v1/auth/logout`, { method: 'POST', body: { refreshToken: state.value.refresh }, headers: { Authorization: `Bearer ${state.value.token}` } })
    } catch { /* abaikan */ }
    clear()
    await navigateTo('/login')
  }

  return { token, refreshToken, user, isLoggedIn, hasRole, canOperate, canManage, canAudit, isSuper, login, refresh, logout, clear }
}
