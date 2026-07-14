return {
  family = "converge-round",
  resolver = "converge-round",
  surface = "issue-comment-stream",
  version_form = "raw",
  producer = "departments/loop/main.lua",
  queue = "github-proxy.github_issue_comment_request",
  marker_source = "libraries/devloop/convergence/rounds.lua",
  request_source = "libraries/devloop/requests/lifecycle.lua",
  marker_builder = "converge_round_marker",
  request_builder = "build_converge_round_comment_request",
}
