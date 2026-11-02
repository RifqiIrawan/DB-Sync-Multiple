export default defineNuxtRouteMiddleware((to) => {
  const auth = useAuth()
  if (to.path === '/login') return auth.isLoggedIn.value ? navigateTo('/dashboard') : undefined
  if (!auth.isLoggedIn.value) return navigateTo('/login')
  if (to.path === '/') return navigateTo('/dashboard')
  if (to.path.startsWith('/users') && !auth.isSuper.value) return navigateTo('/dashboard')
})
