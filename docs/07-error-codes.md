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
| 409 | conflict (state/race) | `booking.slot_taken`, `booking.slot_blocked`, `booking.patient_busy`, `booking.block_overlaps_appointment`, `booking.appointment_not_changeable` |
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
| `auth.rate_limited` | 429 | too many requests from one caller — the login brute-force guard, and from `availability-read` the availability search's per-caller budget (03-nfr.md §2). One code because the caller sees the same failure and has the same remedy; per the rules below, that is one user-meaningful failure and not one per throw site |
| `auth.google_failed` | 401 | Google OIDC flow failed (bad state/nonce, token invalid, email unverified, or the address belongs to an internal account) |
| `auth.not_provisioned` | 403 | Google sign-in on the **staff** entry (S0) by an email with **no account at all** — nothing is created; the user is told to ask administration to register their access (added in `staff-google-guard`). Distinct from `auth.google_failed`: the token is valid, there is simply nothing to claim. Distinct from the two codes below: there is no other door to send them to |
| `auth.use_patient_sign_in` | 403 | Google sign-in on the **staff** entry (S0) by an address that holds a **patient** account — no session; the user is sent to the patient portal (added in `staff-google-guard`) |
| `auth.use_staff_sign_in` | 403 | Google sign-in on the **patient portal** (P1) by an address that holds a **professional** account — no session, and an unclaimed invitation is **not** claimed; the user is sent to the staff console (added in `staff-google-guard`). Each surface admits only the role it serves, so a session is never established for someone every screen on it would refuse |
| `auth.google_unavailable` | 503 | this deployment has no Google client configured, so the federated path is off (added in `identity-session`) |
| `auth.consent_required` | 422 | a required consent has not been granted. **First used in `booking-core`**, as the booking gate: a patient must hold an *active* `DataProcessing` consent at the configured current version. Change 2 grants that consent at just-in-time provisioning and P7 lets a patient revoke it, so until change 5 revocation was possible with nothing checking it — this code closes that loop. Also returned when revoking a consent that is not in force |
| `auth.email_already_in_use` | 409 | staff account creation with a taken email |
| `auth.account_not_found` | 404 | an administrator acted on a staff account that does not exist (added in `identity-session`) |
| `auth.password_change_required` | 403 | the bootstrapped administrator must replace the supplied credential before doing anything else (added in `identity-session`) |
| `auth.current_password_invalid` | 401 | the current password offered on the change-password screen does not match — distinct from `auth.invalid_credentials` because that screen has no email field, so the remedy is one field rather than two (added in `clinic-catalog`) |

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
| `booking.slot_taken` | 409 | optimistic booking lost the race — the professional already holds an appointment over that time (I4). The P3 "taken" state |
| `booking.slot_blocked` | 409 | the professional has an internal `TimeBlock` over the requested time — the **booking direction** of I7 (added in `booking-core`). Deliberately not `slot_taken`: nobody took this slot, the professional declared themselves unavailable, so a patient told "someone was faster" would go looking for a race that did not happen. The mirror of `booking.block_overlaps_appointment`, which already named the other direction |
| `booking.patient_busy` | 409 | the patient already holds an appointment over that time (I6) — added in `booking-core`. Exists because the third exclusion constraint would otherwise have no answer; overloading `slot_taken` would tell a patient somebody else took a slot they are themselves standing in |
| `booking.outside_working_hours` | 422 | slot outside the professional's working hours |
| `booking.lead_time_violation` | 422 | inside minimum lead time |
| `booking.horizon_exceeded` | 422 | beyond scheduling horizon |
| `booking.specialty_mismatch` | 422 | professional lacks the appointment type's specialty (I2) |
| `booking.resource_unavailable` | 409 | no free resource of the required type |
| `booking.cutoff_passed` | 422 | reschedule/cancel inside the cancellation cutoff (F3, default 24 h) — first used in `booking-lifecycle`. The rule takes an *authority* rather than a role, so the same refusal admits a caller the cutoff does not apply to; the front-desk override that passes it is `booking-desk`'s, not this one's |
| `booking.appointment_not_found` | 404 | unknown appointment, on a path whose caller is entitled to distinguish absence from denial. **No patient path uses it**: a patient naming an appointment that is not theirs and one naming an id that never existed both get `auth.ownership_denied`, so the response cannot be used to discover which appointments exist — the same reasoning as `patient.not_found` above. Not "soft-deleted": `booking-core` gave `appointments` no such column, because the status *is* the history |
| `booking.appointment_not_changeable` | 409 | the appointment is already in a terminal state, so there is nothing left to cancel or move — added in `booking-lifecycle`. **Flagged for review**: the proposal claimed this change needed no new code, and it does. The catalogue had no honest answer — `appointment_not_found` would deny a row the patient can see on P5, `ownership_denied` is about who rather than about state, and `cutoff_passed` would give a time-based reason for a state-based refusal, which is the confusion `slot_blocked` was split from `slot_taken` to avoid. The same argument `booking.patient_busy` was added under |
| `booking.block_overlaps_appointment` | 409 | internal block collides with an active appointment (I7 refusal) |

