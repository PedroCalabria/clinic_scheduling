## ADDED Requirements

### Requirement: A professional authorizes calendar access as a separate act from signing in

The system SHALL request authorization to a professional's external calendar only when that professional deliberately starts the connection, and SHALL NOT request it as part of any sign-in. The authorization request SHALL ask for offline access so that a long-lived credential is returned, SHALL ask for the calendar scope, and SHALL declare that previously granted scopes are to be retained, so that obtaining calendar access does not replace the identity authorization the sign-in path depends on. Signing in SHALL continue to request identity scopes only.

#### Scenario: A professional starts the connection

- **WHEN** a signed-in professional starts the calendar connection
- **THEN** the browser is sent to the provider with a request that includes the calendar scope, offline access, and the instruction to retain previously granted scopes

#### Scenario: Signing in still asks for identity only

- **WHEN** any user signs in through the provider
- **THEN** the request asks for identity scopes only, asks for no offline access, and no long-lived credential is returned or stored by that flow

#### Scenario: A patient cannot reach the connection

- **WHEN** a caller who is not a professional attempts to start a calendar connection
- **THEN** the request is refused on the role and no authorization is requested

#### Scenario: No provider client is configured

- **WHEN** a professional starts the connection against a deployment with no provider client configured
- **THEN** the request is refused with `auth.google_unavailable`, no connection is created, and every other part of the system continues to work

### Requirement: The connection flow never establishes a session, and the sign-in flow never yields a calendar credential

The system SHALL keep the calendar authorization flow and the sign-in flow separate: separate start endpoints, separate callback endpoints, and separate flow state. The calendar callback SHALL require an existing authenticated professional, SHALL NOT create a user, and SHALL NOT establish or alter a session. The sign-in callback SHALL NOT store any long-lived provider credential. A callback SHALL refuse a request whose flow state is absent, expired, or does not match the one presented, and flow state SHALL be consumed so that replaying a callback fails.

#### Scenario: The calendar callback requires an authenticated professional

- **WHEN** the calendar callback is reached without an authenticated professional session
- **THEN** it is refused, no user is created, and no session is established

#### Scenario: A replayed calendar callback is refused

- **WHEN** a calendar callback is presented a second time with flow state that has already been consumed
- **THEN** it is refused, no credential is stored, and no connection is created or altered

#### Scenario: Mismatched flow state is refused

- **WHEN** a calendar callback presents state that does not match the state held for this browser, or presents none at all
- **THEN** it is refused before any exchange with the provider is attempted

#### Scenario: The destination cannot be redirected off-origin

- **WHEN** a calendar flow is started with a requested destination that is not a local path of this origin
- **THEN** the flow returns the professional to the default destination instead

### Requirement: The long-lived credential is encrypted at rest and never leaves the API

The system SHALL store the provider's long-lived credential only in encrypted form, under a key supplied by configuration and absent from the repository. The stored value SHALL carry the version of the scheme that produced it. The credential SHALL NOT be returned in any API response, SHALL NOT be written to logs, and SHALL NOT be exposed to any browser. If calendar configuration is present without a usable encryption key, the system SHALL refuse to start rather than store the credential unprotected. Short-lived access credentials SHALL NOT be persisted.

#### Scenario: What is stored is not the credential

- **WHEN** a connection is established and the stored record is read directly from the database
- **THEN** the stored value is not the credential the provider returned, and it carries a scheme version

#### Scenario: The credential is not disclosed

- **WHEN** any calendar endpoint returns the state of a connection
- **THEN** the response contains no credential material of any kind

#### Scenario: Calendar configuration without an encryption key

- **WHEN** the system starts with calendar configuration present and no usable encryption key
- **THEN** startup fails with a message naming the missing key, rather than starting with the feature silently disabled or storing the credential unencrypted

#### Scenario: No calendar configuration at all

- **WHEN** the system starts with no calendar configuration and no encryption key
- **THEN** it starts normally, and only the calendar connection surface reports itself unavailable

### Requirement: A granted scope is verified rather than assumed

The system SHALL verify that the provider actually granted calendar access before recording a connection. When the authorization completes without the calendar scope, the system SHALL store no credential, SHALL create no connection, SHALL write no consent, and SHALL report `calendar.scope_declined`, which SHALL be distinct from the code reported for a grant that was later revoked.

#### Scenario: The professional declines calendar access while approving the rest

