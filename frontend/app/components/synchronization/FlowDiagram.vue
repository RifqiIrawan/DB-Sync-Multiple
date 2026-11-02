<script setup lang="ts">
// Visual alur data A ⇄ B. Paket bergerak cepat saat job berjalan, pelan saat idle, hilang bila grup tidak aktif.
const props = defineProps<{ a?: any; b?: any; direction?: string; running?: boolean; active?: boolean }>()
const cls = computed(() => [props.running ? 'running' : props.active ? 'idle' : 'off', props.direction === 'B_TO_A' ? 'rev' : ''])
const icon = (t?: string) => t === 'MYSQL' ? 'bi-database' : t === 'POSTGRESQL' ? 'bi-database-fill' : 'bi-server'
const label = (t?: string) => t === 'MYSQL' ? 'MySQL' : t === 'POSTGRESQL' ? 'PostgreSQL' : t === 'SQLSERVER' ? 'SQL Server' : t
</script>
<template>
  <div class="flow" :class="cls">
    <div class="node a">
      <div class="d-flex align-items-center gap-2"><span class="ep ep-a">A</span><span class="nm" :title="a?.connectionName">{{ a?.connectionName ?? '—' }}</span></div>
      <span class="dbchip mt-1"><i :class="`bi ${icon(a?.dbType)}`"></i>{{ label(a?.dbType) }}</span>
    </div>
    <svg class="pipe" viewBox="0 0 76 26"><path class="rail" d="M2 13 H74" /><path class="packet" d="M2 13 H74" />
      <path v-if="direction !== 'B_TO_A'" d="M68 8 L74 13 L68 18" fill="none" stroke="var(--faint)" stroke-width="2" stroke-linecap="round" />
      <path v-if="direction !== 'A_TO_B'" d="M8 8 L2 13 L8 18" fill="none" stroke="var(--faint)" stroke-width="2" stroke-linecap="round" /></svg>
    <div class="node b">
      <div class="d-flex align-items-center gap-2"><span class="ep ep-b">B</span><span class="nm" :title="b?.connectionName">{{ b?.connectionName ?? '—' }}</span></div>
      <span class="dbchip mt-1"><i :class="`bi ${icon(b?.dbType)}`"></i>{{ label(b?.dbType) }}</span>
    </div>
  </div>
</template>
