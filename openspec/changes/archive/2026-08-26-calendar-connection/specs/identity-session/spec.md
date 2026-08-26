## MODIFIED Requirements

### Requirement: Consent is captured and versioned

The system SHALL record a user's consent with the version consented to and the moment it was granted, and SHALL allow a consent to be shown as revoked without erasing that it was once granted. A consent SHALL be of a stated type, and the types SHALL be distinct facts about distinct permissions rather than one general agreement. Consent records SHALL be visible to the user they belong to. A consent SHALL be granted only by the user it belongs to, through the surface that asks for that particular permission, and SHALL NOT be granted on another user's behalf.

#### Scenario: Consent granted at first sign-in

- **WHEN** a patient record is created for a newly provisioned user
- **THEN** a data-processing consent is recorded with its version and the time it was granted

#### Scenario: Consent revoked without losing history

- **WHEN** a user revokes a consent
- **THEN** the record is marked revoked with the time of revocation, and the original grant remains recorded rather than deleted

#### Scenario: Action requiring an ungranted consent

- **WHEN** an action requires a consent the user has not granted
- **THEN** the API responds `422` with code `auth.consent_required`

#### Scenario: A professional's calendar consent

- **WHEN** a professional establishes a calendar connection
- **THEN** a calendar consent is recorded for that professional with the version in force and the time it was granted, in the same transaction as the connection, so a connection without its consent record cannot exist

#### Scenario: Calendar consent is withdrawn with the connection

- **WHEN** a professional withdraws their calendar connection
- **THEN** the calendar consent is marked revoked with the time of revocation, and the record that it was once granted remains

#### Scenario: A consent cannot be granted through a surface that does not ask for it

- **WHEN** a caller attempts to grant a consent of a type that the surface being used does not obtain permission for
- **THEN** the grant is refused, so a recorded consent always corresponds to a moment at which that particular permission was actually asked for

### Requirement: Administrators manage staff accounts and professional invitations

An administrator SHALL be able to create internal accounts for front-desk and administrator users, register a professional by the email that user will sign in with, disable an account, restore a disabled account, and deactivate an account. Email SHALL be unique across users whose accounts have not been deactivated; a deactivated account's email SHALL be available for a new account. Disabling an account SHALL prevent new sessions, SHALL revoke that user's existing sessions, and SHALL retain that account's email. Deactivating an account SHALL do all of that and additionally release its email. Restoring a disabled account SHALL return it to the state it should hold — an unclaimed professional invitation SHALL remain awaiting its claim rather than becoming able to sign in — and SHALL clear any failed-attempt streak. A deactivated account SHALL NOT be restorable, because its address may already belong to another account. Disabling or deactivating an account SHALL also withdraw any external-calendar authorization that account holds, so an authorization the clinic was granted does not outlive the access it was granted alongside; restoring an account SHALL NOT restore that authorization. An administrator SHALL be able to resolve which account holds a given email address, whatever role that account holds. An administrator SHALL NOT be able to deactivate their own account. Accounts SHALL be soft-deleted only.

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

#### Scenario: Restoring a disabled account

- **WHEN** an administrator restores an account that was disabled
- **THEN** it can hold a session again, its failed-attempt streak is cleared, and the sessions revoked when it was disabled are not reinstated

#### Scenario: Restoring an unclaimed invitation

- **WHEN** an administrator restores a disabled professional invitation that was never claimed
- **THEN** it returns to awaiting its claim rather than becoming able to sign in, so no account can hold a session without an identity behind it

#### Scenario: A deactivated account cannot be restored

- **WHEN** an administrator attempts to restore an account that was deactivated
- **THEN** the request is refused, because deactivation released the address and it may already belong to another account; the recovery is to register the address anew

#### Scenario: Restoring an account does not restore its calendar authorization

- **WHEN** an administrator restores an account whose external-calendar authorization was withdrawn when it was disabled
- **THEN** that authorization remains withdrawn and its consent remains revoked, so the professional must authorize again rather than the clinic silently regaining access to their calendar

#### Scenario: Ending an account's access ends what that access authorized

- **WHEN** an administrator disables or deactivates the account of a professional who had connected an external calendar
- **THEN** that calendar authorization is withdrawn as part of the same action, rather than remaining valid after the account is switched off
