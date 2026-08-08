local M = {}

local default_marker_version = "2026-06-02T00-00-00Z"

function M.new(ctx, funcs)
  local t = ctx.t
  local core = ctx.core
  local entity_read_mocks = ctx.entity_read_mocks
  local m_builders = ctx.m_builders
  local pr_safety = ctx.pr_safety
  local has_value = ctx.has_value
  local reviewing = funcs.reviewing
  local pr_link_marker_for_fix = funcs.pr_link_marker_for_fix

  local function json_string(value)
    return tostring(value)
      :gsub("\\", "\\\\")
      :gsub('"', '\\"')
      :gsub("\n", "\\n")
  end

  local function render_comment(comment)
    local body, author, created_at = comment, "fkst-test-bot", "2026-06-03T01:00:00Z"
    if type(comment) == "table" then
      body = comment.body
      author = comment.author_login or author
      created_at = comment.created_at or created_at
    end
    local id = type(comment) == "table" and comment.id or nil
    local id_field = id ~= nil and tostring(id) ~= "" and string.format('"id":"%s",', json_string(id)) or ""
    return string.format(
      '{%s"body":"%s","author":{"login":"%s"},"createdAt":"%s"}',
      id_field,
      json_string(body or ""),
      json_string(author),
      json_string(created_at)
    )
  end

  local function encode_assignees_json(assignees)
    local rendered = {}
    for _, assignee in ipairs(assignees or { "fkst-test-bot" }) do
      table.insert(rendered, string.format('{"login":"%s"}', json_string(assignee)))
    end
    return table.concat(rendered, ",")
  end

  local function mock_issue_state(labels, state, comments, assignees, author_login, created_at)
    local selected_comments = {}
    if comments ~= nil then
      for _, comment in ipairs(comments) do
        table.insert(selected_comments, comment)
      end
    else
      local state_marker = nil
      for _, label in ipairs(labels or {}) do
        if label == "fkst-dev:thinking" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "thinking", default_marker_version)
        elseif label == "fkst-dev:ready" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "ready", default_marker_version)
        elseif label == "fkst-dev:implementing" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "implementing", default_marker_version)
        elseif label == "fkst-dev:pr-open" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "pr-open", default_marker_version)
        elseif label == "fkst-dev:reviewing" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "reviewing", default_marker_version)
        elseif label == "fkst-dev:merge-ready" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "merge-ready", default_marker_version)
        elseif label == "fkst-dev:fixing" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "fixing", default_marker_version)
        elseif label == "fkst-dev:impl-failed" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "impl-failed", default_marker_version)
        elseif label == "fkst-dev:blocked" then
          state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "blocked", default_marker_version)
        end
      end
      if state_marker ~= nil then
        table.insert(selected_comments, state_marker)
      end
    end
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:enabled" }, selected_comments, { state = state or "OPEN", assignees = assignees, author_login = author_login, created_at = created_at })
    entity_read_mocks.mock_issue_read_forms(t, {
      labels = labels or { "fkst-dev:enabled" },
      comments = selected_comments,
      state = state or "OPEN",
      assignees = assignees,
      author_login = author_login,
      created_at = created_at,
    })
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:enabled" }, comments = selected_comments, state = state or "OPEN", assignees = assignees, author_login = author_login, created_at = created_at }, "title,body,comments,labels,state,updatedAt,assignees")
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:enabled" }, comments = selected_comments, state = state or "OPEN", assignees = assignees, author_login = author_login, created_at = created_at }, "title,body,comments,labels,state,createdAt,updatedAt,assignees,author")
  end

  local function state_from_labels(labels)
    for _, label in ipairs(labels or {}) do
      if label == "fkst-dev:thinking" then
        return "thinking"
      end
      if label == "fkst-dev:ready" then
        return "ready"
      end
      if label == "fkst-dev:implementing" then
        return "implementing"
      end
      if label == "fkst-dev:pr-open" then
        return "pr-open"
      end
      if label == "fkst-dev:reviewing" then
        return "reviewing"
      end
      if label == "fkst-dev:merge-ready" then
        return "merge-ready"
      end
      if label == "fkst-dev:merging" then
        return "merging"
      end
      if label == "fkst-dev:merged" then
        return "merged"
      end
      if label == "fkst-dev:fixing" then
        return "fixing"
      end
      if label == "fkst-dev:impl-failed" then
        return "impl-failed"
      end
      if label == "fkst-dev:blocked" then
        return "blocked"
      end
    end
    return nil
  end

  local function with_default_state_marker(labels, comments)
    local rendered = {}
    local has_explicit_state_marker = false
    for _, comment in ipairs(comments or {}) do
      local body = comment
      if type(comment) == "table" then
        body = comment.body
      end
      if tostring(body or ""):find("fkst:github-devloop:state:v1", 1, true) ~= nil then
        has_explicit_state_marker = true
      end
      table.insert(rendered, comment)
    end
    local state = state_from_labels(labels)
    if state ~= nil and not has_explicit_state_marker then
      table.insert(rendered, core.state_marker("github-devloop/issue/owner/repo/42", state, default_marker_version))
    end
    return rendered
  end

  local function set_pr_phase_comments(labels, comments)
    ctx.pr_phase_comments = with_default_state_marker(labels, comments)
  end

  local function take_pr_phase_comments()
    local comments = ctx.pr_phase_comments
    ctx.pr_phase_comments = nil
    return comments
  end

  local function set_pending_pr_origin(value)
    ctx.pending_pr_origin = value
  end

  local function latest_fix_head_sha(comments)
    local found = nil
    for _, comment in ipairs(comments or {}) do
      local body = comment
      if type(comment) == "table" then
        body = comment.body
      end
      local head = tostring(body or ""):match('fkst:github%-devloop:fix:v1[^<]*new_head_sha="([^"]+)"')
      if head ~= nil and pr_safety.is_safe_head_sha(head) then
        found = head
      end
    end
    return found
  end

  local function take_pending_pr_origin()
    local value = ctx.pending_pr_origin
    ctx.pending_pr_origin = nil
    return value
  end

  local function mock_pr_origin_from_cached(payload, head_sha)
    local cached = take_pr_phase_comments()
    local pending = take_pending_pr_origin()
    if cached == nil and pending == nil then
      return
    end
    local repo = pending and pending.repo or "owner/repo"
    local pr_number = pending and pending.pr_number or 7
    local head = pending and pending.head or "devloop-owner-repo-42-01HY"
    local base_branch = pending and pending.base_branch or "dev"
    local state = pending and pending.state or "OPEN"
    local effective_head_sha = latest_fix_head_sha(cached) or (pending and pending.head_sha) or head_sha or "def456"
    local comments = {}
    if pending ~= nil then
      for _, comment in ipairs(pending.comments or {}) do
        table.insert(comments, comment)
      end
    elseif cached ~= nil then
      table.insert(comments, m_builders.pr_origin_marker(payload.proposal_id, "42", head, payload.version or reviewing().version, base_branch))
    end
    for _, comment in ipairs(cached or {}) do
      table.insert(comments, comment)
    end
    local fields = { repo = repo, number = pr_number, comments = comments, head = head, head_sha = effective_head_sha, state = state, base_branch = base_branch, labels = pending and pending.labels or {} }
    local times = pending and pending.times or ctx.default_pr_origin_times
    if ctx.pr_origin_view_times_enabled then
      fields.times = times
    end
    entity_read_mocks.mock_pr_read_forms(t, fields)
    local view_fields = {
      repo = repo,
      number = pr_number,
      comments = comments,
      head = head,
      head_sha = effective_head_sha,
      state = state,
      base_branch = base_branch,
      labels = pending and pending.labels or {},
    }
    entity_read_mocks.mock_pr_view_selector(t, view_fields, entity_read_mocks.pr_origin_selector, ctx.pr_origin_view_times_enabled and times or nil)
    return repo, pr_number
  end

  local function mock_result_issue_value(labels, comments, extra)
    local fields = extra or {}
    local gh_labels = {}
    for _, label in ipairs(labels or { "fkst-dev:thinking" }) do
      table.insert(gh_labels, { name = label })
    end
    local gh_comments = {}
    for _, comment in ipairs(comments or with_default_state_marker(labels or { "fkst-dev:thinking" })) do
      if type(comment) == "table" then
        table.insert(gh_comments, comment)
      else
        table.insert(gh_comments, {
          body = tostring(comment),
          author = { login = fields.comment_author_login or fields.author_login or "fkst-test-bot" },
          createdAt = "2026-06-03T01:00:00Z",
        })
      end
    end
    local gh_assignees = {}
    for _, assignee in ipairs(fields.assignees or {}) do
      table.insert(gh_assignees, { login = assignee })
    end
    return {
      number = fields.number or 42,
      title = fields.title or "Implement decision recorder",
      body = fields.body or "",
      url = fields.url or "https://github.example/owner/repo/issues/42",
      updatedAt = fields.updated_at or "2026-06-03T01:02:03Z",
      state = fields.state or "OPEN",
      labels = gh_labels,
      comments = gh_comments,
      assignees = gh_assignees,
      author = { login = fields.author_login or "fkst-test-bot" },
    }
  end

  local function mock_issue_body(body)
    entity_read_mocks.mock_issue_view_raw_selector(t, {}, "body", {
      stdout = string.format('{"body":"%s"}\n', json_string(body or "Issue body")),
    })
  end

  local function mock_issue_result(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:thinking" }, comments)
    local fields = {}
    for key, value in pairs(extra or {}) do
      fields[key] = value
    end
    local selected = with_default_state_marker(labels or { "fkst-dev:thinking" }, comments)
    ctx.pending_result_issue = mock_result_issue_value(labels or { "fkst-dev:thinking" }, selected, fields)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:thinking" }, selected, fields)
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "labels,comments")
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "assignees,author")
  end

  local function mock_issue_loop(labels, comments, extra)
    local fields = extra or {}
    local selected = with_default_state_marker(labels or { "fkst-dev:thinking" }, comments)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:thinking" }, selected, fields)
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, title = fields.title, updated_at = fields.updated_at, state = fields.state, assignees = fields.assignees, author_login = fields.author_login }, "title,updatedAt,labels,comments,state,author")
  end

  local function mock_issue_reconcile(labels, comments, extra)
    mock_issue_loop(labels or { "fkst-dev:thinking" }, comments, extra)
  end

  local function mock_issue_commit_subject_title(fields)
    if fields.commit_title_error ~= nil then
      entity_read_mocks.mock_issue_view_raw_selector(t, {}, "number,title,author", { stderr = tostring(fields.commit_title_error), exit_code = 1 })
      return
    end
    entity_read_mocks.mock_issue_view_raw_selector(t, {}, "number,title,author", {
      stdout = string.format(
        '{"number":42,"title":"%s","author":{"login":"%s"}}\n',
        json_string(fields.commit_title or fields.title or "Implement decision recorder"),
        json_string(fields.author_login or "fkst-test-bot")
      ),
    })
  end

  local function mock_issue_title_labels_comments(labels, comments, extra, default_label, include_default_marker, selector)
    local rendered_labels = {}
    local selected_labels = labels or { default_label }
    for _, label in ipairs(selected_labels) do
      table.insert(rendered_labels, string.format('{"name":"%s"}', json_string(label)))
    end
    local rendered_comments = {}
    local selected_comments = comments or {}
    if include_default_marker then
      selected_comments = with_default_state_marker(selected_labels, selected_comments)
    end
    for _, comment in ipairs(selected_comments) do
      table.insert(rendered_comments, render_comment(comment))
    end
    local fields = extra or {}
    local needs_implement_rechecks = has_value(selected_labels, "fkst-dev:ready")
      or has_value(selected_labels, "fkst-dev:implementing")
      or has_value(selected_labels, "fkst-dev:impl-failed")
    local view_count = include_default_marker and needs_implement_rechecks and 5 or 1
    fields.times = view_count
    entity_read_mocks.mock_issue_read_with_defaults(t, selected_labels, selected_comments, fields)
    entity_read_mocks.mock_issue_view_selector(t, {
      repo = fields.repo,
      number = fields.number,
      labels = selected_labels,
      comments = selected_comments,
      title = fields.title,
      body = fields.body,
      state = fields.state,
      assignees = fields.assignees,
      author_login = fields.author_login,
    }, selector or "title,labels,comments,author", view_count)
    mock_issue_commit_subject_title(fields)
  end

  local function mock_issue_implement(labels, comments, extra)
    mock_issue_title_labels_comments(labels, comments, extra, "fkst-dev:ready", true, "title,body,labels,comments,state,author")
  end

  local function mock_issue_implement_raw(labels, comments, extra)
    mock_issue_title_labels_comments(labels or {}, comments, extra, nil, false, "title,body,labels,comments,state,author")
  end

  local function mock_issue_reviewing(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:pr-open" }, comments)
    local fields = extra or {}
    local selected = with_default_state_marker(labels or { "fkst-dev:pr-open" }, comments)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:pr-open" }, selected, fields)
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:pr-open" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "labels,comments")
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:pr-open" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "assignees,author")
  end

  local function mock_issue_review(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:reviewing" }, comments)
    local fields = extra or {}
    local selected = with_default_state_marker(labels or { "fkst-dev:reviewing" }, comments)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:reviewing" }, selected, fields)
    entity_read_mocks.mock_issue_view_selector(t, { repo = fields.repo, number = fields.number, labels = labels or { "fkst-dev:reviewing" }, comments = selected, title = fields.title, assignees = fields.assignees, author_login = fields.author_login }, "title,labels,comments,assignees,author")
  end

  local function mock_issue_decompose(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:blocked" }, comments)
    local fields = extra or {}
    local selected = with_default_state_marker(labels or { "fkst-dev:blocked" }, comments)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:blocked" }, selected, { title = fields.title, body = fields.body or "Body from GitHub" })
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:blocked" }, comments = selected, title = fields.title, body = fields.body or "Body from GitHub", author_login = fields.author_login }, "title,body,labels,comments,author")
  end

  local function mock_issue_fix(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:fixing" }, comments)
    mock_issue_title_labels_comments(labels, comments, extra, "fkst-dev:fixing", true)
  end

  local function mock_issue_fix_for_event(fix, labels, comments, branch, impl_version, extra)
    local with_link = {}
    for _, comment in ipairs(comments or {}) do
      table.insert(with_link, comment)
    end
    table.insert(with_link, pr_link_marker_for_fix(fix, branch, impl_version))
    set_pr_phase_comments(labels or { "fkst-dev:fixing" }, comments)
    mock_issue_fix(labels, with_link, extra)
  end

  local function mock_issue_review_meta(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:review-meta" }, comments)
    mock_issue_fix(labels or { "fkst-dev:review-meta" }, comments, extra)
  end

  local function mock_issue_merge(labels, comments, extra)
    set_pr_phase_comments(labels or { "fkst-dev:merge-ready" }, comments)
    local fields = extra or {}
    local selected = with_default_state_marker(labels or { "fkst-dev:merge-ready" }, comments)
    entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:merge-ready" }, selected, { title = fields.title, state = fields.state, assignees = fields.assignees or { "fkst-test-bot" } })
    entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:merge-ready" }, comments = selected, title = fields.title, state = fields.state, assignees = fields.assignees or { "fkst-test-bot" }, author_login = fields.author_login }, "title,labels,comments,state,assignees,author")
  end

  return {
    json_string = json_string,
    render_comment = render_comment,
    default_marker_version = default_marker_version,
    encode_assignees_json = encode_assignees_json,
    mock_issue_state = mock_issue_state,
    state_from_labels = state_from_labels,
    with_default_state_marker = with_default_state_marker,
    set_pr_phase_comments = set_pr_phase_comments,
    take_pr_phase_comments = take_pr_phase_comments,
    set_pending_pr_origin = set_pending_pr_origin,
    take_pending_pr_origin = take_pending_pr_origin,
    mock_pr_origin_from_cached = mock_pr_origin_from_cached,
    mock_result_issue_value = mock_result_issue_value,
    mock_issue_body = mock_issue_body,
    mock_issue_result = mock_issue_result,
    mock_issue_loop = mock_issue_loop,
    mock_issue_reconcile = mock_issue_reconcile,
    mock_issue_implement = mock_issue_implement,
    mock_issue_implement_raw = mock_issue_implement_raw,
    mock_issue_reviewing = mock_issue_reviewing,
    mock_issue_review = mock_issue_review,
    mock_issue_decompose = mock_issue_decompose,
    mock_issue_fix = mock_issue_fix,
    mock_issue_fix_for_event = mock_issue_fix_for_event,
    mock_issue_review_meta = mock_issue_review_meta,
    mock_issue_merge = mock_issue_merge,
  }
end

return M
