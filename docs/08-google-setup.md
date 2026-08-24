# Google OAuth Client — Setup Prerequisite

> **Purpose:** the one manual, out-of-repo step the federated login path needs.
> **Introduced by:** change 2 (`identity-session`).
> **Document language:** English.

---

## What this is for, and what it is not

This sets up **authentication** — "Sign in with Google" (OpenID Connect). It does **not** set up
calendar access; that is a separate authorization concern with its own scopes, requested later
via incremental authorization when a professional connects their calendar (change 6,
`calendar-integration`). Keeping the two apart is deliberate — see `01-requirements.md`
§"Auth vs. calendar distinction".

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

Identity scopes only: `openid email profile`. There is no `access_type=offline`, so Google returns
no refresh token, and this change stores no long-lived Google credential — which is also why it
needs no token-encryption key yet.

## Who can sign in with it

| Google account matches | Result |
|---|---|
| No user record | Provisioned as a **patient**, with a data-processing consent recorded |
| A professional an administrator registered by that email (S11) | **Claims** that account, keeping its professional role |
| An internal staff account (reception / administration) | **Refused** (`auth.google_failed`) — staff sign in with their password, and controlling their mailbox must not be enough to sign in as them |

The email must be verified on Google's side. That check is what makes matching by email safe.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `redirect_uri_mismatch` on Google's page | The registered URI differs from what the app sent — check the port, scheme, and exact path |
| Sign-in returns to the app with `authError=auth.google_unavailable` | No client id or secret is configured in the environment the API is running in |
| Sign-in returns with `authError=auth.google_failed` | The state cookie expired (the flow sat unfinished for more than 10 minutes), the callback was replayed, the email is unverified, or the address belongs to an internal account |
| Sign-in succeeds and the app immediately behaves as signed-out | The session cookie is `Secure` and the app is being served over plain HTTP on a hostname that is **not** `localhost` — browsers drop it silently |
| Google refuses the account entirely | The consent screen is in *Testing* and the account is not on the test-user list |
