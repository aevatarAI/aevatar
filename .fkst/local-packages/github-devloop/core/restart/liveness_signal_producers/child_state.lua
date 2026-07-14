return {
  family = "child-state",
  marker_family = "state",
  resolver = "child-state",
  surface = "pr-comment-stream",
  version_form = "raw",
  producer = "github-proxy.github_entity_changed",
  marker_source = "libraries/devloop/state.lua",
  request_source = "core/awaiting_pr_replayer.lua",
  marker_builder = "state_marker",
  observe_only = true,
}
