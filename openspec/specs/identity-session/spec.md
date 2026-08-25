# identity-session Specification

## Purpose

How a caller becomes an authenticated principal and what that principal may do: two
login paths — Google OIDC for patients and professionals, internal email/password for
reception and administration — converging on one app-owned session, with session
lifetime and immediate revocation. Covers how each role's user comes into existence (an
unknown Google email becomes a patient, a professional is pre-created by an
administrator and claimed by their first Google sign-in, an internal account can never
be claimed through Google), role-based authorization, ownership-based authorization on
patient data, access logging of staff access to patient data, and versioned consent
capture.

Established by change `identity-session` (change 2 of the build order), following
`platform-health` from change 1.
## Requirements

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

The system SHALL determine a role at the moment a user first comes into existence and SHALL NOT infer it from the identity provider. Provisioning SHALL depend on the surface the sign-in was started from, and that surface SHALL be fixed when the flow starts rather than derived from the identity the provider returns. Each surface SHALL establish a session only for a user holding the role that surface serves — the patient portal a patient, the staff surface a professional — and SHALL refuse any other role without establishing a session, reporting which surface serves that user instead. A Google sign-in started from the **patient portal** whose email is unknown SHALL create a patient. A Google sign-in started from the **staff surface** SHALL be claim-only: it SHALL claim a pre-created professional the first time and reuse it afterwards, and SHALL refuse an unknown email without creating any user, patient, consent, or session. Every refusal SHALL be decided before any state is written, so a refused sign-in SHALL NOT claim a pending invitation. Role SHALL NOT change as a side effect of signing in, and no sign-in SHALL change a role that already exists.

#### Scenario: Unknown Google email becomes a patient

- **WHEN** a Google sign-in started from the patient portal succeeds for an email no user record holds
- **THEN** the system creates a user with the patient role and its associated patient record holding only the minimal personal data the provider supplied

#### Scenario: Unknown Google email on the staff surface is refused and creates nothing

- **WHEN** a Google sign-in started from the staff surface succeeds for an email no user record holds
- **THEN** the system establishes no session and reports `auth.not_provisioned`, directing the visitor to ask administration to register their access
- **AND** no user, patient record, or consent is created, so the refused visitor remains un-provisioned and can still be invited as a professional afterwards

#### Scenario: An existing patient is refused on the staff surface

- **WHEN** a Google sign-in started from the staff surface succeeds for an email belonging to a user holding the patient role
- **THEN** the system establishes no session and reports `auth.use_patient_sign_in`, naming the surface that does serve them rather than telling them to seek registration they do not need
- **AND** that user's role, records, and consents are left exactly as they were

#### Scenario: A professional is refused on the patient portal

- **WHEN** a Google sign-in started from the patient portal succeeds for an email belonging to a user holding the professional role
- **THEN** the system establishes no session and reports `auth.use_staff_sign_in`, rather than admitting a session in which the portal's own screens cannot find a patient record for the caller
- **AND** no patient record and no consent are created for that user

#### Scenario: An invitation cannot be claimed from the wrong surface

- **WHEN** a Google sign-in started from the patient portal succeeds for an email an administrator registered as a professional and nobody has claimed
- **THEN** the invitation remains unclaimed, holding no provider subject identifier and still awaiting its first sign-in
- **AND** the same identity signing in from the staff surface afterwards claims it normally

#### Scenario: The same identity is a patient on one surface and turned away on the other

- **WHEN** the same Google identity signs in from the patient portal and then from the staff surface
- **THEN** the first sign-in resolves to that patient's session and the second is refused with `auth.use_patient_sign_in`, because the surface the flow started from decides, not the token

#### Scenario: Pre-invited professional claims the prepared account

- **WHEN** a Google sign-in started from the staff surface succeeds for an email an administrator previously registered as a professional
- **THEN** the system links the Google subject identifier to that existing user, preserving its professional role
- **AND** no patient record is created for that user

#### Scenario: A claimed professional signs in again on the staff surface

- **WHEN** a professional who has already claimed their account signs in again from the staff surface
- **THEN** the session is established as before, because claim-only admits an already-claimed professional as well as one claiming for the first time

#### Scenario: Subsequent sign-in reuses the same user

