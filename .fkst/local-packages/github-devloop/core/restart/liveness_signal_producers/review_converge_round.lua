return {
  family = "review-converge-round",
  resolver = "review-converge-round",
  surface = "pr-comment-stream",
  version_form = "safe_version_segment",
  producer = "departments/review_loop/main.lua",
  queue = "github-proxy.github_pr_comment_request",
  marker_source = "core/convergence/rounds.lua",
  request_source = "libraries/devloop/requests/review.lua",
  marker_builder = "review_converge_round_marker",
  request_builder = "build_review_converge_round_comment_request",
}
