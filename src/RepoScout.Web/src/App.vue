<script setup lang="ts">
import { computed, ref } from 'vue'
import { analyze, AnalyzeError, type AnalysisUsage } from './api'
import { renderMarkdown } from './markdown'

const repoUrl = ref('')
const question = ref('')
const loading = ref(false)
const error = ref('')
const fieldErrors = ref<Record<string, string[]>>({})
const report = ref('')
const usage = ref<AnalysisUsage | null>(null)

const usageText = computed(() => {
  const u = usage.value
  if (!u) return ''
  const tokens = (n: number | null) => (n === null ? 'n/a' : n.toLocaleString())
  const byTool = Object.entries(u.toolCallsByName)
    .map(([name, count]) => `${name} ${count}`)
    .join(', ')
  const parts = [
    `${u.rounds.toLocaleString()} rounds`,
    `${u.toolCalls.toLocaleString()} tool calls${byTool ? ` (${byTool})` : ''}`,
    `tokens ${tokens(u.inputTokens)} in / ${tokens(u.outputTokens)} out / ${tokens(u.totalTokens)} total`,
    `${(u.durationMs / 1000).toFixed(1)} s`,
  ]
  if (u.budgetExhausted) parts.push('tool budget exhausted')
  return parts.join(' · ')
})

async function submit() {
  loading.value = true
  error.value = ''
  fieldErrors.value = {}
  report.value = ''
  usage.value = null
  try {
    const result = await analyze(repoUrl.value.trim(), question.value.trim())
    report.value = result.report
    usage.value = result.usage
  } catch (e) {
    if (e instanceof AnalyzeError) {
      error.value = e.message
      fieldErrors.value = e.fieldErrors ?? {}
    } else {
      error.value = 'Something went wrong'
    }
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <main>
    <h1>RepoScout</h1>

    <form @submit.prevent="submit">
      <label>
        Repository link
        <input v-model="repoUrl" type="url" required placeholder="https://github.com/owner/repo" />
        <small v-for="msg in fieldErrors.repoUrl" :key="msg" class="field-error">{{ msg }}</small>
      </label>

      <label>
        Your question
        <textarea
          v-model="question"
          required
          maxlength="500"
          rows="4"
          placeholder="What frontend stack is used in this project?"
        />
        <small v-for="msg in fieldErrors.question" :key="msg" class="field-error">{{ msg }}</small>
      </label>

      <button type="submit" :disabled="loading">{{ loading ? 'Analyzing...' : 'Analyze' }}</button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>

    <article v-if="report" class="report" v-html="renderMarkdown(report)"></article>

    <section v-if="report && usage" class="usage">
      <strong>Usage:</strong> {{ usageText }}
    </section>
  </main>
</template>

<style scoped>
main {
  max-width: 720px;
  margin: 0 auto;
  padding: 3rem 1rem;
  font-family: system-ui, sans-serif;
  color: #1f2937;
}

h1 {
  text-align: center;
  font-size: 2.5rem;
  margin-bottom: 2rem;
}

form {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

label {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  font-weight: 600;
}

input,
textarea {
  font: inherit;
  font-weight: 400;
  padding: 0.65rem 0.8rem;
  border: 1px solid #d1d5db;
  border-radius: 8px;
}

input:focus,
textarea:focus {
  outline: 2px solid #6366f1;
  border-color: transparent;
}

textarea {
  resize: vertical;
}

button {
  font: inherit;
  font-weight: 600;
  padding: 0.7rem 1rem;
  border: 0;
  border-radius: 8px;
  background: #4f46e5;
  color: #fff;
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: wait;
}

.field-error,
.error {
  color: #b91c1c;
  font-weight: 400;
}

.error {
  margin-top: 1.5rem;
}

.report {
  margin-top: 2rem;
  padding: 1rem 1.5rem;
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  background: #f9fafb;
  overflow-wrap: anywhere;
}

.usage {
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #6b7280;
}

.report :deep(pre) {
  overflow-x: auto;
  padding: 0.75rem;
  background: #111827;
  color: #f3f4f6;
  border-radius: 6px;
}

.report :deep(blockquote) {
  margin-left: 0;
  padding-left: 1rem;
  border-left: 3px solid #d1d5db;
  color: #4b5563;
}
</style>
