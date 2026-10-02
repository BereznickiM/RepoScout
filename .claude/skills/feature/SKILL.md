---
name: feature
description: Run one change through the agent workflow (architect -> user approval -> coder -> tester) and open a PR. Use only when the user invokes /feature.
argument-hint: "[--quick] <requirements>"
disable-model-invocation: true
---

# /feature

Requirements: `$ARGUMENTS`. With `--quick` (small change) skip the architect.

You are the orchestrator. You don't edit code — agents do. Only you run git and gh.

## 1. Branch
- The working tree must be clean; otherwise stop and tell the user.
- `git switch main`, `git pull --ff-only`, then `git switch -c <type>/<short-slug>` (type: feat, fix, refactor, docs, test, chore, ci).

## 2. Design (skip with --quick)
- Give the requirements to `architect`. Show the user the full design and wait for explicit approval.
- On feedback: send the requirements, the previous design and the feedback back to `architect`. Repeat until approved.

## 3. Code
- Give `coder` the requirements and the approved design (if any).
- If it reports blockers, show them to the user and wait.

## 4. Tests (max 3 rounds)
- Give `tester` the coder's report (incl. Suggested tests) and the design.
- Status GREEN -> go to step 5.
- Status BLOCKED -> give the reported bugs to `coder`, then run `tester` again. One coder+tester pass = one round.
- Still not GREEN after round 3: stop, summarize the failing tests for the user and ask how to proceed. Do not commit.
- Skip this step only if the change contains no executable code (docs, config).

## 5. Verify, commit, PR
- Run `dotnet build` and `dotnet test` yourself (plus `npm run build` if `src/RepoScout.Web` changed). A failure goes back to step 4 and counts as a round.
- Commit using Conventional Commits: `<type>(<scope>): <summary>`. One commit for code and one for tests is fine.
- `git push -u origin <branch>`.
- Fill in `.github/pull_request_template.md` (every section, nothing invented), save it to `.git/PR_BODY.md`, then:
  `gh pr create --base main --title "<type>(<scope>): <summary>" --body-file .git/PR_BODY.md`
- Never merge. Give the user the PR link, the number of rounds used and anything left unresolved.
