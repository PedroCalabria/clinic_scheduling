# Validation guide — staff-google-guard

Manual checks a human runs against the locally-running app (`00-context.md` §9). Everything here
is deliberately **outside** what the test suite can assert: a real Google account arriving at a
real browser, both-locale rendering of the refusal, and the recovery an administrator performs
by hand in S11.

Short on purpose. The provisioning rule itself is integration-tested from both surfaces, so
what is left for a person is the half CI cannot reach: **this change exists because of something
that happened in a browser with a real Google client, and it is not done until the same sequence
is run again and comes out differently.**

An earlier draft of this guide had six checks. Running it produced check 4 — the same defect on
the patient portal, in the other direction — which is the clearest argument available for why
§9 of `00-context.md` exists.

Check 5 is the one that matters most. It is the incident from change-4 validation, replayed
forward, and it also clears the account that incident left behind.

## Setup

```bash
cp .env.example .env
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- App at `http://localhost:8080`, staff console at `/staff`, staff sign-in at `/staff/login`.
- **A configured Google OAuth client is required** (`08-google-setup.md`). Without one every
  check here is unrunnable as written; record that rather than substituting the internal
  administrator, because the whole subject is the Google door.
- **Two Google accounts you control are needed**, and it matters which is which:
  - **Account A** — never invited to this clinic, and **holding no account here at all**. If it
    was used in change-4 validation it now holds a patient account; either use a third address
    or run check 5 first, which clears it.
  - **Account B** — used for the patient path. It may already be a patient; that is fine.
- Sign in as the bootstrap administrator (`.env`) for the S11 steps.
- Language is switched with the control in the top bar / on the sign-in card. **Every check below
  is run in both pt-BR and en**, and the point of doing so is the *sentence*, not the layout.

---

## 1 — An un-invited Google account is refused on S0, and told what to do

| | |
|---|---|
| Role | Nobody — a visitor |
| Route | `/staff/login` |

**Action.** With Account A signed out of nothing in particular, open `/staff/login`, click
"Sign in with Google", and complete Google's consent with **Account A**.

**Expect.** You land back on the staff sign-in screen — not on a blank console, not on the
patient portal, and not looking at a JSON body in the address bar. An error alert on the card
says, in the language you selected, that the clinic has not registered an account for this
address and that administration must do so. Nothing invites you to try again as though a retry
would help.

Switch language and repeat. Both sentences must read as instructions to a person, not as
translated error jargon.

**Why a human.** The integration suite asserts the code and the absence of a row. It cannot tell
you whether the sentence a locked-out professional reads is usable.

## 2 — The refusal created nothing

| | |
|---|---|
| Role | Administrator |
| Route | `/staff/users` |

**Action.** Signed in as the administrator, look at S11. Then check the database directly:

```bash
docker compose -f infra/docker-compose.yml exec db \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -c "select email, role, status, deleted_at_utc from users where email = 'ACCOUNT_A@example.com';"
```

**Expect.** Zero rows. Not a patient row, not a disabled row, nothing. S11 shows no new entry.

**Why a human.** This is the actual defect. A test asserts it in a transaction that is rolled
back; this asserts it in the database the browser just talked to.

## 3 — The patient portal is unchanged

| | |
|---|---|
| Role | Nobody — a visitor |
| Route | `/` |

**Action.** Open the patient portal and sign in with Google using **Account B**. If Account B is
already a patient here, use any further Google account you control that is not — the check is
about an *unknown* address on P1.

**Expect.** You are signed in as a patient and can reach P7 (your profile), which shows your
minimal data and your data-processing consent. No refusal, no "ask administration". The two
surfaces genuinely diverge.

Then, still with that same account, go to `/staff/login` and sign in with Google there.

**Expect.** Refused — and **not** with check 1's "ask administration" message. This one says you
hold a *patient* account and should use the patient portal, because nothing about your access
needs registering; you are simply at the wrong entrance. Your patient account is untouched: sign
back in on the portal and your profile and consents are exactly as they were.

This is the one property no single-surface check can show: the *same identity*, valid on one door
and refused at the other. Read both refusals side by side and confirm they are different
sentences — if they have collapsed into one, somebody has merged the codes.

## 4 — A professional is turned away at the patient portal

| | |
|---|---|
| Role | A professional — the account this clinic registered |
| Route | `/` |

**Action.** Sign out completely. Open the patient portal at `/` and click "Sign in with Google",
completing it with the Google account that is registered here as a **professional** (the one
check 5 creates, or one already invited).