- **WHEN** the authorization completes and the granted scopes do not include calendar access
- **THEN** nothing is stored, no connection exists, and the professional is told their calendar permission was declined

#### Scenario: Declining is distinguishable from revoking

- **WHEN** a professional is shown the result of a declined authorization
- **THEN** the message is the declined one and not the revoked one, so the action offered is to grant permission rather than to reconnect

### Requirement: A professional has at most one calendar connection, and reconnecting reuses it

The system SHALL hold at most one calendar connection per professional. Reconnecting after a revocation or a withdrawal SHALL update that connection rather than create a second. The connection SHALL record the provider, the calendar it targets, when it was established, its current state, and when that state was last observed. A connection SHALL NOT be reported as connected unless credential material is held for it. When an authorization returns no new long-lived credential, the system SHALL retain the credential it already holds rather than replace it with nothing; if it holds none either, the connection SHALL NOT be recorded as connected.

#### Scenario: Reconnecting does not create a second connection

- **WHEN** a professional whose connection was revoked completes the authorization again
- **THEN** their single connection is updated to connected, and no second connection exists for them

#### Scenario: An authorization returning no new credential

- **WHEN** an authorization completes successfully but returns no long-lived credential, and a credential is already held for that professional
- **THEN** the held credential is retained and the connection is connected

#### Scenario: No credential held and none returned

- **WHEN** an authorization completes without a long-lived credential and none is held for that professional
- **THEN** the connection is not recorded as connected and the professional is told the connection could not be completed

### Requirement: Connection state is an observation, reported with when it was observed

The system SHALL report a connection's state together with the moment that state was last observed, and SHALL provide an action that checks the connection against the provider on demand. The check SHALL exchange the stored credential without reading any calendar content. A provider response indicating the authorization is no longer valid SHALL move the connection to revoked and record the moment. A failure to reach the provider SHALL leave the recorded state unchanged and SHALL be reported as `calendar.sync_failed`, so that an unreachable provider is never recorded as a revoked authorization.

#### Scenario: The state is reported with its observation time

- **WHEN** a professional reads their connection
- **THEN** the response carries the state and the moment that state was last observed

#### Scenario: An on-demand check finds the authorization revoked

- **WHEN** a professional checks a connected connection whose authorization has been revoked at the provider
- **THEN** the connection becomes revoked, the observation moment is recorded, and the professional is offered reconnection with `calendar.consent_revoked`

#### Scenario: The provider is unreachable during a check

- **WHEN** a check cannot reach the provider
- **THEN** the recorded state and its observation moment are unchanged, and the failure is reported as `calendar.sync_failed`

#### Scenario: Checking a connection that was never established

- **WHEN** a professional with no connection checks it
- **THEN** the request is refused with `calendar.not_connected` and no exchange with the provider is attempted

### Requirement: A professional withdraws calendar access, and the withdrawal reaches the provider

The system SHALL allow a professional to disconnect their calendar. Disconnecting SHALL revoke the calendar consent, SHALL clear the stored credential, and SHALL record the connection as disconnected, in a single transaction. The system SHALL also request revocation at the provider; when that request fails, the local withdrawal SHALL still take effect and the professional SHALL be told the authorization may remain listed in their provider account, rather than being shown an unqualified success.

#### Scenario: Disconnecting withdraws everything

- **WHEN** a connected professional disconnects
- **THEN** the calendar consent is revoked, no credential remains stored, the connection reads as disconnected, and revocation is requested at the provider

#### Scenario: The provider cannot be reached during withdrawal

- **WHEN** the revocation request to the provider fails
- **THEN** the local withdrawal still takes effect, and the professional is told the authorization may still be listed in their provider account and where to remove it

#### Scenario: Disconnecting an already-revoked connection

- **WHEN** a professional disconnects a connection whose authorization was already revoked at the provider
- **THEN** the withdrawal completes without error

#### Scenario: Reconnecting after withdrawal

- **WHEN** a professional who disconnected completes the authorization again
- **THEN** a calendar consent is recorded again at the version in force, and the connection reads as connected

### Requirement: Ending a professional's access withdraws their calendar authorization

The system SHALL withdraw a professional's calendar authorization when an administrator disables or deactivates their account. The withdrawal SHALL be the same one the professional could perform themselves: the calendar consent revoked, the stored credential cleared, the connection recorded as disconnected, and revocation requested at the provider. A failure to reach the provider SHALL NOT prevent the account action from succeeding. An account with no calendar connection SHALL be disabled or deactivated without error.

