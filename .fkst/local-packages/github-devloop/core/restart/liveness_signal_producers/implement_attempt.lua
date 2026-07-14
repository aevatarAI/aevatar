return {
  family = "implement-attempt",
  resolver = "implement-attempt",
  surface = "issue-comment-stream",
  version_form = "raw",
  producer = "departments/implement/main.lua",
  queue = "github-proxy.github_issue_comment_request",
  marker_source = "core/implement_attempt.lua",
  request_source = "libraries/devloop/requests/lifecycle.lua",
  marker_builder = "implement_attempt_marker",
  request_builder = "build_implementing_state_comment_request",
}
