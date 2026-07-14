return {
  family = "dependency-wait",
  resolver = "dependency-hold",
  surface = "issue-comment-stream",
  version_form = "raw",
  producer = "core/ready_split.lua",
  queue = "github-proxy.github_issue_comment_request",
  marker_source = "core/dependencies.lua",
  request_source = "libraries/devloop/requests/lifecycle.lua",
  marker_builder = "dependency_wait_marker",
  request_builder = "build_dependency_hold_comment_request",
}
