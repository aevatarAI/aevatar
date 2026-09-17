# Lark Card Form Migration Guide

Date: 2026-06-10
Scope: GitHub issue #1929 Phase 1 validation artifacts.

## Purpose

This guide records how to validate migration of n8n external form pages to Aevatar typed Lark card interactions. Phase 1 does not change production code. It creates a repeatable domain-neutral structured-review fixture and an evidence checklist for a real Lark run.

## Target Capabilities

- 18 visible form fields in one Lark card interaction.
- Mixed prefilled, editable, and read-only fields.
- Complete callback payload after submit, including every logical field key.
- File-upload migration path documented instead of assuming n8n form upload parity.

## Probe Fixture

Use `demos/lark-interaction-probe/structured-review-shadow.yaml`.

The fixture models a domain-neutral record review form with 18 logical fields:

1. `record_name`
2. `record_id`
3. `group`
4. `category`
5. `effective_date`
6. `review_type`
7. `source_name`
8. `source_title`
9. `source_reference`
10. `locale`
11. `quantity`
12. `secondary_quantity`
13. `project_reference`
14. `delivery_method`
15. `review_owner`
16. `risk_level`
17. `comments`
18. `attachment_note`

## Manual Run Steps

1. Import or render the probe fixture through the existing typed `InteractionSpec` to Lark card path.
2. Send the card to a real Lark desktop client and a real Lark mobile client.
3. Submit with a mix of unchanged prefilled values and edited values.
4. Capture a desktop screenshot showing all fields can be reached by scrolling.
5. Capture a mobile screenshot showing the same card is usable on a narrow viewport.
6. Store a redacted callback sample that includes `form_value` or equivalent submitted values for all 18 field keys.
7. Record Lark client versions, test tenant/app, run id, and timestamp.

## Evidence To Fill From Real Run

Do not mark the migration complete until this section is filled with real evidence.

| Evidence | Status | Location |
|---|---:|---|
| Desktop screenshot | Missing | Fill after live run |
| Mobile screenshot | Missing | Fill after live run |
| Redacted callback payload | Missing | Fill after live run |
| 18-field completeness check | Missing | Fill after live run |
| Prefill/edit/read-only behavior notes | Missing | Fill after live run |
| File upload replacement decision | Recorded | See below |

## File Upload Migration

The n8n form upload field is not migrated as a raw upload widget in Phase 1. Capture attachment references through Lark/NyxID document or message surfaces that already own file delivery, and store only stable references in the workflow form result.

For generated review documents, use the Lark document creation path. For pdfco-like PDF generation, the migration decision is `replace_with_lark_docx_create`.

## Acceptance Notes

- This guide does not fabricate screenshots, client versions, callback bodies, or live Lark behavior.
- The fixture is a shadow probe. A production migration still needs a real run to confirm Lark card limits and callback shape in the target tenant.
