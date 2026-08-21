## ADDED Requirements

### Requirement: Internal accounts authenticate into an app-owned session

Staff members holding an internal account SHALL authenticate with email and password and receive the application's own session. The password SHALL be stored only as a verifier produced by a deliberate password-hashing function, never reversibly. A failed attempt MUST NOT reveal whether the email exists.

#### Scenario: Valid internal credentials

- **WHEN** a user submits an email and password matching an active internal account
- **THEN** the API establishes a session for that user and returns the session credential in an `HttpOnly`, `Secure`, `SameSite` cookie
- **AND** the response body contains no password hash and no session identifier

#### Scenario: Wrong password

- **WHEN** a user submits a valid email with an incorrect password
- **THEN** the API responds `401` with code `auth.invalid_credentials` and establishes no session

#### Scenario: Unknown email is indistinguishable from a wrong password

- **WHEN** a user submits an email that no account uses
- **THEN** the API responds `401` with code `auth.invalid_credentials` — the same code, status, and shape as a wrong password

#### Scenario: Disabled account

- **WHEN** a user submits correct credentials for an account whose status is disabled or locked
- **THEN** the API responds `403` with code `auth.account_disabled` and establishes no session

### Requirement: Google sign-in resolves to the same app-owned session

Patients and professionals SHALL authenticate through Google using a server-side authorization-code flow, and SHALL receive the same kind of session an internal account receives. The Google ID token MUST be validated for signature against the provider's published keys and for its `iss`, `aud`, and `exp` claims before any session is established. No Google token SHALL be exposed to the browser or persisted by this capability.

#### Scenario: Valid Google identity

- **WHEN** the Google callback presents an authorization code that resolves to an ID token passing signature, `iss`, `aud`, `exp`, and `nonce` validation
- **THEN** the API establishes a session for the corresponding user and returns the same session cookie shape as the internal path
- **AND** every downstream endpoint treats that session identically to an internal-account session, without knowing which path produced it

#### Scenario: Invalid ID token

- **WHEN** the ID token fails signature, issuer, audience, or expiry validation
- **THEN** the API establishes no session and returns the browser to the sign-in surface reporting `auth.google_failed`, so the visitor sees a translated explanation rather than a raw response body

#### Scenario: Federated sign-in is not configured

- **WHEN** a visitor starts Google sign-in against a deployment that has no Google client configured
- **THEN** the request is refused with `auth.google_unavailable` and no session is established
- **AND** the application still starts and the internal-account path still works, so a missing Google client degrades one login path rather than stopping the system

#### Scenario: Only login scope is requested

- **WHEN** the sign-in flow redirects a professional to Google
- **THEN** the request asks only for identity scopes, and the flow stores no refresh token and no calendar authorization

### Requirement: A user's role is provisioned deterministically, never inferred

The system SHALL determine a role at the moment a user first comes into existence and SHALL NOT infer it from the identity provider. A Google sign-in whose email is unknown SHALL create a patient. A Google sign-in whose email matches a user an administrator pre-created for a professional SHALL claim that existing user rather than creating a second one. Role SHALL NOT change as a side effect of signing in.

#### Scenario: Unknown Google email becomes a patient

- **WHEN** a Google sign-in succeeds for an email no user record holds
- **THEN** the system creates a user with the patient role and its associated patient record holding only the minimal personal data the provider supplied

#### Scenario: Pre-invited professional claims the prepared account

- **WHEN** a Google sign-in succeeds for an email an administrator previously registered as a professional
- **THEN** the system links the Google subject identifier to that existing user, preserving its professional role
- **AND** no patient record is created for that user

#### Scenario: Subsequent sign-in reuses the same user

- **WHEN** a user who has signed in before signs in again through the same provider
- **THEN** the system resolves the existing user by the stored subject identifier and creates no duplicate user, patient, or role change

#### Scenario: An internal account cannot be claimed through Google

- **WHEN** a Google sign-in succeeds for an email belonging to an internal-account user
- **THEN** the system establishes no session and reports `auth.google_failed`, so a staff account cannot be taken over by controlling its email at the provider
- **AND** because a staff member clicking the wrong sign-in button is an ordinary mistake, the refusal returns them to the sign-in surface with a translated message rather than a raw response body

### Requirement: The session table is the authority, and revocation is immediate

The session credential in the cookie SHALL be an opaque identifier carrying no authorization data. Every authenticated request SHALL resolve the caller by looking up the stored session, so that a session's validity is never read from a copy held by the client. Revoking a session SHALL take effect on the next request it is used for.

#### Scenario: Revocation is effective on the next request

- **WHEN** a session is revoked and the same cookie is then used on a subsequent request
- **THEN** the API responds `401` with code `auth.session_expired` and performs no work on the caller's behalf

#### Scenario: Expired session

- **WHEN** a request presents a session whose expiry has passed
- **THEN** the API responds `401` with code `auth.session_expired` and treats the session as no longer usable, regardless of any later change to the stored row

#### Scenario: Sign-out revokes the session

