// DBSync frontend: Nuxt (SPA mode) + Vue 3 + TypeScript + Bootstrap 5 (di-skin penuh: "Signal Room")
export default defineNuxtConfig({
  compatibilityDate: '2026-11-01',
  ssr: false, // dashboard internal; token disimpan di browser, tidak butuh SSR
  devtools: { enabled: false },
  typescript: { strict: true },
  css: [
    'bootstrap/dist/css/bootstrap.min.css',
    'bootstrap-icons/font/bootstrap-icons.css',
    '@fontsource-variable/bricolage-grotesque',
    '@fontsource-variable/instrument-sans',
    '@fontsource-variable/jetbrains-mono',
    '~/assets/main.css',
  ],
  runtimeConfig: {
    public: {
      // Alamat API DBSync. Override dengan env NUXT_PUBLIC_API_BASE
      apiBase: 'http://localhost:18080',
    },
  },
  app: {
    pageTransition: { name: 'page', mode: 'out-in' },
    head: {
      title: 'DBSync Platform',
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }, { name: 'color-scheme', content: 'light dark' }],
      // terapkan tema sebelum render pertama agar tidak berkedip
      script: [{ innerHTML: "try{var t=localStorage.getItem('dbsync.theme')||(matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light');document.documentElement.setAttribute('data-bs-theme',t)}catch(e){}" }],
    },
  },
})
