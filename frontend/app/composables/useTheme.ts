export function useTheme() {
  const theme = useState<'light' | 'dark'>('theme', () => 'light')
  function apply(t: 'light' | 'dark') {
    theme.value = t
    if (import.meta.client) {
      document.documentElement.setAttribute('data-bs-theme', t)
      localStorage.setItem('dbsync.theme', t)
    }
  }
  function init() {
    if (!import.meta.client) return
    const saved = localStorage.getItem('dbsync.theme') as 'light' | 'dark' | null
    theme.value = saved ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
    document.documentElement.setAttribute('data-bs-theme', theme.value)
  }
  const toggle = () => apply(theme.value === 'dark' ? 'light' : 'dark')
  return { theme, toggle, init }
}

/** Satu state bersama untuk palet perintah (Ctrl+K). */
export function useCommandPalette() {
  const open = useState('cmdOpen', () => false)
  return { open, show: () => (open.value = true), hide: () => (open.value = false) }
}