- **WHEN** an authenticated user signs out
- **THEN** that session becomes unusable for any further request, and the session cookie is cleared from the browser

#### Scenario: Forged or unknown session credential

- **WHEN** a request presents a session identifier that no stored session matches
- **THEN** the API responds `401` with code `auth.session_expired` and discloses nothing about which sessions exist

### Requirement: Endpoints require authentication unless explicitly opened

Endpoints SHALL require an authenticated session by default; an endpoint reachable anonymously MUST be so by an explicit, visible decision. Introducing authentication MUST NOT close an endpoint that was previously and intentionally anonymous.

#### Scenario: Protected endpoint without a session

- **WHEN** an unauthenticated request reaches an endpoint that has not been explicitly opened
- **THEN** the API responds `401` with code `auth.session_expired`

#### Scenario: Health endpoint remains anonymous

- **WHEN** an unauthenticated client requests `GET /api/health`
- **THEN** the request succeeds exactly as it did before authentication existed

#### Scenario: Sign-in endpoints are reachable anonymously

- **WHEN** an unauthenticated client requests a sign-in or Google-callback endpoint
- **THEN** the request is accepted for processing rather than refused for lack of a session

### Requirement: Role-based authorization restricts actions

The system SHALL authorize actions by the caller's role, expressed as declarative policy rather than checks scattered through handlers. A caller whose role lacks a permission SHALL be refused even when authenticated, and the refusal SHALL be distinguishable from being unauthenticated.

#### Scenario: Role lacks the permission

- **WHEN** an authenticated front-desk user attempts an administrator-only action, such as creating a staff account
- **THEN** the API responds `403` with code `auth.forbidden` and the action does not occur

#### Scenario: Role holds the permission

- **WHEN** an authenticated administrator performs the same action
- **THEN** the action succeeds

#### Scenario: Authenticated-but-forbidden is distinct from unauthenticated

- **WHEN** the same forbidden request is made without a session and then with a session belonging to an insufficient role
- **THEN** the first is refused `401` with `auth.session_expired` and the second `403` with `auth.forbidden`

### Requirement: Patient data is protected by ownership, not only by role

Access to a patient's personal data SHALL additionally require that the caller owns that data. The system SHALL NOT trust a client-supplied identifier to establish ownership; ownership SHALL be derived from the authenticated session. Holding the patient role SHALL NOT by itself grant access to any patient record.

#### Scenario: Patient reads their own data

- **WHEN** an authenticated patient requests their own profile and consents
- **THEN** the request succeeds and returns only that patient's data

#### Scenario: Patient reads another patient's data

- **WHEN** an authenticated patient requests a profile identified as another patient's
- **THEN** the API responds `403` with code `auth.ownership_denied` and discloses nothing about whether that record exists

#### Scenario: Patient updates another patient's data

- **WHEN** an authenticated patient attempts to modify another patient's profile or consents
- **THEN** the API responds `403` with code `auth.ownership_denied` and no data is modified

#### Scenario: Client-supplied identifier cannot override the session

- **WHEN** a request carries a patient identifier that differs from the one the session resolves to
- **THEN** the session's identity governs, and the mismatched identifier never widens access

### Requirement: Staff access to patient personal data is recorded

When a staff member accesses a patient's personal data, the system SHALL record who accessed which patient's data, what action was taken, and when. A patient accessing their own data SHALL NOT be recorded.

#### Scenario: Staff access is logged

- **WHEN** a front-desk user or administrator reads a patient's personal data
- **THEN** an access record is stored identifying the acting user, the patient, the action, and the time of access

#### Scenario: Self-access is not logged

- **WHEN** a patient reads their own personal data
- **THEN** no access record is created

### Requirement: Consent is captured and versioned

The system SHALL record a patient's data-processing consent with the version consented to and the moment it was granted, and SHALL allow a consent to be shown as revoked without erasing that it was once granted. Consent records SHALL be visible to the patient they belong to.

#### Scenario: Consent granted at first sign-in

- **WHEN** a patient record is created for a newly provisioned user
- **THEN** a data-processing consent is recorded with its version and the time it was granted

#### Scenario: Consent revoked without losing history

- **WHEN** a patient revokes a consent
- **THEN** the record is marked revoked with the time of revocation, and the original grant remains recorded rather than deleted

#### Scenario: Action requiring an ungranted consent

- **WHEN** an action requires a consent the user has not granted
- **THEN** the API responds `422` with code `auth.consent_required`

### Requirement: Administrators manage staff accounts and professional invitations

An administrator SHALL be able to create internal accounts for front-desk and administrator users, register a professional by the email that user will sign in with, and disable an account. Email SHALL be unique across users. Disabling an account SHALL prevent new sessions and SHALL revoke that user's existing sessions. Accounts SHALL be soft-deleted only.

#### Scenario: Administrator creates an internal staff account

- **WHEN** an administrator creates a front-desk account with an unused email
- **THEN** the account is created in a state that can sign in with the password mechanism, and the created user's role is exactly the one the administrator specified

#### Scenario: Administrator registers a professional to be claimed

