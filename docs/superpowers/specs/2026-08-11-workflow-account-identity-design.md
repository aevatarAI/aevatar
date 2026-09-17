# Workflow Activity Account Identity Design

## Problem

Workflow Activity vNext currently lets each route choose the header identity source. Settings passes `/api/auth/me` data into the shell, while Workflows, Activity, and editor routes omit that prop and let the header read the stored NyxID session. Because the current backend response does not include profile fields, the same browser session renders `Account` without an avatar on Settings and the real name and avatar elsewhere.

## Decision

Workflow Activity vNext will have one account identity owner. A shared hook will query `/api/auth/me`, combine it with the restorable NyxID session, and provide both the resolved backend session and the header principal. The shell will always consume this principal; individual routes will no longer provide account identity props.

The backend response remains authoritative for authentication state. Stored NyxID profile fields may fill missing name, email, verification, picture, roles, and groups only when the backend subject exactly matches the stored NyxID `sub`. A mismatched or unknown subject must not reuse cached profile data. An explicitly unauthenticated backend response must render signed-out state even when a restorable browser session exists.

## Components

- `useWorkflowActivityAccount` owns the shared React Query request and identity resolution.
- `resolveWorkflowActivityAccount` is a pure function that validates subject ownership and merges missing profile presentation fields.
- `WorkflowActivityVNextShell` always renders the resolved principal from the shared hook.
- `SettingsPage` reuses the hook's query state and resolved auth session for the Account panel.

## Verification

Focused tests will cover matching-subject fallback, mismatched-subject rejection, explicit signed-out authority, and the Settings route regression where the backend omits profile fields but the matching NyxID session contains the real name and avatar. Browser verification will use the user's existing Chrome session on both Settings and Workflows.
