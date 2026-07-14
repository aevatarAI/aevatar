local M = {}
local wiring = require("core.devloop_wiring")
local parsers_misc = require("devloop.parsers.misc")
local parsers_pr = require("devloop.parsers.pr")
local workflow_ports = require("devloop.adapters.workflow_ports")
local _hidden_state_conformance = require("devloop.hidden_state_conformance")


function M.decompose_package_queue()
  return "github-devloop-decompose.devloop_decompose"
end

function M.parse_pr_view_merge(stdout)
  return parsers_pr.parse_pr_view_merge(M, stdout)
end

function M.rollup_failure_gate_sha(pr)
  return parsers_misc.rollup_failure_gate_sha(M, pr)
end

local base = require("devloop.base")
local function dept_exec_sync(...) return exec_sync(...) end
M.safe_updated_at = function(...) return base.safe_updated_at(M, ...) end
M.intake_dedup_key = function(...) return base.intake_dedup_key(M, ...) end
M.intake_candidate_delivery_dedup_key = function(...) return base.intake_candidate_delivery_dedup_key(M, ...) end
M.ci_selfheal_once_key = function(...) return base.ci_selfheal_once_key(M, ...) end
M.ci_missing_status_first_observed_key = function(...) return base.ci_missing_status_first_observed_key(M, ...) end
M.judgment_worktree_path = base.judgment_worktree_path
M.max_body_len = function(...) return base.max_body_len(M, ...) end
M.quote_untrusted_prompt_text = function(...) return base.quote_untrusted_prompt_text(M, ...) end
M.gh_exec_opts = function(...) return base.gh_exec_opts(M, ...) end
M._max_key_len = base._max_key_len
M._max_dedup_len = base._max_dedup_len
M._max_title_len = base._max_title_len
M._max_body_len = base._max_body_len
M._max_comments_len = base._max_comments_len
M._max_meta_reason_len = base._max_meta_reason_len
M._max_framing_len = base._max_framing_len
M._max_impl_output_len = base._max_impl_output_len
M._max_blocking_gap_len = base._max_blocking_gap_len
M._max_review_ledger_len = base._max_review_ledger_len
M._max_pr_issue_context_len = base._max_pr_issue_context_len
M._max_pr_title_len = base._max_pr_title_len
M._action_label = base._action_label
M._intake_label = base._intake_label
M._class_label = base._class_label
M._reason_label = base._reason_label
M._verdict_label = base._verdict_label
M._reply_label = base._reply_label
M._untrusted_issue_data_begin = base._untrusted_issue_data_begin
M._untrusted_issue_data_end = base._untrusted_issue_data_end
M._test_bot_login = base._test_bot_login
M._enabled_label = base._enabled_label
M._tracking_label = base._tracking_label
M._hold_label = base._hold_label
M._thinking_label = base._thinking_label
M._ready_label = base._ready_label
M._implementing_label = base._implementing_label
M._awaiting_pr_label = base._awaiting_pr_label
M._pr_open_label = base._pr_open_label
M._reviewing_label = base._reviewing_label
M._merge_ready_label = base._merge_ready_label
M._merging_label = base._merging_label
M._merged_label = base._merged_label
M._fixing_label = base._fixing_label
M._review_meta_label = base._review_meta_label
M._impl_failed_label = base._impl_failed_label
M._blocked_label = base._blocked_label
M._blocked_on_dependency_label = base._blocked_on_dependency_label
M._label_colors = base._label_colors
M._has_value = base._has_value
M._is_review_meta_action = base._is_review_meta_action
require("forge.github_debug_stamp").install(M, require("devloop.base").read_env)
require("core.github_graphql").install(M)
require("devloop.commands").install(M)
require("forge.merge_commands").install(M)
local github_proxy_entity_view = require("devloop.github_proxy_entity_view")
M.cached_entity_view = function(...) return github_proxy_entity_view.cached_entity_view(M, ...) end
M.fetch_pr_view_origin = github_proxy_entity_view.fetch_pr_view_origin
M.invalidate_entity_after_write = github_proxy_entity_view.invalidate_entity_after_write
require("forge.merge").install(M)
local git_mechanics = require("devloop.git_mechanics")
local function dept_exec_argv(...) return exec_argv(...) end
M.git = require("forge.git").new(dept_exec_argv)
require("devloop.logging").install(M)
require("devloop.state").install(M)
require("devloop.gate").install({ sources = wiring.gate_sources() })
require("core.pr_delegation").install(M)
require("core.impl_failure").install(M)
M.restart_package_name = "github-devloop"
M.restart_lifecycle_states = {
  "thinking",
  "dependency_wait",
  "ready",
  "implementing",
  "awaiting-pr",
  "impl-failed",
  "blocked",
  "merged",
}
M.restart_source_root = "packages/github-devloop/"
M.restart_consumer_sources = {
  "packages/github-devloop/departments/observe_issue/main.lua",
  "packages/github-devloop/departments/liveness_scan/main.lua",
  "packages/github-devloop/core/awaiting_pr_replayer.lua",
  "packages/github-devloop/core/ready_split.lua",
  "libraries/devloop/decompose.lua",
}
require("devloop.restart").install(M, wiring.restart(M))
require("devloop.restart.issue.pr_partition_contract").install(M)
local restart_liveness_resolved = require("devloop.liveness").with_restart_policy({
  runtime_provenance = {
    proposal_id = "github-devloop/issue/provenance/repo/1",
    version = "restart-liveness-provenance",
    marker_created_at = "2026-06-03T00:00:00Z",
  },
})
restart_liveness_resolved.workflow_ports = workflow_ports.from_devloop(M)
require("workflow.restart_liveness_contract").install(M, restart_liveness_resolved)
local restart_responsibility_contract = require("devloop.restart_responsibility_contract")
M.restart_responsibility_inventory_errors = function(...) return restart_responsibility_contract.restart_responsibility_inventory_errors(M, ...) end
M.strict_restart_responsibility_contract_errors = function(...) return restart_responsibility_contract.strict_restart_responsibility_contract_errors(M, ...) end
local restart_actionable_epoch = require("devloop.restart_actionable_epoch")
M.actionable_epoch_resolve = function(...) return restart_actionable_epoch.actionable_epoch_resolve(M, ...) end
local ready_split_replayers = require("core.ready_split").install(M)
local awaiting_pr_replayers = require("core.awaiting_pr_replayer").install(M)
M.replayer_registry = {
  dependency_wait = ready_split_replayers.dependency_wait,
  ready = ready_split_replayers.ready,
  ["awaiting-pr"] = awaiting_pr_replayers["awaiting-pr"],
}
require("core.liveness_bounds").install(M)
require("devloop.liveness").install(M, wiring.liveness(M))
local prompts = require("devloop.prompts")
prompts.install(M, wiring.prompts(), { implement = true })
require("core.reconcile_requests").install(M)
local entity = require("devloop.entity")
M.linked_pr_surface_snapshot = function(...) return entity.linked_pr_surface_snapshot(M, ...) end
require("core.implement_attempt").install(M)
require("core.ratchet_slice_ledger").install(M)
require("core.dependencies").install(M)
require("core.span_conformance").install(M)

return M