- **WHEN** a user who has signed in before signs in again through the same provider
- **THEN** the system resolves the existing user by the stored subject identifier and creates no duplicate user, patient, or role change

#### Scenario: An internal account cannot be claimed through Google

- **WHEN** a Google sign-in succeeds for an email belonging to an internal-account user
- **THEN** the system establishes no session and reports `auth.google_failed`, so a staff account cannot be taken over by controlling its email at the provider, and this refusal is reported the same way from either surface because it concerns the provider rather than the door
- **AND** because a staff member clicking the wrong sign-in button is an ordinary mistake, the refusal returns them to the sign-in surface with a translated message rather than a raw response body

#### Scenario: Refusal precedes any write

- **WHEN** a Google sign-in is refused on either surface
- **THEN** the refusal is decided before anything is written, so a refused sign-in leaves the stored users, patients, consents, and sessions byte-for-byte as they were

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

Access to a patient's personal data SHALL additionally require that the caller owns that data, or holds a role that overrides ownership for a stated reason. The system SHALL NOT trust a client-supplied identifier to establish ownership; ownership SHALL be derived from the authenticated session. Holding the patient role SHALL NOT by itself grant access to any patient record.

Operational staff — front desk and administrators — SHALL be permitted to reach any patient's personal data, that being the work those roles exist to do. A professional SHALL be permitted to reach the personal data of a patient **only** where that patient appears on the professional's own schedule, and SHALL be refused otherwise. Whether that relationship holds SHALL be established by the caller and supplied to the rule as a fact, never derived inside the rule, so that the rule stays free of any notion of schedules or storage.

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

#### Scenario: A professional reaches a patient on their own schedule

- **WHEN** a professional reads a schedule containing an appointment of theirs, and that appointment names its patient
- **THEN** the access is permitted, the patient being one of their own

#### Scenario: A professional cannot reach a patient who is not theirs

- **WHEN** a professional attempts to reach the personal data of a patient who holds no appointment with them
- **THEN** the access is refused, the professional role conferring no general access to patients

#### Scenario: The relationship is a fact given to the rule, not looked up by it

- **WHEN** the ownership rule is evaluated for a professional
- **THEN** it is told whether the patient is theirs and decides from that, holding no means of finding out for itself

### Requirement: Staff access to patient personal data is recorded

When a staff member accesses a patient's personal data, the system SHALL record who accessed which patient's data, what action was taken, and when. A patient accessing their own data SHALL NOT be recorded. Access permitted by role rather than by ownership SHALL be recorded whichever staff role permitted it, including a professional reading a patient on their own schedule.

A single request disclosing several patients SHALL record every patient it disclosed, and SHALL record them before the disclosure is returned, so that the record exists whether or not the rest of the request succeeds. Acting on an appointment SHALL NOT by itself produce a record, an appointment not being the patient's personal data; the reading of the patient's name that preceded it SHALL already have been recorded.

#### Scenario: Staff access is logged

- **WHEN** a front-desk user or administrator reads a patient's personal data
- **THEN** an access record is stored identifying the acting user, the patient, the action, and the time of access

#### Scenario: Self-access is not logged

- **WHEN** a patient reads their own personal data
- **THEN** no access record is created

#### Scenario: A professional reading their own patients is logged

- **WHEN** a professional reads a schedule that names the patients of their appointments
- **THEN** an access record is stored for each patient named

#### Scenario: A day of appointments records every patient it disclosed

- **WHEN** a front-desk user reads a day containing appointments for several patients
- **THEN** one access record exists for each distinct patient named in the response

#### Scenario: A day naming no patient records nothing

- **WHEN** a staff member reads a day that contains no appointments
- **THEN** no access record is created

#### Scenario: Resolving a patient by their email is logged

- **WHEN** a front-desk user resolves a patient by their contact email and the patient is returned
- **THEN** an access record is stored for that patient

#### Scenario: Cancelling an appointment does not produce a second record

- **WHEN** a front-desk user cancels an appointment on a day they have already read
- **THEN** the cancellation itself creates no further access record

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

An administrator SHALL be able to create internal accounts for front-desk and administrator users, register a professional by the email that user will sign in with, disable an account, and deactivate an account. Email SHALL be unique across users whose accounts have not been deactivated; a deactivated account's email SHALL be available for a new account. Disabling an account SHALL prevent new sessions, SHALL revoke that user's existing sessions, and SHALL retain that account's email. Deactivating an account SHALL do all of that and additionally release its email. An administrator SHALL be able to resolve which account holds a given email address, whatever role that account holds. An administrator SHALL NOT be able to deactivate their own account. Accounts SHALL be soft-deleted only.

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

