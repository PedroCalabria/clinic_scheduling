# Google OAuth Client — Setup Prerequisite

> **Purpose:** the one manual, out-of-repo step the federated login path needs.
> **Introduced by:** change 2 (`identity-session`).
> **Document language:** English.

---

## What this is for, and what it is not

This sets up **authentication** — "Sign in with Google" (OpenID Connect). It does **not** by
itself set up calendar access; that is a separate authorization concern with its own scopes,
requested via incremental authorization when a professional connects their calendar on S2.
Keeping the two apart is deliberate — see `01-requirements.md` §"Auth vs. calendar distinction".

**Change 6a (`calendar-connection`) shipped that second half**, and it needs two more manual
steps in the same Console project — see §"Turning on the calendar connection" below. The OAuth
client is the same one: it is one application asking for a second permission, not a second
application.

**You can skip this entirely and still run everything.** Without a Google client the app starts
normally, internal accounts (reception and administration) sign in as usual, and the Google
button reports `auth.google_unavailable`. CI never needs these values either: the federated path
is covered through substituted seams, so no secret is required to run the test suite.

## Do this now (before change 5)

This was originally optional and slated for change 6. It is now a **prerequisite for change 5**, because change 5 delivers the patient/professional screens (P2–P6, S1) that are reachable *only* via Google sign-in — including P2, the flagship. Two increments of human validation (change 4's deferred guide, then change 5's) otherwise stack up unexecuted on this one missing piece. It is cheap to remove: local sign-in needs no tunnel (Google treats `localhost` as a special case; the tunnel is a change-7 webhook concern).

Concrete checklist:

1. Do the OAuth-client setup below (§Steps) — ~5 minutes in the Google Cloud Console. Put the client id/secret in `.env`.
2. On the OAuth consent screen (still in *Testing*), add **your own Google account** and a **second Google account** as test users.
3. In S11 (staff → users), invite your own Google email as a **professional**. Sign in with Google to **claim** it → this is the professional path (S1 and, later, S2).
4. Sign in with the **second** Google account with no prior record → provisioned as a **patient** (JIT) → this is the patient path (P1–P6).
5. **Run change 4's deferred validation guide now** (`openspec/changes/availability-read/validation.md`) as the professional you just claimed — clearing the debt that was honestly deferred for lack of a Google client. Then check the box.

Note: the seeded `dra.helena@clinic.local` is dev fixture data on a non-existent domain and is **not** claimable via Google — it drives the API/solver, not browser sign-in. Human validation of Google screens uses the real accounts from steps 2–4.

## Steps

