return {
  family = "merge-gate-wait",
  resolver = "merge-gate-wait",
  surface = "pr-comment-stream",
  version_form = "raw",
  producer = "departments/merge/ci_wait.lua",
  queue = "github-proxy.github_pr_comment_request",
  marker_source = "core/merge_gate_wait.lua",
  request_source = "core/merge_gate_wait.lua",
  marker_builder = "merge_gate_wait_marker",
  request_builder = "build_merge_gate_wait_comment_request",
}