### calendar — capability `calendar-integration`
| Code | Status | When |
|---|---|---|
| `calendar.not_connected` | 422 | professional has no calendar connection to act on — first used in `calendar-connection` (6a) by *check* and *disconnect*. **Not** what a professional who has simply never connected sees on S2: reading a state of "not connected" is a successful read of a real state, not a refusal |
| `calendar.consent_revoked` | 422 | the grant is gone on Google's side and we observed it — an `invalid_grant` from a refresh exchange. S2's revoked state, and the remedy is reconnecting |
| `calendar.sync_failed` | 503 | Google unreachable. **Deliberately not recorded as a revocation** (6a design K8): an outage that flipped a connection to revoked would tell a professional to reconnect something that is working. 6b reuses it for a failed dispatch |
| `calendar.scope_declined` | 422 | the authorization completed **without** calendar access — added in `calendar-connection` (6a). Google's consent screen is granular, so a professional can approve the request and untick calendar access while the token response stays perfectly valid; nothing about the redirect says the ask was refused. Distinct from `consent_revoked` because the two need different sentences: "you declined" invites granting permission, "it was revoked" invites reconnecting, and reporting one as the other sends the professional to the wrong action |
| `calendar.connect_failed` | 422 | the authorization returned no long-lived credential and none is held — added in `calendar-connection` (6a). Google issues a refresh token only on the first grant for a client/user pair, so a successful authorization can carry nothing; recording a connection anyway would mean a status of "connected" that 6b could never dispatch against |

A missing Google client reuses **`auth.google_unavailable`** rather than minting a calendar-specific twin: it is the same operator fact (this deployment has no Google client), and §5's rule is to reuse a code the catalogue already has.

### clinic configuration — capability `clinic-configuration`
| Code | Status | When |
|---|---|---|
| `config.in_use` | 409 | cannot deactivate an entity still referenced by active records (e.g. a `Specialty` with active `AppointmentType`s; a `ResourceType` with active `Resource`s or referenced by active `AppointmentType`s) — added in `clinic-catalog` |
| `config.duplicate_name` | 409 | a catalog entity with that name already exists (active) — added in `clinic-catalog` |
| `config.not_found` | 404 | referenced catalog/professional-config entity does not exist — added in `clinic-catalog` |
| `config.specialty_not_held` | 422 | assigning a per-type duration for an appointment type whose specialty the professional does not hold (I2 qualification gate) — added in `professional-configuration` |
| `config.working_hours_overlap` | 409 | two working-hour templates for the same `dayOfWeek` with overlapping effective ranges — added in `professional-configuration` |
| `config.working_hours_invalid` | 422 | working-hour segment with `startTime >= endTime` or crossing midnight — added in `professional-configuration` |

### time blocks — capability `availability` (internal) / `calendar-integration` (external)
| Code | Status | When |
|---|---|---|
| `block.invalid_range` | 422 | internal `TimeBlock` with `start >= end` (S3) — added in `availability-read` |

### generic
| Code | Status | When |
|---|---|---|
| `server.unexpected` | 500 | unhandled error (never leak internals) |

## 4. Rules

- One code per distinct, user-meaningful failure — not one per throw site.
- Add codes here **before** using them in a slice; the matching pt-BR/en i18n keys are part of that change's Definition of Done.
- Prefer codes the design already implies (the P2/P3/S2 states above map directly to `availability.unavailable`, `booking.slot_taken`, `calendar.consent_revoked`).