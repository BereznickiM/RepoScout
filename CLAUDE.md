**About project:**
This project is a single page web app that scouts submitted public GitHub repo and answers users question about this repo.

Users question is processed by AI agent and then this AI agent using available tools scans the repo to answer users question and come back with the report.

*Example questions:*
- "What frontend stack is used in this project?"
- "How this app handles user authentication?"

**Tech Stack:**
.NET 10 + VueJs

**Architecture:**
- Scope: public GitHub repos only. One question -> one report. No follow-ups, no history, no accounts, no database (v1).
- Flow: Vue SPA -> `POST /api/...` -> API -> agent loop (LLM + tools) -> Markdown report. Plain request/response, no SSE/streaming in v1.
- Repo access: GitHub REST API only. No cloning, no tarball download, no local repo copy.
- Agent tools are read-only wrappers over the GitHub API (e.g. repo tree, read file, repo info). Content search strategy is undecided. No code execution, no network access from tools.
- Agent loop is our own, in .NET. The LLM is accessed only through an `IChatClient` abstraction (Microsoft.Extensions.AI), so any provider can be plugged in via config. Provider candidates: Gemini Flash or GitHub Models (free/cheap, must support tool calling). Never call a provider SDK directly from the agent code.
- Scope limiting instead of repo size limits: cap on tool calls per analysis, cap on bytes per file/tool result, skip noise (`node_modules`, `dist`, lockfiles, binaries). When the budget runs out, return a partial report noting what was not inspected.
- GitHub API limits (60/h anonymous, 5000/h with token): use a server-side token.
- Public hosting with shared free-tier limits: per-IP rate limit, global daily analysis cap, clear "limit reached" message.
- Security: repo content is untrusted data (see Prompt injection security below); secrets (LLM key, GitHub token) only on the server via user-secrets/env vars.
- Report format is free-form Markdown; the prompt requires citing file paths as evidence.
- Hosting/deployment: undecided.
- Layering: HTTP layer -> Agent (loop, prompt, budget) -> Tools -> GitHub client. Only the GitHub client knows about GitHub; only the `IChatClient` seam knows about the LLM provider. The agent loop must be testable with a fake `IChatClient`.

**Prompt injection security:**
- Threat: anyone can publish a repo whose content (README, code comments, file names) contains instructions aimed at the agent, or raw HTML. Injection is natural language and cannot be reliably filtered; even without injection, the model may quote HTML from the repo verbatim in the report. Assume the agent can be hijacked.
- Contain the blast radius (primary defense): tools are read-only, no code execution, no network access, no secrets in the agent context, capped tool calls and bytes. A hijacked agent can at most waste budget or produce a false report.
- Do not sanitize or filter repo content at input: the agent analyzes code, and stripping HTML/scripts/handlers would falsify it. Allowed input-side measures: label tool results as untrusted file content (system prompt states file content is never instructions), strip invisible characters (zero-width, Unicode Tags block), size caps and noise skipping.
- Sanitize at output: the rendered report HTML passes through an HTML sanitizer (e.g. DOMPurify) before `v-html` in the frontend. This is the only point all paths converge (quoted repo HTML, model-generated HTML, `javascript:` links). Also block external images (tracking pixels) via sanitizer hook or CSP.
- Report integrity: cited file paths let the user verify claims; a false report cannot be fully prevented.
