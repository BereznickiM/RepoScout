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

const renderedReport = computed(() => renderMarkdown(report.value))

const statusText = computed(() => {
  if (loading.value) return 'Scouting the repository...'
  return report.value ? 'Report ready.' : ''
})

const toolBreakdown = computed(() =>
  Object.entries(usage.value?.toolCallsByName ?? {})
    .map(([name, count]) => `${name} ${count}`)
    .join(', '),
)

const formatTokens = (n: number | null) => (n === null ? 'n/a' : n.toLocaleString())

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
  <header>
    <svg class="logo" viewBox="0 0 100 100" fill="none" stroke="currentColor" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <path class="f-b" d="M12 34 C12 30 14 28 18 28 L36 27 L42 34 L80 33 C84 33 86 36 86 39 L85 74 C85 78 82 80 78 80 L19 81 C15 81 13 78 13 74 Z"/>
      <path d="M13 42 L85 41"/>
      <circle class="f-card" cx="60" cy="58" r="15"/>
      <path d="M54 54 C56 51 60 50 63 52" stroke-width="2.5"/>
      <path d="M71 69 L84 83" stroke-width="6"/>
    </svg>
    <h1>RepoScout</h1>
    <p class="tagline">Ask anything about a repository — get answers straight from its code.</p>
  </header>

  <main>
    <form @submit.prevent="submit">
      <div class="field">
        <label for="repo">
          <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path class="f-a" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
            <path d="M20 28 L28 20"/>
            <path d="M22 17 L25 14 C28 11 32 11 34 14 C37 17 36 20 34 23 L31 26"/>
            <path d="M26 31 L23 34 C20 37 16 37 14 34 C11 31 12 28 14 25 L17 22"/>
          </svg>
          Repository URL
        </label>
        <input
          id="repo"
          v-model="repoUrl"
          type="url"
          required
          placeholder="https://github.com/owner/repo"
          autocomplete="off"
          :aria-invalid="fieldErrors.repoUrl?.length ? 'true' : undefined"
          :aria-describedby="fieldErrors.repoUrl?.length ? 'repo-error' : undefined"
        />
        <div v-if="fieldErrors.repoUrl?.length" id="repo-error">
          <small v-for="msg in fieldErrors.repoUrl" :key="msg" class="field-error">{{ msg }}</small>
        </div>
      </div>

      <div class="field">
        <label for="query">
          <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path class="f-b" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
            <circle class="f-card" cx="21" cy="21" r="8.5"/>
            <path d="M27.5 27.5 L35 35" stroke-width="3.5"/>
            <path d="M17 19 C18 17 20 16 22 16.5" stroke-width="2"/>
          </svg>
          What do you want to know?
        </label>
        <input
          id="query"
          v-model="question"
          type="text"
          required
          maxlength="500"
          placeholder="e.g. How this project handles security?"
          autocomplete="off"
          :aria-invalid="fieldErrors.question?.length ? 'true' : undefined"
          :aria-describedby="fieldErrors.question?.length ? 'query-error' : undefined"
        />
        <div v-if="fieldErrors.question?.length" id="query-error">
          <small v-for="msg in fieldErrors.question" :key="msg" class="field-error">{{ msg }}</small>
        </div>
      </div>

      <div class="actions">
        <button
          class="run"
          :class="{ 'is-running': loading }"
          type="submit"
          :disabled="loading"
          :aria-busy="loading"
        >
          <svg class="ico" viewBox="0 0 32 32" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <circle cx="10" cy="20" r="5.5"/><circle cx="22" cy="20" r="5.5"/>
            <path d="M6 16 L8.5 7 L13 7.5 L14 15"/><path d="M26 16 L23.5 7 L19 7.5 L18 15"/>
            <path d="M15 17 C15.7 16.3 16.3 16.3 17 17"/>
          </svg>
          <span class="run-label">{{ loading ? 'Scouting...' : 'Run Scout' }}</span>
          <svg class="arrow" viewBox="0 0 40 20" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M3 10.5 C12 9 22 11 35 10"/><path d="M28 3.5 L36 10 L28.5 16.5"/>
          </svg>
        </button>
      </div>
    </form>

    <p v-if="error" class="alert" role="alert">{{ error }}</p>
    <p class="sr-only" role="status" aria-live="polite">{{ statusText }}</p>

  </main>

  <div v-if="report" class="results">
    <section class="field output" aria-labelledby="result-label">
      <h2 id="result-label" class="label">
        <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path class="f-c" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
          <path class="f-card" d="M15 12 L33 12.5 L32.5 36 L15.5 35.5 Z"/>
          <path d="M19 20 L29 20.3"/>
          <path d="M19 25 L28 25.2"/>
          <path d="M19 30 L25 30.1"/>
        </svg>
        Result
      </h2>
      <div class="report" v-html="renderedReport"></div>
    </section>

    <section v-if="usage" class="field usage" aria-labelledby="usage-label">
      <h2 id="usage-label" class="label">
        <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path class="f-a" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
          <path class="f-card" d="M12 33 C12 24 17 18 24 18 C31 18 36 24 36 33 Z"/>
          <path d="M24 33 L29 23"/>
          <path d="M14.5 27 L16.5 28"/>
          <path d="M24 20 L24 22"/>
          <path d="M33.5 27 L31.5 28"/>
        </svg>
        Usage
      </h2>
      <div class="usage-box">
        <dl class="usage-list">
          <dt>Tool calls</dt>
          <dd>{{ usage.toolCalls.toLocaleString() }}<template v-if="toolBreakdown"> ({{ toolBreakdown }})</template></dd>
          <dt>Rounds</dt>
          <dd>{{ usage.rounds.toLocaleString() }}</dd>
          <dt>Tokens</dt>
          <dd>in {{ formatTokens(usage.inputTokens) }} / out {{ formatTokens(usage.outputTokens) }} / total {{ formatTokens(usage.totalTokens) }}</dd>
          <dt>Time</dt>
          <dd>{{ (usage.durationMs / 1000).toFixed(1) }} s</dd>
        </dl>
        <p v-if="usage.budgetExhausted" class="usage-note">Tool budget ran out - the report may be partial.</p>
      </div>
    </section>
  </div>
</template>
