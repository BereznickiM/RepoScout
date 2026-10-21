<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import { analyze, AnalyzeError, type AnalysisUsage } from './api'
import { renderMarkdown } from './markdown'

const repoUrl = ref('')
const question = ref('')
const loading = ref(false)
const error = ref('')
const fieldErrors = ref<Record<string, string[]>>({})
const report = ref('')
const usage = ref<AnalysisUsage | null>(null)

const resultsEl = ref<HTMLElement | null>(null)
const reportEl = ref<HTMLElement | null>(null)
const usageEl = ref<HTMLElement | null>(null)
const revealed = ref(0)
const typing = computed(() => revealed.value < report.value.length)
let rafId = 0

const renderedReport = computed(() => renderMarkdown(report.value.slice(0, revealed.value)))

const SCOUT_MESSAGES = ['Reading file tree…', 'Inspecting files…', 'Following references…', 'Writing report…']
const SLOW_AFTER_SECONDS = 45
const elapsed = ref(0)
let startedAt = 0
let timerId = 0

const scoutMessage = computed(() => SCOUT_MESSAGES[Math.floor(elapsed.value / 6) % SCOUT_MESSAGES.length])
const isSlow = computed(() => elapsed.value >= SLOW_AFTER_SECONDS)

function startTimer() {
  stopTimer()
  startedAt = Date.now()
  elapsed.value = 0
  timerId = window.setInterval(() => {
    elapsed.value = Math.floor((Date.now() - startedAt) / 1000)
  }, 1000)
}

function stopTimer() {
  clearInterval(timerId)
  timerId = 0
}

const statusText = computed(() => {
  if (loading.value) {
    return isSlow.value
      ? 'Scouting the repository... Scouting is taking longer than usual...'
      : 'Scouting the repository...'
  }
  return report.value && !typing.value ? 'Report ready.' : ''
})

const FOLLOW_MARGIN = 40
const TOP_MARGIN = 16
const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '])
// Only real user input counts as "user scrolled"; our own scrollTo does not fire these.
const USER_EVENTS = ['wheel', 'touchstart', 'touchmove'] as const

let userScrolled = false

function onUserInput(e: Event) {
  if (e.type === 'keydown' && !NAV_KEYS.has((e as KeyboardEvent).key)) return
  userScrolled = true
}

function listenForUser() {
  userScrolled = false
  USER_EVENTS.forEach((t) => window.addEventListener(t, onUserInput, { passive: true }))
  window.addEventListener('keydown', onUserInput)
}

function unlistenForUser() {
  USER_EVENTS.forEach((t) => window.removeEventListener(t, onUserInput))
  window.removeEventListener('keydown', onUserInput)
}

