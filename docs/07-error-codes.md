# Error-Code Catalogue — Clinic Scheduling System

> **Purpose:** the stable contract behind Decision I (API returns codes + params; frontend translates). Define once here so slices reuse codes instead of inventing `{code, params}` shapes and drifting i18n keys.
> **Referenced by:** `00-context.md` §5.
> **Document language:** English.

---

## 1. Envelope

Every error response carries a machine-readable body:

```json
{ "code": "booking.slot_taken", "params": { "time": "2026-08-25T15:30:00Z" } }
```

- `code` — stable, namespaced string: `domain.problem`. Never change a code once shipped; add a new one instead.
- `params` — optional flat object filling placeholders in the i18n message. Never put translated prose in the API.
- The frontend maps `code` → an i18n key (pt-BR / en) and interpolates `params`.

## 2. HTTP status mapping

| Status | Meaning | Example codes |
|---|---|---|
| 400 | malformed request | `validation.*` |
| 401 | not authenticated | `auth.session_expired` |
| 403 | authenticated but not allowed | `auth.forbidden`, `auth.ownership_denied` |
| 404 | not found | `booking.appointment_not_found` |
| 409 | conflict (state/race) | `booking.slot_taken`, `booking.block_overlaps_appointment` |
| 422 | valid shape, violates a business rule | `booking.cutoff_passed`, `booking.outside_working_hours` |
| 429 | rate-limited | `auth.rate_limited` |
| 503 | dependency unavailable | `availability.unavailable`, `calendar.sync_failed` |

## 3. Initial catalogue (seed — extend as slices land)

### auth / session — capability `identity-session`
| Code | Status | When |
|---|---|---|
| `auth.session_expired` | 401 | session missing/expired |
| `auth.forbidden` | 403 | role lacks permission |
| `auth.ownership_denied` | 403 | patient accessing data that is not theirs |
| `auth.invalid_credentials` | 401 | internal account: wrong email/password |
| `auth.account_disabled` | 403 | account locked or disabled |
| `auth.rate_limited` | 429 | too many login attempts (brute-force guard) |
| `auth.google_failed` | 401 | Google OIDC flow failed (bad state/nonce, token invalid, email unverified, or the address belongs to an internal account) |
| `auth.google_unavailable` | 503 | this deployment has no Google client configured, so the federated path is off (added in `identity-session`) |
| `auth.consent_required` | 422 | a required consent has not been granted |
| `auth.email_already_in_use` | 409 | staff account creation with a taken email |
| `auth.account_not_found` | 404 | an administrator acted on a staff account that does not exist (added in `identity-session`) |
| `auth.password_change_required` | 403 | the bootstrapped administrator must replace the supplied credential before doing anything else (added in `identity-session`) |

### patient — capability `identity-session`
| Code | Status | When |
|---|---|---|
| `patient.not_found` | 404 | staff requested a patient record that does not exist (added in `identity-session`). A **patient** asking for a record that is not theirs never sees this — they get `auth.ownership_denied`, so the response cannot be used to discover which records exist |

### validation — cross-cutting
| Code | Status | Params |
|---|---|---|
| `validation.required` | 400 | `field` |
| `validation.invalid_format` | 400 | `field` |

### availability — capability `availability`
| Code | Status | When |
|---|---|---|
| `availability.window_invalid` | 400 | date window malformed / too large |
| `availability.unavailable` | 503 | schedule service could not answer (P2 error state) |

### booking — capability `booking`
| Code | Status | When |
|---|---|---|
| `booking.slot_taken` | 409 | optimistic booking lost the race (P3 "taken" state) |
| `booking.outside_working_hours` | 422 | slot outside the professional's working hours |
| `booking.lead_time_violation` | 422 | inside minimum lead time |
| `booking.horizon_exceeded` | 422 | beyond scheduling horizon |
| `booking.specialty_mismatch` | 422 | professional lacks the appointment type's specialty (I2) |
| `booking.resource_unavailable` | 409 | no free resource of the required type |
| `booking.cutoff_passed` | 422 | patient reschedule/cancel inside the 24h cutoff (F3) |
| `booking.appointment_not_found` | 404 | unknown / soft-deleted appointment |
| `booking.block_overlaps_appointment` | 409 | internal block collides with an active appointment (I7 refusal) |

### calendar — capability `calendar-integration`
| Code | Status | When |
|---|---|---|
| `calendar.not_connected` | 422 | professional has no calendar connection |
| `calendar.consent_revoked` | 422 | OAuth consent revoked (S2 revoked state) |
| `calendar.sync_failed` | 503 | Google API unreachable / sync error |

### generic
| Code | Status | When |
|---|---|---|
| `server.unexpected` | 500 | unhandled error (never leak internals) |

## 4. Rules

- One code per distinct, user-meaningful failure — not one per throw site.
- Add codes here **before** using them in a slice; the matching pt-BR/en i18n keys are part of that change's Definition of Done.
- Prefer codes the design already implies (the P2/P3/S2 states above map directly to `availability.unavailable`, `booking.slot_taken`, `calendar.consent_revoked`).
