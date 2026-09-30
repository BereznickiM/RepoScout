<script setup lang="ts">
import { onMounted, ref } from 'vue'

const status = ref('loading...')

onMounted(async () => {
  try {
    const res = await fetch('/api/health')
    status.value = (await res.json()).status
  } catch {
    status.value = 'unreachable'
  }
})
</script>

<template>
  <h1>RepoScout</h1>
  <p>API status: {{ status }}</p>
</template>
