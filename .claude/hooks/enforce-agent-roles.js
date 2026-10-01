// PreToolUse hook: only `coder` edits src/, only `tester` edits tests/.
// `agent_type` is present only inside a subagent; otherwise the caller is the main session.

const path = require('path');

const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const agent = input.agent_type || 'main session';
const { file_path } = input.tool_input || {};

function requireAgent(owner, what) {
  if (agent === owner) return;
  console.log(JSON.stringify({
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'deny',
      permissionDecisionReason: `Only the '${owner}' agent may ${what}; caller is '${agent}'. Delegate to ${owner}.`,
    },
  }));
  process.exit(0);
}

if (file_path) {
  const top = path.relative(process.env.CLAUDE_PROJECT_DIR, file_path).split(path.sep)[0];
  if (top === 'src') requireAgent('coder', 'modify production code (src/)');
  if (top === 'tests') requireAgent('tester', 'modify tests (tests/)');
}
