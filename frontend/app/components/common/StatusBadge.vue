<script setup lang="ts">
const props = defineProps<{ status?: string | null }>()
const cls = computed(() => {
  switch ((props.status ?? '').toUpperCase()) {
    case 'ACTIVE': case 'SUCCESS': case 'CONNECTED': case 'VALID': case 'COMPLETED': case 'OK': case 'RESOLVED': case 'RUNNING': return 'text-bg-success'
    case 'PAUSED': case 'WARNING': case 'RETRY': case 'PARTIAL': case 'QUEUED': case 'PENDING': case 'CANCELLING': case 'COMPLETED_WITH_ERRORS': case 'VALIDATING': return 'text-bg-warning'
    case 'ERROR': case 'FAILED': case 'DISCONNECTED': case 'INVALID': case 'DEAD_LETTER': case 'CONFLICT': return 'text-bg-danger'
    case 'STOPPED': case 'CANCELLED': case 'SKIPPED': case 'DRAFT': case 'UNKNOWN': case 'UNVALIDATED': return 'text-bg-secondary'
    default: return 'text-bg-light border'
  }
})
</script>
<template><span class="badge" :class="cls">{{ status ?? '-' }}</span></template>
