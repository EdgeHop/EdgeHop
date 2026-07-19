# edgehop-graph-first.ps1 — Claude Code PreToolUse hook (non-blocking nudge)
#
# When Claude Code is about to run a text-search tool (Grep / Glob), this hook
# emits a non-blocking `additionalContext` reminder to prefer the `edgehop`
# MCP tools for STRUCTURAL questions (who calls what, where a symbol lives,
# which components render which, reachability, graph shape) while leaving raw
# text / content search to grep. It NEVER blocks the tool and ALWAYS exits 0.
#
# Delivery note: this file is an artifact only. It is NOT installed into this
# repo's or any consuming repo's settings.json. A consuming repo opts in by
# adding the snippet below to its .claude/settings.json.
#
# ---------------------------------------------------------------------------
# Ready-to-paste .claude/settings.json PreToolUse snippet (adjust the path to
# wherever this script lives in the consuming repo):
#
# {
#   "hooks": {
#     "PreToolUse": [
#       {
#         "matcher": "Grep|Glob",
#         "hooks": [
#           {
#             "type": "command",
#             "command": "pwsh -NoProfile -File .claude/hooks/edgehop-graph-first.ps1"
#           }
#         ]
#       }
#     ]
#   }
# }
# ---------------------------------------------------------------------------

# Never let an error in the hook block or fail the tool call.
$ErrorActionPreference = 'SilentlyContinue'

try {
    # Hook payload arrives as a single JSON object on stdin.
    $raw = [Console]::In.ReadToEnd()
    $toolName = $null
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $payload = $raw | ConvertFrom-Json
        $toolName = $payload.tool_name
    }

    # Only nudge for the text-search tools; everything else passes through silently.
    if ($toolName -eq 'Grep' -or $toolName -eq 'Glob') {
        $context = @(
            'edgehop MCP tools are available for STRUCTURAL questions — prefer them over text search here.',
            'Use: find_symbol (where a symbol lives; component symbols point at their .razor file),',
            'get_callers (who calls a method, including Blazor UI handlers bound in markup),',
            'get_relationships (typed edges: CONTAINS / CALLS / IMPLEMENTS / INHERITS / REFERENCES /',
            'OVERRIDES / RENDERS / HTTP_CALLS — direction out|in|both; depth>1 needs one edge type),',
            'get_path (shortest directed reachability between two symbols), graph_stats (graph shape /',
            'god nodes for orientation). Raw text / content matching stays grep''s job — the graph',
            'indexes structure, not text. This is a non-blocking suggestion; continue if grep is right here.'
        ) -join ' '

        $out = @{
            hookSpecificOutput = @{
                hookEventName     = 'PreToolUse'
                additionalContext = $context
            }
        }

        $out | ConvertTo-Json -Depth 5 -Compress | Write-Output
    }
}
catch {
    # Swallow everything — a broken hook must never break the developer's tool call.
}

# Always succeed; never block.
exit 0
