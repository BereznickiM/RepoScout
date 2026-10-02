// PreToolUse hook (Bash|PowerShell): only the main session changes git state or runs `gh pr`.
// `agent_type` is present only inside a subagent; otherwise the caller is the main session.

const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const command = (input.tool_input && input.tool_input.command) || '';
const forbidden = /\bgit\s+(commit|push|add|checkout|switch|branch|reset|restore|stash|merge|rebase)\b|\bgh\s+pr\b/;

if (input.agent_type && forbidden.test(command)) {
  console.log(JSON.stringify({
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'deny',
      permissionDecisionReason: `Agent '${input.agent_type}' may not change git state or create PRs. Finish your task and report back.`,
    },
  }));
}
