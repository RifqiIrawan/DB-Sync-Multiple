export interface Toast { id: number; type: 'success' | 'danger' | 'warning' | 'info'; title: string; text?: string }

export function useToast() {
  const toasts = useState<Toast[]>('toasts', () => [])
  let seq = useState('toastSeq', () => 0)
  function push(type: Toast['type'], title: string, text?: string, ms = 5000) {
    // cegah banjir notifikasi identik (mis. polling berkala saat API mati)
    if (toasts.value.some(t => t.title === title && t.text === text)) return
    const id = ++seq.value
    toasts.value.push({ id, type, title, text })
    setTimeout(() => { toasts.value = toasts.value.filter(t => t.id !== id) }, ms)
  }
  return {
    toasts,
    success: (t: string, x?: string) => push('success', t, x),
    error: (t: string, x?: string) => push('danger', t, x, 9000),
    warn: (t: string, x?: string) => push('warning', t, x, 7000),
    info: (t: string, x?: string) => push('info', t, x),
    dismiss: (id: number) => { toasts.value = toasts.value.filter(t => t.id !== id) },
  }
}

export function useConfirm() {
  const state = useState<{ open: boolean; title: string; text: string; danger: boolean; resolve: ((v: boolean) => void) | null }>('confirm', () => ({ open: false, title: '', text: '', danger: false, resolve: null }))
  function ask(title: string, text = '', danger = false): Promise<boolean> {
    return new Promise(resolve => { state.value = { open: true, title, text, danger, resolve } })
  }
  function answer(v: boolean) { state.value.resolve?.(v); state.value = { ...state.value, open: false, resolve: null } }
  return { state, ask, answer }
}

/** Wrapper $fetch: menambahkan Bearer token, refresh otomatis saat 401, membuka envelope {success,data}, dan menampilkan error. */
export function useApi() {
  const config = useRuntimeConfig()
  const auth = useAuth()
  const toast = useToast()

  async function request<T = any>(method: string, path: string, opts: { body?: any; query?: Record<string, any>; silent?: boolean } = {}): Promise<T> {
    const call = () => $fetch<any>(`${config.public.apiBase}/api/v1${path}`, {
      method: method as any, body: opts.body, query: opts.query,
      headers: auth.token.value ? { Authorization: `Bearer ${auth.token.value}` } : {},
    })
    try {
      let res
      try { res = await call() }
      catch (e: any) {
        if (e?.response?.status === 401 && await auth.refresh()) res = await call()
        else throw e
      }
      return res.data as T
    } catch (e: any) {
      const status = e?.response?.status
      if (status === 401) { auth.clear(); await navigateTo('/login'); throw e }
      if (!opts.silent) {
        const d = e?.data
        const detail = d?.errors?.length ? d.errors.map((x: any) => x.message).join('\n') : (e?.message ?? '')
        toast.error(d?.message ?? 'Permintaan gagal', detail)
      }
      throw e
    }
  }

  return {
    request,
    get: <T = any>(p: string, query?: Record<string, any>, silent = false) => request<T>('GET', p, { query, silent }),
    post: <T = any>(p: string, body?: any, silent = false) => request<T>('POST', p, { body: body ?? {}, silent }),
    put: <T = any>(p: string, body?: any) => request<T>('PUT', p, { body }),
    del: <T = any>(p: string) => request<T>('DELETE', p),
  }
}

export const fmtDate = (v?: string | null) => v ? new Date(v).toLocaleString('id-ID', { dateStyle: 'medium', timeStyle: 'medium' }) : '-'
export const fmtNum = (v?: number | null) => v == null ? '-' : v.toLocaleString('id-ID')
export function fmtDuration(a?: string | null, b?: string | null) {
  if (!a) return '-'
  const s = Math.max(0, Math.round(((b ? new Date(b) : new Date()).getTime() - new Date(a).getTime()) / 1000))
  return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
}
