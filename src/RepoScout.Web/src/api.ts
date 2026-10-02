export class AnalyzeError extends Error {
  fieldErrors?: Record<string, string[]>

  constructor(message: string, fieldErrors?: Record<string, string[]>) {
    super(message)
    this.name = 'AnalyzeError'
    this.fieldErrors = fieldErrors
  }
}

export async function analyze(repoUrl: string, question: string): Promise<string> {
  let res: Response
  try {
    res = await fetch('/api/analyze', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ repoUrl, question }),
    })
  } catch {
    throw new AnalyzeError('API unreachable')
  }

  if (res.ok) {
    return ((await res.json()) as { report: string }).report
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
    throw new AnalyzeError(problem?.title ?? 'Invalid request', fieldErrors)
  }

  throw new AnalyzeError(`Something went wrong (HTTP ${res.status})`)
}