#### Scenario: Disabling a professional withdraws their calendar

- **WHEN** an administrator disables the account of a professional whose calendar is connected
- **THEN** their calendar consent is revoked, no credential remains stored, the connection reads as disconnected, and revocation is requested at the provider

#### Scenario: Deactivating a professional withdraws their calendar

- **WHEN** an administrator deactivates the account of a professional whose calendar is connected
- **THEN** the same withdrawal takes effect, so an account whose address has been released holds no calendar authorization

#### Scenario: An unreachable provider does not block the account action

- **WHEN** an administrator disables a professional whose calendar is connected and the provider cannot be reached
- **THEN** the account is still disabled, its sessions are still revoked, and the local withdrawal still takes effect

#### Scenario: An account with no calendar

- **WHEN** an administrator disables or deactivates an account that has never connected a calendar
- **THEN** the action succeeds unchanged, because having nothing to withdraw is an ordinary state rather than a failure

#### Scenario: The authorization ends rather than pausing

- **WHEN** a calendar authorization has been withdrawn because the account was disabled or deactivated
- **THEN** no credential remains and the connection is not usable, so a calendar can only become connected again by a professional completing the authorization afresh — the withdrawal ends the authorization rather than suspending it

### Requirement: A professional reaches only their own connection

The system SHALL scope every calendar-connection operation to the professional making the request, resolved from their session. No calendar endpoint SHALL accept an identifier naming whose connection to act on. A professional SHALL NOT read, establish, check, or withdraw another professional's connection, and no other role SHALL be able to act on a professional's connection on their behalf.

#### Scenario: A professional acts only on their own

- **WHEN** a professional reads or acts on the calendar connection
- **THEN** the connection acted on is their own, determined from their session rather than from anything in the request

#### Scenario: Another professional's connection is unreachable

- **WHEN** a professional attempts to reach a connection belonging to another professional
- **THEN** there is no request that names one, so the attempt cannot be expressed

#### Scenario: Staff cannot connect on a professional's behalf

- **WHEN** a front-desk user or an administrator attempts any calendar-connection operation
- **THEN** the request is refused on the role

### Requirement: The calendar connection surface is a professional's own screen, in both languages

The system SHALL present a professional with a surface that shows whether their calendar is connected, when that was last observed, and offers connecting, checking, reconnecting after a revocation, and disconnecting with a confirmation. Refusals SHALL be shown as translated messages derived from their codes. Every string SHALL be translated in pt-BR and en. The surface SHALL NOT be presented to any other role, and SHALL NOT appear in navigation for a role that cannot use it.

#### Scenario: A connected professional sees their state

- **WHEN** a professional with a connected calendar opens the surface
- **THEN** it reports the calendar as connected, names when that was last observed, and offers checking and disconnecting

#### Scenario: A revoked authorization prompts reconnection

- **WHEN** a professional whose authorization has been revoked opens the surface
- **THEN** it reports the authorization as no longer valid and offers reconnection

#### Scenario: A professional who has never connected

- **WHEN** a professional with no connection opens the surface
- **THEN** it reports that no calendar is connected and offers connecting, without presenting the absence as an error

#### Scenario: The surface is absent for other roles

- **WHEN** a front-desk user or an administrator uses the staff application
- **THEN** the calendar surface is neither offered in navigation nor reachable by its route

#### Scenario: Both languages

- **WHEN** the surface is viewed in pt-BR and in en
- **THEN** every label, state and refusal message is translated in both, with no untranslated key shown

### Requirement: Connecting a calendar changes no scheduling behavior in this capability's first increment

The system SHALL NOT alter availability, booking, cancellation or rescheduling as a result of a calendar connection existing. No calendar event SHALL be created, updated or deleted, and no appointment SHALL carry an external event reference, until outbound propagation is delivered.

#### Scenario: Availability is unchanged by a connection

- **WHEN** availability is computed for a professional whose calendar is connected
- **THEN** the result is identical to what it would be with no connection

#### Scenario: Booking is unchanged by a connection

- **WHEN** an appointment is booked, cancelled or rescheduled for a professional whose calendar is connected
- **THEN** the outcome and the stored appointment are the same as for a professional with no connection, and no external calendar is contacted
