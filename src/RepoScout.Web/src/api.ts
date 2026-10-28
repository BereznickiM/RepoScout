const CLIENT_TIMEOUT_MS = 180_000

export const SERVER_UNAVAILABLE_MESSAGE = 'The server is temporarily unavailable, please try again in a few minutes.'

export class AnalyzeError extends Error {
  fieldErrors?: Record<string, string[]>

  constructor(message: string, fieldErrors?: Record<string, string[]>) {
    super(message)
    this.name = 'AnalyzeError'
    this.fieldErrors = fieldErrors
  }
}

export type AnalysisUsage = {
  rounds: number
  toolCalls: number
  toolCallsByName: Record<string, number>
  inputTokens: number | null
  outputTokens: number | null
  totalTokens: number | null
  durationMs: number
  budgetExhausted: boolean
}

export type AnalyzeResult = {
  report: string
  usage: AnalysisUsage
}

export async function analyze(repoUrl: string, question: string): Promise<AnalyzeResult> {
  let res: Response
  try {
    res = await fetch('/api/analyze', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ repoUrl, question }),
      signal: AbortSignal.timeout(CLIENT_TIMEOUT_MS),
    })
  } catch {
    throw new AnalyzeError(SERVER_UNAVAILABLE_MESSAGE)
  }

  if (res.ok) {
    return (await res.json()) as AnalyzeResult
  }

  if (res.status === 400) {
    const problem = (await res.json().catch(() => null)) as {
      title?: string
      errors?: Record<string, string[]>
    } | null
    const fieldErrors: Record<string, string[]> = {}
    for (const [key, messages] of Object.entries(problem?.errors ?? {})) {
      // ASP.NET may return keys in PascalCase or camelCase; normalize to camelCase.
      fieldErrors[key.charAt(0).toLowerCase() + key.slice(1)] = messages
    }
    const message = problem?.title ?? 'Invalid request'
    console.error(`API error ${res.status}: ${message}`)
    throw new AnalyzeError(message, fieldErrors)
  }

  const problem = (await res.json().catch(() => null)) as {
    title?: string
    detail?: string
  } | null
  const message = problem?.detail ?? problem?.title ?? SERVER_UNAVAILABLE_MESSAGE
  console.error(`API error ${res.status}: ${message}`)
  throw new AnalyzeError(message)
}
