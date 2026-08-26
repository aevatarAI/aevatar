# NyxID Assistant semantic evaluation

## Aevatar source pin

The conformance guard reads every `aevatar.files` entry from the Git tree named
by `aevatar.revision`; uncommitted working-tree content is not source evidence.
The declared revision must be an ancestor of the current `HEAD`, and every
managed source blob at `HEAD` must still equal the declared blob. This prevents
a later committed source change from bypassing the pin by leaving
`sources.json` untouched.
After committing the exact Aevatar source revision to evaluate, refresh the
revision, per-file digests, and aggregate mechanically:

```bash
python3 tools/ci/nyxid_conformance_guard.py \
  --refresh-aevatar-revision '<full-source-commit-sha>'
bash tools/ci/nyxid_conformance_guard.sh
```

Commit the resulting `sources.json` update only after the guard passes.

## Code-execution wire contract

The current-`main` code-execution boundary is maintained independently in
`docs/contracts/nyxid-code-execution-conformance/v1`. Keeping it out of this fixed Assistant source
pin lets upstream wire drift fail its own pull-request and scheduled check without changing the
reproducible Assistant semantic-evaluation baseline.

## Registry revision `nyxid-assistant-actions.v8`

NyxID production began serving `nyxid-assistant-actions.v8` on 2026-08-19
(`registry-v8.json` is the served payload: v7 plus `service.reauthorize`
`{userServiceId, requestedScopes[]}`, risk `grant`, `remember_eligible: false`).
Since aevatar#3521 the loader no longer gates on the revision string at all:
`schema_version` is the only registry-wide compatibility check, the revision is
recorded as an observability label, and each descriptor validates against its
pinned per-action contract independently — a divergent or unknown descriptor is
skipped on its own while `service.connect` / `key.create` / `key.rotate` stay
executable. `service.reauthorize` remains non-executable, and advertising it
still requires the typed producer, AG-UI mapper, postcondition reader,
coverage-manifest row flip, and a fresh semantic evaluation run.
`assistant_registry.revision` still names v7 because `nyxid.revision` /
`nyxid_source_sha256` pin the NyxID commit that published v7; re-pinning the
NyxID source commit (and the CI workflow checkout ref) stays a single-change
operation when the corpus is refreshed.

`semantic-evaluation.json` is the checked-in release-gate record. The conformance guard fails while its
status is not `passed`, while results are absent, or when the recorded aggregate cannot be reproduced
from the case evidence.

The semantic dataset is derived from every `coverage-manifest.json` row that has an `utterances` array.
The current contract contains 134 cases: 77 Class-A cases and 57 Class-L cases. Each case receives the
expected operation and 31 stable-hash distractors. The exact candidate IDs and digest are recorded in
the JSONL evidence. Candidate policy `stable-hash-32.v2` gives the classifier each candidate's own
authoritative assistant mechanism, availability, and outcome class. This context is supplied
symmetrically for every candidate; the adapter still receives no expected operation or score.

## Provider adapter

The runner accepts only a Python adapter under `tools/nyxid-conformance/provider-adapters/`. Its path,
SHA-256 digest, and digest-derived revision are recorded and independently checked by the guard. The
adapter receives no expected operation or expected outcome. It receives only the utterance, the pinned
32-candidate catalog, and the classifier request.

The checked-in `nyxid-proxy-chat-completions.py` adapter has no endpoint, token, profile, or arbitrary
command option. It uses the signed-in CLI identity and the fixed
`nyxid proxy request chrono-llm-public /chat/completions` route. Do not place credentials in command
arguments, evidence, logs, or Git. The adapter sends no tools and the protocol pins
`effect_policy=forbid`; it performs model inference only. It does not call Aevatar, Lark, GitHub, or
another effect-capable service.

The evaluation config also pins the exact NyxID UserService selector and expected resolved model. The
adapter passes the selector with `--via-service`; the guard rejects evidence produced by another model.

## Run

Run from an exact checkout of the Aevatar revision being evaluated:

```bash
python3 tools/nyxid-conformance/run-semantic-evaluation.py \
  --provider 'chrono-llm-public' \
  --provider-route 'chrono-llm-public/chat/completions' \
  --model 'gpt-5.4-2026-03-05' \
  --temperature 0 \
  --prompt-version 'StreamingAgentProfileTurnClassifier@8d15ef5727ab933cd5fa9244181bcb23b798cfc9' \
  --provider-adapter tools/nyxid-conformance/provider-adapters/nyxid-proxy-chat-completions.py \
  --run-id '<bounded-run-id>' \
  --aevatar-revision '<full-40-character-HEAD>' \
  --timeout-seconds 60 \
  --max-attempts 2 \
  --retry-backoff-seconds 1 \
  --evidence-output docs/contracts/nyxid-assistant-conformance/v1/evidence/<run-id>.jsonl \
  --evaluation-output /tmp/<run-id>-semantic-evaluation.json
```

The runner refuses to overwrite `semantic-evaluation.json`. It writes a candidate evaluation document
and one JSONL row per case. Each attempt records UTC timestamps, duration, terminal outcome, exit code,
and response/stderr digests. Raw stderr and provider payloads are not persisted. The only raw model
content retained is the bounded classifier JSON needed for offline recomputation.

Review the candidate, the provider identity, the resolved model IDs, and every failing case before
updating the checked-in evaluation. Never change `status` to `passed` by hand when the generated
candidate is `failed`.

## Offline validation

The guard recomputes the dataset and candidate digests, validates the checked-in adapter digest,
parses every raw model response with the pinned classifier schema, derives the selected operation's
availability/outcome from the coverage manifest, and recomputes all metrics. Passing requires:

- route accuracy at least 0.95 (at least 128 of 134 current cases);
- availability/outcome accuracy exactly 1.0;
- blocked-intent honesty exactly 1.0 (72 current blocked cases after `key.create` and `key.rotate` shipped);
- zero false execution, verification, or strong-consistency claims; and
- zero provider/protocol errors.

Run the focused self-test with:

```bash
python3 -m unittest tools/ci/tests/test_nyxid_semantic_evaluation.py -v
```

The checked-in evaluation records a reviewed authenticated run (`semantic-evaluation.json`,
`status: passed`); the guard recomputes every digest and metric offline from that evidence and
fails on any drift. To refresh, run `tools/nyxid-conformance/run-semantic-evaluation.py` — it
writes a candidate document and refuses to overwrite the recorded evaluation — then review the
candidate and record it explicitly.

## Fixture corpora

`tolerant-reader-fixtures.json` and `adversarial-fixtures.json` are executed, not just
shape-checked. Each tolerant-reader fixture is loaded through `NyxIdAssistantActionRegistry`;
each adversarial fixture is bound to an executor in
`test/Aevatar.AI.Tests/NyxIdAdversarialCorpusTests.cs` that drives real production code and
asserts the fixture's `expected_outcome`. A fixture with no executor fails that suite, and the
guard independently fails if the harness is missing or does not mention every fixture id — a
corpus that asserts only its own shape proves nothing about the system.

Where an outcome is enforced structurally rather than by inspecting content — an injected
instruction cannot approve anything because no model-reachable path decides approvals at all —
the executor asserts that structure and says so in place.
