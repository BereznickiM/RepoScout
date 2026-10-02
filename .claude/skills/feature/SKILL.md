---
name: feature
description: Orchestrates one change end-to-end on a dedicated branch -> architect design + user approval (full mode) -> coder -> tester loop -> verify -> commits -> push -> PR. Use only when the user invokes /feature.
argument-hint: "[--quick] <requirements>"
disable-model-invocation: true
allowed-tools: Bash(git status:*), Bash(git switch:*), Bash(git pull:*), Bash(git fetch:*), Bash(git branch:*), Bash(git diff:*), Bash(git log:*), Bash(git add:*), Bash(git commit:*), Bash(git push -u origin:*), Bash(git ls-remote:*), Bash(dotnet build:*), Bash(dotnet test:*), Bash(gh auth status:*), Bash(gh pr create:*)
---

# /feature — orchestrate one change into one PR

Input: `$ARGUMENTS`. If it starts with `--quick` → QUICK mode (strip the flag); otherwise FULL mode. The rest is REQUIREMENTS (verbatim).

You are the orchestrator. You never edit `src/` or `tests/` (a hook enforces it) — delegate to `coder` / `tester` agents. Only you run git/gh. Keep a running state: MODE, BRANCH, DESIGN, CODER_REPORTS, TESTER_REPORTS, ROUND.

If REQUIREMENTS is empty or too vague to act on → ask the user and stop.

## 0. Preconditions (abort on any failure, tell the user why)
1. `git status --porcelain` must be empty. If not: stop, list the files, do NOT stash/discard.
2. `git switch main` then `git pull --ff-only origin main`.
3. `gh auth status` must succeed.
4. Derive BRANCH = `<type>/<slug>`: type ∈ feat|fix|refactor|chore|docs|test|perf (Conventional Commits type matching the change); slug = 2–5 lowercase kebab-case words, ASCII, ≤ 40 chars (e.g. `feat/analyze-endpoint`). It must not exist locally (`git branch --list`) or on origin (`git ls-remote --heads origin <BRANCH>`); if it does, append `-2`, `-3`.
5. `git switch -c <BRANCH>`. Tell the user: mode + branch.

## 1. Design (FULL mode only)
Spawn agent `architect` with:
```
REQUIREMENTS (from the user, verbatim):
<REQUIREMENTS>

Context: RepoScout, see CLAUDE.md (binding). Branch: <BRANCH>.
Return the design as text in your standard format. Do not write files.
Make section 6 (Implementation plan) executable by the coder and section 7 (Testing strategy) concrete enough for the tester.
List every open question that needs the user's decision in section 8, marked [NEEDS USER].
```
Show the user the full design verbatim, then ask:
"Approve this design? Reply `approve` to proceed, or give feedback."

