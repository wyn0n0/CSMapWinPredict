<script setup lang="ts">
defineProps<{ name: 'upload' | 'reset' | 'palette' | 'file' | 'radar' | 'chart' | 'layers' | 'arrow' | 'info' }>()
const paths = {
  upload: 'M12 15V3m-4 4 4-4 4 4M5 13v7h14v-7',
  reset: 'M3 10a9 9 0 1 1 2 8M3 4v6h6',
  palette: 'M12 3a9 9 0 1 0 0 18h1a2 2 0 0 0 1-3.7 1.5 1.5 0 0 1 1-2.7h2a4 4 0 0 0 4-4C21 6.4 17 3 12 3ZM7 10h.01M10 7h.01M15 7h.01M17 10h.01',
  file: 'M14 3H5v18h14V8ZM14 3v5h5M8 12h8M8 16h6',
  radar: 'M12 2v4m0 12v4M2 12h4m12 0h4M12 8v8m-4-4h8M20 12a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z',
  chart: 'M3 17h4V9h5v4h5V5h4M3 21h18',
  layers: 'm3 7 9-4 9 4-9 4ZM3 12l9 4 9-4M3 17l9 4 9-4',
  arrow: 'M4 12h16m-6-6 6 6-6 6',
  info: 'M12 11v6m0-10h.01M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z',
}
</script>

<template>
  <svg class="ui-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path :d="paths[name]" /></svg>
</template>
