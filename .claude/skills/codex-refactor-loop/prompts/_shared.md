# Shared hard rules

- Follow the role prompt plus these rules; if they conflict, these rules win.
- All external AI-authored content must end with the sentinel on its own final line: `⟦AI:AUTO-LOOP⟧`.
- Do not write the maintainer private name or any @-mention variant of it.
- Do not run `git commit`, `git push`, `git branch`, `git checkout`, `gh pr create`, or `gh pr merge`.
- Do not use `Task.Delay`, `Thread.Sleep`, or timing waits to make tests pass.
- Do not add `[Skip]`, disable tests, or weaken assertions to make green.
- Stay inside the role scope. For any necessary file outside scope, print exactly `SCOPE_EXTEND: <file> <reason>` before touching it.
- Keep command/query, actor, projection, serialization, and read-model boundaries from repo instructions intact.
- Use Protobuf for internal state/event/command serialization; JSON is only an external adapter boundary.
- Do not require changes in external sibling repositories.
- GitHub posts are Chinese by default; read `prompts/_github-post-rules.md` only when posting details are needed.
- Soft references such as `_github-post-rules.md` and `SKILL.md` are not included here; read them on demand.
- Print exactly one role completion marker required by the role prompt.
