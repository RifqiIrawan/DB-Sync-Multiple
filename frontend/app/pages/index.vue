<script setup lang="ts">
// Halaman akar: arahkan ke dashboard (middleware auth mengarahkan ke /login bila belum masuk).
await navigateTo('/dashboard', { replace: true })
</script>
<template><div></div></template>