1. In the [Google Cloud Console](https://console.cloud.google.com/), create or select a project.
2. **APIs & Services → OAuth consent screen**: choose *External*, fill in the app name and support
   email. While the app is in *Testing*, add the Google accounts you intend to sign in with as test
   users — otherwise Google refuses them.
3. **APIs & Services → Credentials → Create credentials → OAuth client ID**, application type
   **Web application**.
4. Under **Authorized redirect URIs**, add the callback **exactly** as the app will send it:

   | Environment | Redirect URI |
   |---|---|
   | Local | `http://localhost:8080/api/auth/google/callback` |
   | Production | `https://<your-host>/api/auth/google/callback` |

   Plain HTTP is permitted here **because the host is `localhost`** — Google treats it as a
   special case, which is why local sign-in needs no tunnel. (A tunnel does become necessary in
   change 7, for inbound webhooks, which Google will not deliver to `localhost`.)

   The port must match `CADDY_HTTP_PORT`. The path must match exactly: Google compares the
   redirect URI string, so a trailing slash or a different port is a refused sign-in, not a
   warning.

5. Copy the client id and secret into `.env`:

   ```
   Auth__Google__ClientId=<client id>
   Auth__Google__ClientSecret=<client secret>
   Auth__Google__RedirectUri=http://localhost:8080/api/auth/google/callback
   ```

   The secret is a credential: it belongs in `.env` (git-ignored) or Docker secrets, never in the
   repository (`03-nfr.md` §2).

6. Restart the stack. The Google button on the patient portal and the staff sign-in screen now
   starts a real flow.

## What the app asks Google for

**The sign-in flow** asks for identity scopes only: `openid email profile`. There is no
`access_type=offline`, so Google returns no refresh token and the sign-in path stores no
long-lived Google credential. That is still true, and there is a test asserting it.

**The calendar flow** (6a) is separate and asks for `.../auth/calendar.events` with
`access_type=offline`, `prompt=consent` and — load-bearing — `include_granted_scopes=true`, so the
identity grant obtained at sign-in is retained rather than silently replaced. It is started only
by a professional pressing *Connect* on S2, never by signing in. **That flow does store a
long-lived credential, encrypted at rest**, which is why `Calendar__TokenEncryptionKey` exists.

## Who can sign in with it

| Google account matches | Result |
|---|---|
| No user record | Provisioned as a **patient**, with a data-processing consent recorded |
| A professional an administrator registered by that email (S11) | **Claims** that account, keeping its professional role |
| An internal staff account (reception / administration) | **Refused** (`auth.google_failed`) — staff sign in with their password, and controlling their mailbox must not be enough to sign in as them |

The email must be verified on Google's side. That check is what makes matching by email safe.

## Turning on the calendar connection (change 6a)

Two manual steps, both in the same Console project, both one-time. Skipping either fails
loudly — but the second one fails only at the *first check*, which is late enough to waste a
session.

1. **APIs & Services → Library → enable the Google Calendar API.** Without it, connecting appears
   to work and the first *Check connection* fails.
2. **Credentials → your OAuth client → add a second authorized redirect URI**, exactly:

   ```
   http://localhost:8080/api/calendar/connect/callback
   ```

   This is an **addition**, not a replacement. The sign-in URI stays: two flows, two callbacks,
   deliberately (change 6a design K2).

Then set both calendar values in `.env`. **Generate the key first** — it is a random value you
produce, not a literal to copy:

```bash
openssl rand -base64 32
```

That prints 44 characters ending in `=`, which decode to the 32 bytes AES-256 needs. Paste the
output as the value, with no quotes and no angle brackets:

```
Calendar__RedirectUri=http://localhost:8080/api/calendar/connect/callback
Calendar__TokenEncryptionKey=Ij/l6asmxtesb/h0ZEvjINZH3hAQ8WZ9OKPkD2S7WW8=
```

The key above is an **example of the shape** — generate your own; a key published in a document
protects nothing. If you have no `openssl` (it ships with Git for Windows), any of these does the
same job:

```powershell
# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))
```

```bash
# Python, anywhere
python -c "import os,base64; print(base64.b64encode(os.urandom(32)).decode())"
```

**If the value is wrong, the API says so and refuses to start** rather than starting without
protecting the credential — `Calendar__TokenEncryptionKey is not valid base64` for a placeholder
left unreplaced, or `decodes to N bytes; AES-256 needs exactly 32` for a value of the wrong length.

Setting `Calendar__RedirectUri` is what turns the feature on. Leave it empty and the app behaves
exactly as before; set it **without** the key and the API refuses to start, on purpose — a refresh
token is a long-lived credential and is never stored in clear.

**What a professional will see.** Google's consent screen for the calendar scope is *granular*:
it can be approved while the calendar tickbox is left unticked, and the flow still completes
successfully. The app checks the scopes actually granted and reports `calendar.scope_declined`
rather than recording a connection — worth knowing, because it is the easiest thing to do by
accident and the result looks like a failure that is not one.

**The test-user list applies to this flow too.** The consent screen is in *Testing*, so the account
connecting a calendar must be on the **Test users** list — the same list the sign-in flow needs.
Reaching a screen that says *"o app não concluiu o processo de verificação"* with
`Erro 403: access_denied` means that account is not on it: **Google Auth Platform → Audience →
Test users → Add users** (older Console: *OAuth consent screen → Test users*).

> ### The seven-day expiry, and why it is not a bug
>
> **While the app is in *Testing*, Google expires refresh tokens issued to test users after seven
> days.** Nothing in this system did it and nothing here can prevent it.
>
> What it looks like: a calendar that connected fine goes quiet, and the next *Check connection*
> reports the authorization as revoked (`calendar.consent_revoked`) — the same way a real
> withdrawal looks, because from our side it is indistinguishable. Google returns `invalid_grant`
> for both.
>
> The remedy is to reconnect, which is one click on S2. **Do not treat a connection that lapsed
> after about a week as a defect** — check what the publishing status is before investigating
> anything else. This also matters for `calendar-outbound` (6b): its dispatcher will meet the same
> expiry and must report it as a connection needing reconnection rather than as a failed
> appointment.
>
> Publishing the app (moving out of *Testing*) removes the seven-day limit, but for a portfolio
> project that means submitting a sensitive scope for Google's verification review, which is a
> deliberate non-goal. Living with the expiry is the right trade here — it just has to be known.

**Revoking, for validating the revoked state.** Go to
[myaccount.google.com/permissions](https://myaccount.google.com/permissions), find the app, and
remove access. Then press *Check connection* on S2: the status becomes revoked and reconnection is
offered. Nothing detects this on its own in 6a — there is no scheduler until 6b — which is why the
screen shows *when* it last checked.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `redirect_uri_mismatch` on Google's page | The registered URI differs from what the app sent — check the port, scheme, and exact path |
| Sign-in returns to the app with `authError=auth.google_unavailable` | No client id or secret is configured in the environment the API is running in |
| Sign-in returns with `authError=auth.google_failed` | The state cookie expired (the flow sat unfinished for more than 10 minutes), the callback was replayed, the email is unverified, or the address belongs to an internal account |
| Sign-in succeeds and the app immediately behaves as signed-out | The session cookie is `Secure` and the app is being served over plain HTTP on a hostname that is **not** `localhost` — browsers drop it silently |
| Google refuses the account entirely | The consent screen is in *Testing* and the account is not on the test-user list |
| `access_denied` / "não concluiu o processo de verificação" when **connecting a calendar** | Same cause, same fix: that account is not a **test user**. The list applies to the calendar flow exactly as it does to sign-in |
| A calendar that was connected reads as **revoked** after about a week, with nobody having revoked anything | Expected while the app is in *Testing*: Google expires test users' refresh tokens after seven days. Reconnect on S2. See the note above before investigating |
| `redirect_uri_mismatch` when **connecting a calendar** (not signing in) | The second redirect URI is not registered, or differs from `Calendar__RedirectUri`. The sign-in URI being correct says nothing about this one |
| S2 connects, then *Check connection* fails | The Google Calendar API is not enabled on the project |
| S2 reports `calendar.scope_declined` after what looked like a successful approval | The calendar tickbox was left unticked on the granular consent screen. Connect again and allow calendar access |
| The API refuses to start naming `Calendar__TokenEncryptionKey` | `Calendar__RedirectUri` is set without a key, or the key is not 32 bytes of base64. Deliberate: a configured calendar with no key would otherwise store a refresh token in clear |
| Every professional is suddenly asked to reconnect | `Calendar__TokenEncryptionKey` changed. Stored credentials are unreadable under a different key; there is no recovery but reconnecting |
