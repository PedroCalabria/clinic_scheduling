# Validation guide — calendar-connection

Manual checks a human runs against the locally-running app (`00-context.md` §9).

> **Check 13 was added after the guide was written**, when the maintainer decided that disabling
> an account should withdraw its calendar grant (design K16). It is the only check here that needs
> the administrator account as well as the professional one.

**What this guide is for, and why it carries more than usual.** Every automated tier in this change
talks to a **stubbed Google**. The handler substitution from change 2 is what keeps CI free of
secrets, and it is exactly what makes these checks irreplaceable: the tests prove the code does the
right thing with the response it was handed, and nothing at all about whether Google hands back that
response. The consent screen, the granular permission tickbox, the second redirect URI, the refresh
token that only arrives on a first grant, and `invalid_grant` after a real revocation are **all**
outside what any tier can see.

**The demonstration, stated once so the run can be judged against it.** A professional connects
their own Google Calendar from a screen in the staff console, the screen says so, and — this is the
half that matters — when they take that permission away in their Google account, the screen tells
them the truth about it. Nothing is written to any calendar in this change; if a check makes you
expect an event to appear, the check is wrong, not the build.

**A second thing to expect that is not a defect.** While the Google app is in *Testing*, refresh
tokens issued to test users **expire after seven days**. A connection made more than a week before
the run will read as revoked, and Google's answer is indistinguishable from a real withdrawal.
Reconnect and carry on — and if that happens mid-run, say so in the Outcome rather than recording
it as a failure of check 4 (`08-google-setup.md`, "The seven-day expiry").

**One thing to expect that is not a defect.** Between two presses of "check connection", S2 can show
"connected" for a grant revoked minutes ago (design K8). That is why the screen states *when* it last
looked. Judge whether that reads as honest or as broken — check 11 asks you directly.

## Setup

```bash
cp .env.example .env
# fill in: the Google client id and secret (docs/08-google-setup.md)
#          Calendar__TokenEncryptionKey  — the generation command is in .env.example
#          Calendar__RedirectUri         — http://localhost:8080/api/calendar/connect/callback
docker compose -f infra/docker-compose.yml --env-file .env up -d --build
```

- Staff console at `http://localhost:8080/staff/`.
- Set `Clinic__SeedDevelopmentData=true`.
- **The two manual Google Console steps must already be done** (design K14, task 0.1): the calendar
  redirect URI registered *exactly* as above, and the Google Calendar API enabled on the project.
  Skipping the first fails at the consent screen; skipping the second fails only at the first probe.

**Accounts you need — read this before starting.**

- A **real Google account you control**, invited as a professional in S11 and claimed by signing in
  on S0 (`08-google-setup.md` steps 2–4). This is the account whose calendar you will connect **and
  then revoke**. Pick one you are willing to do that to.
- The seeded `dra.helena@clinic.local` **cannot be used**: a non-existent domain is not claimable
  through Google. She exercises the API and the solver, never the browser sign-in.
- A **front-desk** internal account and the **administrator**, for checks 8, 9 and 13.
- **Every Google account used here must be on the consent screen's Test users list**, including for
  the calendar flow — an account missing from it is refused with `access_denied` before any of this
  is reachable.
- The claimed professional needs a `Professional` row (save them once in S7) — see check 12 for what
  happens when they do not have one.

---

## Checks

### 1 — The connect flow reaches Google, with the identity grant intact

Sign in on S0 as the claimed professional. Open **`/staff/calendar`**. Press **Connect**.

- **Expect:** Google's consent screen, naming calendar access.
- **Then:** approve it. You land back on S2 and it reads **connected**, with a "last checked" moment.
- **The half that is easy to miss:** after returning, **reload the staff console**. You must still be
  signed in. If the session is gone or a later screen misbehaves, `include_granted_scopes=true` did
  not do its job and the identity grant was replaced — the exact trap `StartGoogleSignIn` has warned
  about since change 2.

### 2 — What is actually in the database

With the connection established:

```bash
docker compose -f infra/docker-compose.yml --env-file .env exec db \
  psql -U <user> -d <db> -c 'select professional_id, status, left(refresh_token_sealed, 12) from calendar_connections;'
```

- **Expect:** the column begins with the envelope version prefix (`v1.`) and is plainly not a Google
  token. A Google refresh token starts `1//`.
- **This is the single fact the whole change exists to make true.** A test asserts it too; look at it
  once with your own eyes anyway.

### 3 — The declined-scope path *(the most likely real failure)*

