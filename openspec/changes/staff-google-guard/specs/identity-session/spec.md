## MODIFIED Requirements

### Requirement: A user's role is provisioned deterministically, never inferred

The system SHALL determine a role at the moment a user first comes into existence and SHALL NOT infer it from the identity provider. Provisioning SHALL depend on the surface the sign-in was started from, and that surface SHALL be fixed when the flow starts rather than derived from the identity the provider returns. Each surface SHALL establish a session only for a user holding the role that surface serves — the patient portal a patient, the staff surface a professional — and SHALL refuse any other role without establishing a session, reporting which surface serves that user instead. A Google sign-in started from the **patient portal** whose email is unknown SHALL create a patient. A Google sign-in started from the **staff surface** SHALL be claim-only: it SHALL claim a pre-created professional the first time and reuse it afterwards, and SHALL refuse an unknown email without creating any user, patient, consent, or session. Every refusal SHALL be decided before any state is written, so a refused sign-in SHALL NOT claim a pending invitation. Role SHALL NOT change as a side effect of signing in, and no sign-in SHALL change a role that already exists.

#### Scenario: Unknown Google email on the patient portal becomes a patient

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
