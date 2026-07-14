local saga_conformance = require("devloop.saga_conformance")
local M
local wiring = require("core.devloop_wiring")

-- fkst.toml conformance hook: function = "core.saga_conformance_errors" (delegates to typed devloop.saga_conformance.errors)
local function saga_conformance_errors()
  return saga_conformance.errors(M)
end

M = {
  saga_conformance_errors = saga_conformance_errors,
}

function M.decompose_package_queue()
  return "devloop_decompose"
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
require("devloop.commands").install(M)
local github_proxy_entity_view = require("devloop.github_proxy_entity_view")
M.cached_entity_view = function(...) return github_proxy_entity_view.cached_entity_view(M, ...) end
M.fetch_pr_view_origin = github_proxy_entity_view.fetch_pr_view_origin
M.invalidate_entity_after_write = github_proxy_entity_view.invalidate_entity_after_write
require("devloop.logging").install(M)
require("devloop.state").install(M)
local prompts = require("devloop.prompts")
prompts.install(M, wiring.prompts(), { decompose = true })
local entity = require("devloop.entity")
M.linked_pr_surface_snapshot = function(...) return entity.linked_pr_surface_snapshot(M, ...) end
require("core.saga").install(M)
require("core.decompose").install(M)

return M