Disconnect (check 7), then press **Connect** again. On Google's consent screen, **untick the calendar
permission** while approving the rest, if Google offers it granularly for this client.

- **Expect:** S2 reports that calendar permission was **declined** — not that it was revoked, and not
  a generic failure. The action offered is to grant permission, not to reconnect.
- **Then check the database:** no connection row in a connected state, and **no calendar consent
  row**. Nothing is stored for a permission that was refused.
- **If Google does not offer the tickbox for this client**, say so in the Outcome and mark the check
  unrun rather than passed. The code path is tested; what is unverified is that Google can produce it.

### 4 — Revoke at Google, then come back *(the check with no substitute)*

With the calendar connected, go to
**[myaccount.google.com/permissions](https://myaccount.google.com/permissions)**, find this app, and
**remove access**.

- Return to S2 and press **Check connection**.
- **Expect:** the status becomes **revoked**, the observed moment updates, and the screen offers
  **reconnect** with a translated message.
- **Not expected:** an unhandled error, a generic failure, or a screen still claiming "connected".

### 5 — Reconnect after a revocation

Press **Reconnect** and complete the flow.

- **Expect:** connected again.
- **Then check the database:** still **exactly one** row in `calendar_connections` for this
  professional, and **two** calendar consent rows — the first revoked, the second granted. The
  revoked one still records that it was once granted.
- This is the "record, don't erase" rule that `identity-session` established, meeting a second
  consent type for the first time.

### 6 — Google unreachable is not the same as revoked

Reconnect first. Then take the API off the internet — the simplest reliable way is to stop the
stack's outbound access (disconnect the host from the network for a moment, or point
`Calendar__TokenEndpoint` at an unroutable address and restart the API). Press **Check connection**.

- **Expect:** a message that the check could not be completed, and the status **unchanged** — still
  connected, with its **previous** observed moment.
- **Not expected:** the status flipping to revoked. Recording an outage as a revocation would tell a
  professional to reconnect something that is fine.

### 7 — Disconnect is a real withdrawal

With everything restored and connected, press **Disconnect** and confirm the dialog.

- **Expect:** the screen reports disconnected.
- **Then check the database:** the row still exists, its status is disconnected, and the sealed
  credential column is **null**. The record of having been connected survives; the key to their
  calendar does not.
