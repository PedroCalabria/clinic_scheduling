# platform-health Specification

## Purpose

The deployed system reports its own operational readiness: an HTTP health endpoint
covering database connectivity, reachable through the public reverse proxy, plus the
cross-cutting guarantees every surface depends on — both frontends served at their own
base paths from a single origin, requests traceable by correlation id, and user-facing
text present in both product languages.

Established by change `walking-skeleton` (change 1 of the build order).

## Requirements

### Requirement: Health endpoint reports database connectivity

The API SHALL expose `GET /api/health` returning an aggregate operational status that includes the reachability of the PostgreSQL database. The endpoint MUST NOT require authentication, and MUST NOT disclose connection strings, credentials, host names, or exception details.

#### Scenario: Database reachable

- **WHEN** a client requests `GET /api/health` and the API can execute a trivial query against PostgreSQL
- **THEN** the API responds `200 OK` with an overall status of `Healthy` and a database check reported as healthy

#### Scenario: Database unreachable

- **WHEN** a client requests `GET /api/health` and PostgreSQL cannot be reached
- **THEN** the API responds `503 Service Unavailable` with an overall status of `Unhealthy` and a database check reported as unhealthy
- **AND** the response body contains no connection string, credential, host name, or stack trace

### Requirement: Health endpoint reachable through the reverse proxy

The deployed stack SHALL expose the health endpoint to clients through Caddy at the public path `/api/health`, so that the proxy route itself is covered by the same check. The API container SHALL NOT publish a port to the host.

#### Scenario: Reached through the public entrypoint

- **WHEN** a client requests `/api/health` against Caddy's published port on a running Compose stack
- **THEN** the request is proxied to the API and the health response is returned unchanged

#### Scenario: API not directly addressable

- **WHEN** the Compose stack is running
- **THEN** only Caddy publishes a host port, and the API and database are reachable only from within the internal Compose network

### Requirement: Both frontend surfaces are served at their base paths

Caddy SHALL serve the patient-portal build at `/` and the staff build at `/staff` from a single origin. Each surface MUST load its own assets correctly and MUST resolve client-side deep links on a full page load, so that neither surface's routing or assets leak into the other.

#### Scenario: Patient portal served at root

- **WHEN** a browser loads `/`
- **THEN** the patient-portal build is served, its assets resolve with `200` responses, and the app mounts

#### Scenario: Staff app served at its prefix

- **WHEN** a browser loads `/staff`
- **THEN** the staff build is served, its assets resolve with `200` responses, and the app mounts under the `/staff` router basename

#### Scenario: Deep link survives a full reload

- **WHEN** a browser performs a full page load of a client-side route beneath either base path, such as `/staff/anything`
- **THEN** Caddy returns that surface's own `index.html` rather than `404`, and the app resolves the route on the client

#### Scenario: Each surface reaches the API

- **WHEN** either surface's health page loads and calls `/api/health` through Caddy
- **THEN** the call succeeds and the surface renders the reported status, including database connectivity

### Requirement: Requests are traceable by correlation id

The API SHALL emit structured logs in which every log entry written while handling a request carries a correlation id identifying that request. The correlation id SHALL be taken from the inbound request when supplied and generated when absent, and SHALL be returned to the caller in the response.

#### Scenario: Correlation id generated

- **WHEN** a request arrives without a correlation id header
- **THEN** the API generates one, includes it in every log entry emitted for that request, and returns it in a response header

#### Scenario: Inbound correlation id preserved

- **WHEN** a request arrives carrying a correlation id header
- **THEN** the API reuses that value rather than generating a new one, so the caller's identifier appears in the API's logs

### Requirement: User-facing text is localized in both product languages

Every user-facing string rendered by either frontend surface SHALL resolve through the i18n layer and SHALL have a value present in both pt-BR and en. No user-facing string may be hardcoded in a component.

#### Scenario: Language switch changes rendered text

- **WHEN** the active language of either surface is switched between pt-BR and en
- **THEN** the rendered user-facing text changes to the corresponding translation, with no missing-key fallback displayed

#### Scenario: Missing translation fails the build pipeline

- **WHEN** a user-facing key exists in one language's resource file but not the other
- **THEN** the i18n-key presence check fails, and the change cannot pass CI