function prefersReducedMotion() {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

function scrollToCardTop() {
  const card = resultsEl.value
  if (!card) return
  window.scrollTo({ top: card.getBoundingClientRect().top + window.scrollY - TOP_MARGIN, behavior: 'auto' })
}

// Keeps the newest text near the viewport bottom while the report is being typed.
function followReveal() {
  const card = resultsEl.value
  const body = reportEl.value
  if (userScrolled || !card || !body) return
  const overflow = body.getBoundingClientRect().bottom - (window.innerHeight - FOLLOW_MARGIN)
  if (overflow <= 0) return
  let top = window.scrollY + overflow
  const cardRect = card.getBoundingClientRect()
  // If the whole card fits on screen, never push its header out of view.
  if (cardRect.height + TOP_MARGIN + FOLLOW_MARGIN <= window.innerHeight) {
    top = Math.min(top, cardRect.top + window.scrollY - TOP_MARGIN)
  }
  if (top > window.scrollY) window.scrollTo({ top, behavior: 'auto' })
}

function stopReveal() {
  cancelAnimationFrame(rafId)
  rafId = 0
  unlistenForUser()
}

async function endReveal() {
  const interrupted = userScrolled
  stopReveal()
  revealed.value = report.value.length
  await nextTick()
  if (!interrupted) usageEl.value?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
}

function startReveal() {
  stopReveal()
  const total = report.value.length
  if (prefersReducedMotion()) {
    revealed.value = total
    nextTick(scrollToCardTop)
    return
  }
  revealed.value = 0
  listenForUser()
  nextTick(() => {
    const card = resultsEl.value
    if (card && card.getBoundingClientRect().top > window.innerHeight - 160) scrollToCardTop()
  })
  // Bounded total time regardless of length: 1.2 s .. 3.5 s.
  const duration = Math.min(3500, Math.max(1200, total * 4))
  const start = performance.now()
  const tick = (now: number) => {
    const progress = Math.min(1, (now - start) / duration)
    revealed.value = Math.ceil(total * progress)
    if (progress < 1) {
      rafId = requestAnimationFrame(tick)
      nextTick(followReveal)
    } else {
      endReveal()
    }
  }
  rafId = requestAnimationFrame(tick)
}

onBeforeUnmount(() => {
  stopReveal()
  stopTimer()
})

const toolRows = computed(() => {
  const entries = Object.entries(usage.value?.toolCallsByName ?? {}).sort((a, b) => b[1] - a[1])
  const max = entries.length ? entries[0][1] : 0
  return entries.map(([name, count]) => ({ name, count, width: max > 0 ? Math.max(6, (count / max) * 100) : 0 }))
})

const tokenSplit = computed(() => {
  const input = usage.value?.inputTokens ?? null
  const output = usage.value?.outputTokens ?? null
  if (input === null || output === null || input + output <= 0) return null
  const sum = input + output
  const inputPct = Math.round((input / sum) * 100)
  return { input, output, total: sum, inputPct, outputPct: 100 - inputPct }
})

const formatTokens = (n: number | null) => (n === null ? 'n/a' : n.toLocaleString())

async function submit() {
  loading.value = true
  startTimer()
  error.value = ''
  fieldErrors.value = {}
  stopReveal()
  report.value = ''
  revealed.value = 0
  usage.value = null
  try {
    const result = await analyze(repoUrl.value.trim(), question.value.trim())
    report.value = result.report
    usage.value = result.usage
    startReveal()
  } catch (e) {
    if (e instanceof AnalyzeError) {
      error.value = e.message
      fieldErrors.value = e.fieldErrors ?? {}
    } else {
      error.value = 'Something went wrong'
    }
  } finally {
    stopTimer()
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
          <svg v-if="!loading" class="arrow" viewBox="0 0 40 20" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M3 10.5 C12 9 22 11 35 10"/><path d="M28 3.5 L36 10 L28.5 16.5"/>
          </svg>
        </button>
      </div>
      <div v-if="loading" class="scout-progress">
        <p class="scout-message">{{ scoutMessage }}</p>
        <p v-if="isSlow" class="scout-slow">Scouting is taking longer than usual...</p>
        <p class="scout-elapsed">Elapsed: {{ elapsed }}s</p>
      </div>
    </form>

    <p v-if="error" class="alert" role="alert">{{ error }}</p>
    <p class="sr-only" role="status" aria-live="polite">{{ statusText }}</p>

  </main>

  <div v-if="report" ref="resultsEl" class="results">
    <section class="field output" aria-labelledby="report-label">
      <h2 id="report-label" class="label">
        <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path class="f-c" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
          <path class="f-card" d="M15 12 L33 12.5 L32.5 36 L15.5 35.5 Z"/>
          <path d="M19 20 L29 20.3"/>
          <path d="M19 25 L28 25.2"/>
          <path d="M19 30 L25 30.1"/>
        </svg>
        <span class="title">Scouting Report</span>
        <button v-if="typing" type="button" class="skip" @click="endReveal">Show all</button>
      </h2>
      <div ref="reportEl" class="report" :class="{ 'is-typing': typing }" @click="typing && endReveal()" v-html="renderedReport"></div>
    </section>

    <section v-if="usage && !typing" ref="usageEl" class="field usage" aria-labelledby="usage-label">
      <h2 id="usage-label" class="label">
        <svg class="icon" viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path class="f-a" d="M6 10 C6 7 8 5 11 5 L37 6 C40 6 42 8 42 11 L41 37 C41 40 39 42 36 42 L10 41 C7 41 5 39 5 36 Z"/>
          <path class="f-card" d="M12 33 C12 24 17 18 24 18 C31 18 36 24 36 33 Z"/>
          <path d="M24 33 L29 23"/>
          <path d="M14.5 27 L16.5 28"/>
          <path d="M24 20 L24 22"/>
          <path d="M33.5 27 L31.5 28"/>
        </svg>
        <span class="title">Usage</span>
      </h2>
      <div class="usage-box">
        <div class="stat-grid">
          <div class="stat stat-a">
            <span class="stat-num">{{ usage.toolCalls.toLocaleString() }}</span>
            <span class="stat-label">Tool calls</span>
          </div>
          <div class="stat stat-b">
            <span class="stat-num">{{ usage.rounds.toLocaleString() }}</span>
            <span class="stat-label">Rounds</span>
          </div>
          <div class="stat stat-c">
            <span class="stat-num">{{ (usage.durationMs / 1000).toFixed(1) }}<small>s</small></span>
            <span class="stat-label">Time</span>
          </div>
          <div class="stat stat-d">
            <span class="stat-num">{{ formatTokens(usage.totalTokens) }}</span>
            <span class="stat-label">Tokens</span>
          </div>
        </div>

        <div v-if="toolRows.length" class="usage-group">
          <h3 class="usage-sub">Tools used</h3>
          <ul class="bars">
            <li v-for="row in toolRows" :key="row.name" class="bar-row">
              <span class="bar-name">{{ row.name }}</span>
              <span class="bar-track"><span class="bar-fill" :style="{ width: row.width + '%' }"></span></span>
              <span class="bar-count">{{ row.count.toLocaleString() }}</span>
            </li>
          </ul>
        </div>

        <div class="usage-group">
          <h3 class="usage-sub">Tokens</h3>
          <template v-if="tokenSplit">
            <div
              class="split"
              role="img"
              :aria-label="`Input ${tokenSplit.input.toLocaleString()} tokens (${tokenSplit.inputPct}%), output ${tokenSplit.output.toLocaleString()} tokens (${tokenSplit.outputPct}%)`"
            >
              <span class="split-in" :style="{ width: tokenSplit.inputPct + '%' }"></span>
              <span class="split-out" :style="{ width: tokenSplit.outputPct + '%' }"></span>
            </div>
            <ul class="legend">
              <li><span class="swatch swatch-in" aria-hidden="true"></span>Input {{ tokenSplit.input.toLocaleString() }} · {{ tokenSplit.inputPct }}%</li>
              <li><span class="swatch swatch-out" aria-hidden="true"></span>Output {{ tokenSplit.output.toLocaleString() }} · {{ tokenSplit.outputPct }}%</li>
              <li class="legend-total">Total {{ formatTokens(usage.totalTokens ?? tokenSplit.total) }}</li>
            </ul>
          </template>
          <p v-else class="usage-muted">Token counts not reported</p>
        </div>

        <p v-if="usage.budgetExhausted" class="usage-note">Tool budget ran out - the report may be partial.</p>
      </div>
    </section>
  </div>

  <footer class="site-footer">
    <span>Made by Michał Bereźnicki</span>
    <span aria-hidden="true">·</span>
    <span>2026</span>
    <span aria-hidden="true">·</span>
    <a
      class="site-footer-link"
      href="https://github.com/BereznickiM/RepoScout"
      target="_blank"
      rel="noopener noreferrer"
    >
      Source code
      <svg viewBox="0 0 16 16" width="18" height="18" fill="currentColor" aria-hidden="true">
        <path
          d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"
        />
      </svg>
    </a>
  </footer>
</template>