**Expect.** You do **not** get in. The landing page shows a message saying this account is
registered as a clinic professional and to use the staff console instead. You are not signed in —
there is no "Signed in as…" line and no "Sign out" in the header.

Then go to `/staff/login` and sign in with the same account: it works, and lands you in the
console.

**Why this check exists.** This is the defect that came out of running an earlier draft of this
guide. The portal used to *admit* a professional, and the first screen they reached said "no such
patient record" — technically correct, since a professional has no patient row, and useless as an
answer. If you ever see that message again on the portal, this fix has regressed.

**Also confirm nothing was consumed.** If the account you used was still an *unclaimed*
invitation, check S11 afterwards: it must still show **Awaiting first sign-in**. The portal must
not claim an invitation on its way to refusing the sign-in.

Both locales, for the refusal message.

## 5 — Recovery: the address is freed, invited, and claimed

| | |
|---|---|
| Role | Administrator, then the professional |
| Route | `/staff/users`, then `/staff/login` |

This is the incident from change-4 validation, run forward. Use the address that incident turned
into a patient by mistake — if there isn't one, make one first by signing in on **P1** with
Account A, so the check runs against a genuine accidental patient.

**Action.**

1. As the administrator in S11, invite that address as a **Professional**.
2. Expect the refusal: another account already uses that email. A panel appears under the form
   offering **"See which account uses it"** — click it, and confirm it reports the **patient**
   role and an active status.
3. Read the panel before clicking anything else. It states the address, the role and the status
   it is about to retire, and says access ends, the address is freed, the history is kept and no
   role is changed. Then press **"Retire this account and free the address"**. Two separate
   clicks, and neither of them registers anything — if the panel ever grows a single
   "deactivate and invite" button, that is the role change this system deliberately does not
   have, and this check has failed.
4. Invite the address as a Professional again. It succeeds, and S11 lists it as a professional
   invitation awaiting its claim.
5. Sign out. At `/staff/login`, sign in with Google using that same account.

**Expect.** The claim succeeds and you land inside the staff console as a professional. In the
database, two rows now hold that address: the old one soft-deleted (`deleted_at_utc` set), and a
new one with `role = 'Professional'` and `status = 'Active'`. The old row's history was not
rewritten and no role was changed anywhere.

Run steps 1–4 once in each language, so the confirmation copy and the refusal copy are both read
in pt-BR and en.

**Why a human.** Every step is individually integration-tested. What is not testable is whether
an administrator confronted with "that email is taken" can find their way out of it without
being told how — and whether the confirmation dialog reads like retiring an account rather than
promoting a patient.

## 6 — The internal-account mistake still reads the way it did

| | |
|---|---|
| Role | Front desk or administrator |
| Route | `/staff/login` |

**Action.** At the staff sign-in, click "Sign in with Google" while intending to use an internal
account — i.e. complete Google with an address that belongs to an internal staff account here.
(Create a front-desk account in S11 using a Google address you control if you need one.)

**Expect.** Refused with the *Google sign-in did not complete* message that also points you at
the email-and-password form — **not** the "ask administration" message. The two refusals are
different sentences because they have different remedies, and this check is the one that catches
them being collapsed into one.

Both locales.

## 7 — The refusal survives a deep link

| | |
|---|---|
| Role | Nobody — a visitor |
| Route | `/staff/users` (directly), then Google |

**Action.** While signed out, type `/staff/users` into the address bar. You are sent to the staff
sign-in screen. From there, sign in with Google using **Account A** (still un-invited, if check 5
has not consumed it — otherwise any un-invited address).

**Expect.** The refusal message appears on the sign-in card. It does **not** vanish because the
browser was bounced through a guarded route on its way back.

**Why a human.** This is the query-string-preserving fix in `RequireAuth` (design D6). It is
invisible to the API tests, and its symptom is silence — an alert that simply never renders.

---

## Result

| # | Check | pt-BR | en | Notes |
|---|---|---|---|---|
| 1 | Un-invited account refused on S0 | ☐ | ☐ | |
| 2 | Refusal created no row | ☐ | — | |
| 3 | P1 unchanged; same identity sent back at S0 | ☐ | ☐ | |
| 4 | Professional turned away at P1, invitation intact | ☐ | ☐ | |
| 5 | Deactivate, re-invite, claim | ☐ | ☐ | |
| 6 | Internal-account refusal still distinct | ☐ | ☐ | |
| 7 | Refusal survives a deep link | ☐ | ☐ | |

The change is not done until this table is filled in (`00-context.md` §7, §9).
