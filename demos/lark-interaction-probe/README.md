# Lark Interaction Probe

This directory contains the Phase 1 probe artifacts for migrating n8n form pages to typed Lark card interactions.

## Files

- `structured-review-shadow.yaml`: 18-field domain-neutral record review fixture.

## How To Use

1. Load the YAML fixture through the existing workflow parser.
2. Render the `notify` step interaction through the current Lark card composer path.
3. Send it to a real Lark user in a test tenant.
4. Capture desktop and mobile screenshots.
5. Save a redacted callback payload and verify all 18 logical field keys are present.

## Evidence Policy

Live Lark screenshots and callback payloads must come from an actual run. Do not synthesize or infer them from the fixture.

Suggested evidence filenames after a real run:

- `evidence/YYYY-MM-DD-desktop.png`
- `evidence/YYYY-MM-DD-mobile.png`
- `evidence/YYYY-MM-DD-redacted-callback.json`
- `evidence/YYYY-MM-DD-run-notes.md`
