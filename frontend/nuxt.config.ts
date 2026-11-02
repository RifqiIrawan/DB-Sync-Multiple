// DBSync frontend: Nuxt (SPA mode) + Vue 3 + TypeScript + Bootstrap 5
export default defineNuxtConfig({
  compatibilityDate: '2026-11-01',
  ssr: false, // dashboard internal; token disimpan di browser, tidak butuh SSR
  devtools: { enabled: false },
  typescript: { strict: true },
  css: ['bootstrap/dist/css/bootstrap.min.css', 'bootstrap-icons/font/bootstrap-icons.css', '~/assets/main.css'],
  runtimeConfig: {
    public: {
      // Alamat API DBSync. Override dengan env NUXT_PUBLIC_API_BASE
      apiBase: 'http://localhost:5000',
    },
  },
  app: {
    head: {
      title: 'DBSync Platform',
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
    },
  },
})