- **Then check Google:** the app is **gone** from
  [myaccount.google.com/permissions](https://myaccount.google.com/permissions). If it is still
  listed, the revoke call failed — and in that case S2 must have told you so rather than reporting
  plain success. Which of the two happened is the finding; record it either way.

### 8 — Reception and administration have no calendar screen

Sign in as the **front-desk** account, then as the **administrator**.

- **Expect:** no calendar entry in the navigation for either.
- **Then type `/staff/calendar` into the address bar** for each. The route must refuse, not render an
  empty or broken screen.

### 9 — Nobody connects a calendar on somebody else's behalf

Still as the administrator, look for any surface — S7, S11, anywhere — that offers to connect,
inspect, or disconnect a professional's calendar.

- **Expect:** there is none. The grant is the professional's to give.

### 13 — Disabling a professional takes their calendar back *(design K16)*

Reconnect the calendar first. Then, as the **administrator**, open S11 and **disable** the
professional's account.

- **Then check Google:** the app is **gone** from
  [myaccount.google.com/permissions](https://myaccount.google.com/permissions). Their access to
  this system ending should have ended the clinic's access to their calendar, in the same action.
- **Then check the database:** their connection reads `Disconnected` with a null credential, and
  their `CalendarSync` consent is revoked.
- **Expect no error and no delay** in S11 — the withdrawal is part of the action, not a second
  step the administrator has to remember.
- **Note the consequence, and judge whether it is acceptable:** if that professional is ever
  restored, they must reconnect. Disabling is described elsewhere as a reversible off-switch, and
  this makes one part of it irreversible on purpose. Say whether that reads as right.

### 10 — Keyboard and both locales

In **pt-BR** and in **en**, on S2:

- Every label, status, action, the confirmation dialog and every refusal message is translated. No
  raw key, no English string in the pt-BR view.
- Reach and operate every control by keyboard alone, including the disconnect confirmation: focus
  enters the dialog, Escape closes it, and focus returns to the button that opened it.

### 11 — A judgement, not a check

Look at S2 as the professional whose calendar it is.

- Does "connected, last checked at 14:02" read as **honest**, or as a screen that does not know what
  is going on?
- Does the never-connected state read as a **state**, or as the screen telling you off for something
  that currently has no benefit? (Design Open Question 4 is waiting on this answer.)
- Write the opinion down even if it is favourable. `booking-desk`'s check 1 came back answered and
  favourable, and that was worth having.

### 12 — A claimed professional with no `Professional` row

Invite a **second** real Google account as a professional in S11, claim it by signing in, and — without
saving anything in S7 — open `/staff/calendar`.

- **Expect:** something useful and translated, not an error page and not a blank screen.
- Design Open Question 3 predicts this case exists. Whatever it does, the finding decides whether the
  connection is keyed on the `Professional` row or on the `User`. If a second Google account is not
  available, mark this unrun — do not guess it.

---

## Outcome — run 2026-08-25/26

**All thirteen checks pass.** The run is worth reading for what happened *before* they did: four
defects were found while getting the guide to the point where it could be executed at all, and
not one of them was reachable by any automated tier.

| # | Result |
|---|---|
| 1 · connect, identity grant intact | Passed |
| 2 · the stored value is not the token | Passed |
| 3 · declined scope | Passed |
| 4 · revoke at Google, then check | Passed |
| 5 · reconnect, one row, two consents | Passed |
| 6 · unreachable ≠ revoked | Passed |
| 7 · disconnect withdraws at Google too | Passed |
| 8 · no calendar screen for other roles | Passed |
| 9 · nobody connects on another's behalf | Passed |
| 10 · keyboard and both locales | Passed — the keyboard pass included the disconnect dialog's focus handling, closing task 9.6 |
| 11 · judgement | Passed |
| 12 · professional with no configuration row | Passed |
| 13 · disabling withdraws the grant | Passed |

### What the run found before it could pass

Four defects, in the order they surfaced. **The first three were in the setup this guide
prescribes, and the fourth was a shipped defect in another capability** — which is the finding
worth keeping, because it says the guide's value is not only in its checks.

1. **The encryption key was a placeholder taken literally.** `docs/08-google-setup.md` wrote it as
   `<openssl rand -base64 32>`, which reads as a value to copy rather than a command to run. The
   API refused to start, exactly as designed (K4) — the failure was loud and named the setting, so
   the design held; the documentation was what misled. Rewritten as a command with an example of
   the *shape* of the result, plus alternatives for machines without `openssl`.
2. **`redirect_uri_mismatch`** — task 0.1, the second redirect URI, not yet registered. Not a
   defect; the manual step doing what it does when skipped.
3. **`access_denied`** — the Google account was not on the consent screen's **Test users** list.
   Also not a defect, but undocumented for the calendar flow, and now a troubleshooting row. It
   surfaced a genuine operational fact along the way: **while the app is in *Testing*, Google
   expires test users' refresh tokens after seven days**, so a connection will read as revoked
   about a week later with nobody having revoked anything. Documented, because otherwise it looks
   like a 6a defect — and 6b's dispatcher will meet the same expiry.
4. **`{"code":"server.unexpected"}` at the end of a real Google sign-in** — a shipped defect in
   `identity-session`, found by this guide and fixed in this change (task group 14). Deactivating
   an account released its **address** but not its **Google identity**, so the product's own
   documented recovery path — deactivate-and-invite-anew — was impassable for any Google account
   that had already been claimed. The code and the index disagreed about what deactivation means,
   and the index was the one that was wrong. Nothing covered the path; there is now a test.

### What was not examined

- **The seven-day refresh-token expiry was not waited out.** Its effect is understood and
  documented but has not been observed in this deployment.
- **Key rotation** was not exercised. Open Question 1 was answered by accepting that reconnecting
  is the recovery, so there is no procedure to test — but nothing here has confirmed that a
  changed key produces the reported state on a running stack rather than only in a unit test.
- **The stale-status window (K8) was not observed at length.** Check 11 judged the screen's
  honesty about it; nobody watched a connection sit stale for hours.

### What this run is worth saying about

**Every automated tier in this change talks to a stubbed Google, and every one of the four defects
above lived outside what they can see.** Three were in configuration and documentation — the
things a test cannot have an opinion about — and the fourth was a database constraint disagreeing
with the code above it, reachable only by running the real flow end to end.

The fourth deserves the last word. `00-context.md` §5 has described deactivate-and-invite-anew as
the recovery path since change 2, and `staff-google-guard` built the surface for it. It had never
been run all the way through by a person, so nobody had discovered that its last step could not
complete. The guide did not find a bug in what this change built; it found a bug in what an
earlier change had claimed, by being the first thing to actually walk the path.