- **WHEN** an administrator registers a professional by email
- **THEN** a user with the professional role exists holding that email and no password, awaiting the Google sign-in that will claim it

#### Scenario: Duplicate email

- **WHEN** an administrator creates an account with an email another user already holds
- **THEN** the API responds `409` with code `auth.email_already_in_use` and no account is created

#### Scenario: Disabling an account ends its access

- **WHEN** an administrator disables an account that currently has an active session
- **THEN** that session's next request is refused, and no new session can be established for the account

### Requirement: An administrator exists before any administrator can sign in

The system SHALL be able to establish its first administrator without requiring an existing administrator, from configuration supplied outside the repository. Applying that configuration repeatedly SHALL NOT create duplicate accounts. The bootstrapped credential MUST NOT be usable indefinitely as supplied without the operator being made aware of it.

#### Scenario: First start with no administrator

- **WHEN** the application starts against a database holding no administrator and bootstrap configuration is present
- **THEN** exactly one administrator account is created from that configuration and can sign in

#### Scenario: Restart does not duplicate

- **WHEN** the application starts again with the same bootstrap configuration and that administrator already exists
- **THEN** no second account is created and the existing account is left as it is, including any password the operator has since changed

#### Scenario: Supplied credential is not silently permanent

- **WHEN** the bootstrapped administrator signs in still using the credential exactly as supplied by configuration
- **THEN** the system either requires that password to be changed before other work proceeds, or emits a warning identifying the account as still holding its bootstrap credential

### Requirement: The sign-in path resists automated guessing

The login endpoints SHALL be rate-limited, and repeated failures against a single account SHALL lock that account rather than allowing unbounded attempts. Refusals from these controls SHALL use the error contract rather than an opaque failure.

#### Scenario: Too many attempts

- **WHEN** login attempts from a caller exceed the configured rate
- **THEN** the API responds `429` with code `auth.rate_limited` without evaluating the credentials

#### Scenario: Repeated failures lock the account

- **WHEN** consecutive failed attempts against one account exceed the configured threshold
- **THEN** the account is locked, and a subsequent attempt with the correct password is refused `403` with code `auth.account_disabled`

#### Scenario: Rate limiting does not apply to the whole API

- **WHEN** an authenticated user makes ordinary requests to non-login endpoints at normal volume
- **THEN** those requests are not refused by the login rate limit

### Requirement: The redirect flow and the cookie-authenticated API each have their own request-forgery defence

The Google redirect flow SHALL be protected by a `state` value bound to the initiating browser and a `nonce` bound to the resulting ID token, so a callback cannot be replayed or injected. Separately, state-changing requests to the cookie-authenticated API SHALL require proof that the caller intended the request, which a cross-site attacker cannot supply merely by causing the browser to send its cookie.

#### Scenario: Callback with a missing or mismatched state

- **WHEN** a Google callback arrives whose `state` does not match the one issued to that browser
- **THEN** the API establishes no session and returns the browser to the sign-in surface reporting `auth.google_failed`
- **AND** the destination is a path within this application, never one taken from the request, so the refusal cannot be turned into an open redirect

#### Scenario: Replayed callback

- **WHEN** a previously consumed callback, including its `state` and `nonce`, is presented a second time
- **THEN** it is refused with `auth.google_failed` rather than establishing a second session, because consuming the callback cleared the state it would have to match

#### Scenario: Cross-site state-changing request

- **WHEN** a state-changing request arrives carrying a valid session cookie but without the request-forgery proof the API requires
- **THEN** the request is refused and no state changes

#### Scenario: Same-origin request from the app succeeds

- **WHEN** either frontend makes the same state-changing request through its normal flow
- **THEN** the required proof is present and the request succeeds

### Requirement: Both surfaces present sign-in and guard authenticated routes

Each frontend SHALL offer the sign-in path appropriate to its audience — Google for patients on the public portal, and both Google and internal credentials on the staff surface — and SHALL prevent an unauthenticated visitor from reaching an authenticated screen. The staff surface SHALL show only the navigation its signed-in user's role permits. All user-facing text introduced SHALL resolve through the i18n layer in both product languages.

#### Scenario: Unauthenticated visitor to an authenticated screen

- **WHEN** an unauthenticated visitor loads an authenticated route on either surface, including by a full page load of a deep link
- **THEN** the surface presents its sign-in screen instead of the authenticated screen and does not render protected data

#### Scenario: Session lost mid-session

- **WHEN** a request from an authenticated screen is refused because the session expired or was revoked
- **THEN** the surface returns the user to sign-in with a translated explanation rather than failing silently or showing a raw error

#### Scenario: Navigation reflects role

- **WHEN** a user signs in to the staff surface
- **THEN** the app-shell shows the navigation entries their role permits and omits the others, and the omitted destinations remain refused by the API if requested directly

#### Scenario: Patient manages their own profile and consents

- **WHEN** a signed-in patient opens their profile screen
- **THEN** they see their own minimal personal data and consent status, and can update what they are permitted to update