#### Scenario: Disabling an account keeps its email

- **WHEN** an administrator disables an account and then creates a new account with the same email
- **THEN** the API responds `409` with code `auth.email_already_in_use`, because disabling turns an account off without retiring the identity

#### Scenario: A deactivated address can be registered again

- **WHEN** an administrator deactivates the account holding an email and then registers a professional with that same email
- **THEN** the registration succeeds and produces a new user with the professional role and a new identity, while the deactivated account remains stored with its history

#### Scenario: Deactivating an account ends its access

- **WHEN** an administrator deactivates an account that currently has an active session
- **THEN** that session's next request is refused, no new session can be established for the account, and the account is soft-deleted rather than removed

#### Scenario: Recovering an account created by mistake

- **WHEN** a visitor has been provisioned as a patient by mistake and an administrator needs that address to belong to a professional
- **THEN** the administrator can resolve which account holds the address, deactivate it, and register the address as a professional, and the resulting invitation is claimable by that person's next Google sign-in
- **AND** the role of the original account is never altered, because recovery replaces the account rather than changing it

#### Scenario: Resolving the account holding an address

- **WHEN** an administrator asks which account holds a given email address
- **THEN** the answer identifies that account, its role, and its status if one exists, and reports `auth.account_not_found` if none does, regardless of whether that account would appear in the staff account list

#### Scenario: An administrator cannot deactivate their own account

- **WHEN** an administrator attempts to deactivate the account their own session belongs to
- **THEN** the API responds `403` with code `auth.forbidden` and the account remains usable, so the clinic cannot lock itself out of account administration

#### Scenario: Only an administrator may deactivate an account

- **WHEN** an authenticated front-desk user attempts to deactivate any account
- **THEN** the API responds `403` with code `auth.forbidden` and nothing is modified

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

Each frontend SHALL offer the sign-in path appropriate to its audience — Google for patients on the public portal, and both Google and internal credentials on the staff surface — and SHALL prevent an unauthenticated visitor from reaching an authenticated screen. A refusal the API reports by returning the browser to the application SHALL reach the sign-in screen that translates it, including when the visitor is returned to a route that requires authentication. The staff surface SHALL show only the navigation its signed-in user's role permits. All user-facing text introduced SHALL resolve through the i18n layer in both product languages.

#### Scenario: Unauthenticated visitor to an authenticated screen

- **WHEN** an unauthenticated visitor loads an authenticated route on either surface, including by a full page load of a deep link
- **THEN** the surface presents its sign-in screen instead of the authenticated screen and does not render protected data

#### Scenario: A refused sign-in reports its reason on the sign-in screen

- **WHEN** a Google sign-in is refused and the browser is returned to an authenticated route carrying the refusal code
- **THEN** the surface presents its sign-in screen showing the translated explanation for that code, rather than discarding the reason while redirecting the visitor to sign in

#### Scenario: An un-invited professional is told what to do

- **WHEN** a visitor is refused on the staff surface because no account is registered for their address
- **THEN** the staff sign-in screen explains that administration must register their access, in the language the visitor is using, and offers no action that would create an account

#### Scenario: Someone at the wrong entrance is told which one is theirs

- **WHEN** a visitor is refused because the account their address holds belongs to the other surface
- **THEN** the sign-in screen they were returned to names the surface that does serve them, in the language they are using, and distinguishes that from being unregistered

#### Scenario: Session lost mid-session

- **WHEN** a request from an authenticated screen is refused because the session expired or was revoked
- **THEN** the surface returns the user to sign-in with a translated explanation rather than failing silently or showing a raw error

#### Scenario: Navigation reflects role

- **WHEN** a user signs in to the staff surface
- **THEN** the app-shell shows the navigation entries their role permits and omits the others, and the omitted destinations remain refused by the API if requested directly

#### Scenario: Patient manages their own profile and consents

- **WHEN** a signed-in patient opens their profile screen
- **THEN** they see their own minimal personal data and consent status, and can update what they are permitted to update