**Approval gate:** proceed ONLY on an explicit approval (approve / yes / OK / go). Anything else is feedback → re-spawn `architect` with: original REQUIREMENTS + previous design verbatim + user feedback verbatim + "Revise the design; list what changed at the top." Show it and ask again. No iteration cap. If the user cancels → `git switch main`, `git branch -D <BRANCH>`, stop.
Store the approved text as DESIGN (include the user's answers to [NEEDS USER] questions).

## 2. Implementation
Spawn agent `coder` with:
```
TASK: <REQUIREMENTS verbatim>
MODE: <full|quick>   BRANCH: <BRANCH> (already checked out; do not switch branches, do not commit)
DESIGN (approved by the user, binding contract):      <- full mode only
<DESIGN verbatim>
QUICK MODE NOTE: no design phase. If this needs a decision listed in your rule 4 (new layer, package, public contract, secret), stop and report it under Blockers.   <- quick mode only

End with your standard report, plus this extra section after "Notes for tester":
- **Suggested tests:** bullet list, each = behavior -> level (unit/integration) -> expected result, including failure paths.
```
After it returns:
- If **Blockers / decisions needed** is non-empty → stop, show blockers to the user (in quick mode suggest re-running without `--quick`). On the user's answer, re-spawn `coder` with the answer appended, or abort.
- If **Verification** doesn't show a successful build → re-spawn `coder` once with "Build is failing: <output>. Fix it." Still failing → escalate (§5).
- Append the report to CODER_REPORTS.

## 3. Tests
Skip this stage only if the diff touches no executable code (docs/config text only) — record that for the PR.
Spawn agent `tester` with:
```
BRANCH: <BRANCH> (checked out; do not commit). See `git diff main` for the change.
REQUIREMENTS: <REQUIREMENTS verbatim>
DESIGN: <DESIGN verbatim, or "none (quick mode)">
CODER REPORT: <latest coder report verbatim>

Instructions:
- Take the coder's "Suggested tests" as input, not as the limit. Add cases from your own analysis (boundaries, failures, CLAUDE.md invariants).
- Run the FULL suite (`dotnet test` from the repo root). Do not finish while any test fails for a reason you can fix in tests/.
- You may finish with failures ONLY if each one is a production-code bug: list it under "Bugs found" with failing test name, expected vs actual, suspected file:line.
- Forbidden: Skip/Ignore attributes, deleting tests, loosening assertions, catching exceptions to hide failures, or otherwise weakening tests to get green. Updating an existing test is allowed only when DESIGN/TASK deliberately changes that contract — say so explicitly per test.
- Report in your standard format. Results must quote the exact `dotnet test` summary line.
```
Append the report to TESTER_REPORTS.

## 4. Coder <-> tester loop
ROUND starts at 1 (the first coder+tester pass above).
While the tester reports Bugs found or Testability issues that block a test:
1. If ROUND == 3 → escalate (§5).
2. ROUND += 1. Re-spawn `coder` with:
   ```
   FIX ROUND <ROUND>/3 on BRANCH <BRANCH>. Do not edit tests/. Do not commit.
   TASK + DESIGN: <as in §2, verbatim>
   BUGS / SEAMS REPORTED BY TESTER (verbatim):
   <tester's Bugs found + Testability issues>
   Fix the production code so these tests pass as written. If you believe a test's expectation is wrong, do not change behavior — explain under Blockers.
   Return your standard report incl. "Suggested tests" for anything new.
   ```
   If the coder disputes a test → stop and ask the user who is right (counts as the round).
3. Re-spawn `tester` with the §3 prompt plus "RE-VERIFY after fix round <ROUND>. Previous tester report: <verbatim>. Coder fix report: <verbatim>. Do not rewrite the failing tests to match the code."
Exit the loop when the tester reports 0 failed, 0 skipped (among new/changed tests), and no bugs.

## 5. Escalation (stop condition)
Stop. Do NOT commit, push or open a PR. Leave the branch and working tree as is. Report to the user:
- branch, mode, ROUND reached
- still-failing tests with expected vs actual, the last coder and tester reports (condensed)
- your assessment: production bug vs wrong test vs design gap
- options: (a) give guidance and continue for 1 more round, (b) switch to full mode / re-design, (c) abandon (`git switch main` + `git branch -D`)

## 6. Independent verification (never trust agent reports alone)
Run yourself, from the repo root:
1. `dotnet build` → 0 errors.
2. `dotnet test` → 0 failed. Compare the passed count with the tester's report. Check no Skip was added: `git diff main -- tests/` must not contain `Skip =`, `[Fact(Skip`, `Ignore`, or removed `[Fact]`/`[Theory]` lines without a stated contract reason.
3. If `src/RepoScout.Web/` changed: `npm run build` in `src/RepoScout.Web`.
4. `git status --porcelain` / `git diff main --stat`: no files outside the expected scope, no `bin/ obj/ dist/ node_modules/`, no `*.user`. Grep the diff for secrets (`api[_-]?key`, `token`, `ghp_`, `sk-`, `AIza`, connection strings).
Any failure → send it to the responsible agent once (build/src → coder, tests → tester); this counts as a loop round under the same cap. Then re-run §6.

## 7. Commits (Conventional Commits, imperative, ≤ 72 chars, lowercase after colon)
Scope: `api`, `web`, `agent`, `tools`, `github`, `tests`, `repo`, or omit.
1. Implementation commit: stage everything except tester-owned changes (`tests/`, plus `RepoScout.slnx` if the tester only added a test project).
   `git commit -m "<type>(<scope>): <summary>" -m "<2-4 line body: what and why>"`
2. Tests commit (if any test changes): `git add tests/` (+ `RepoScout.slnx` if applicable)
   `git commit -m "test(<scope>): <summary of covered behavior>"`
Never `--no-verify`, never amend. Confirm `git status --porcelain` is empty afterwards.

## 8. Push + PR
1. `git push -u origin <BRANCH>`
2. Read `.github/pull_request_template.md`. Fill EVERY section from the reports — no invention; write "None"/"N/A" where not applicable:
   - Summary — from REQUIREMENTS + coder summaries.
   - Design summary — condensed DESIGN §1 + §4 decisions (full mode); quick mode: "Quick mode — no design phase."
   - Changes per layer — from coder's Files changed, grouped HTTP / Agent / Tools / GitHub client / Web / Config.
   - Tests — tests added (tester report), `dotnet test` summary from YOUR run, coder<->tester rounds used, coverage gaps.
   - Security checklist — tick only items you verified; leave others unticked with a note.
   - Out of scope — "Noticed but not touched" + assumptions/deviations.
   No "Generated with Claude Code" footer or any AI attribution line in the PR body.
3. Write the body with the Write tool to `<scratchpad>/pr-body-<slug>.md` (outside the repo), then:
   `gh pr create --base main --head <BRANCH> --title "<same as implementation commit title>" --body-file "<that file>"`
   Do not merge, do not enable auto-merge, do not request reviewers.

## 9. Final report to the user (concise)
- PR URL, branch, mode
- Commits (hash + title)
- Verification: build, `dotnet test` summary, web build (if run)
- Coder<->tester rounds used (n/3) and bugs found/fixed
- Assumptions/deviations from the design (each with impact)
- Coverage gaps and noticed-but-untouched issues (suggested follow-ups)
- Reminder: PR awaits your manual review and merge; after merging run `git switch main && git pull`.
