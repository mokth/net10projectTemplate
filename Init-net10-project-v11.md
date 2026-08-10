# .NET 10 Blazor Server ERP Foundation --- Implementation Plan (v11)

**Status:** **10/10 implementation baseline --- reviewed and hardened;
implement phase-by-phase**\
**Target:** .NET 10\
**UI:** Blazor Server / Interactive Server only\
**Reference system:** `C:\BlazorProjects\ERPLiteProtoType`\
**Primary goal:** Build a simple, reusable, secure, maintainable ERP
foundation --- not a full ERP.\
**Project path (recommended):** `C:\BlazorProjects\ErpWeb` (confirm
before Phase 1)

------------------------------------------------------------------------

# 1. Executive Summary

This plan establishes the foundation for the new ERP web application.

The foundation must provide:

-   Authentication via ASP.NET Core cookie auth (HTTP login/logout
    endpoints)
-   Authorization (thin `userlevel` checks only)
-   Current user/company/branch/location context
-   SQL Server + EF Core access via `IDbContextFactory`
-   Serilog logging (from Phase 1)
-   DevExpress theme switching + Bootstrap 5
-   Reusable `CommonDataGrid<T>` (delivered in split sub-phases)
-   Thin demo pages
-   Security and maintainability guardrails

The implementation must follow:

> **Inspect → Plan → Implement → Build → Test → Review → Continue**

Do not allow an AI coding agent to generate the entire project in one
pass.

Each major phase must be independently buildable and reviewable.

------------------------------------------------------------------------

# 2. Final Technology Decisions

  -------------------------------------------------------------------------------------
  Component                           Decision
  ----------------------------------- -------------------------------------------------
  Framework                           **.NET 10**

  UI                                  **Blazor Server / Interactive Server only**

  SPA/WASM                            **No**

  UI components                       **DevExpress Blazor 26.1.x**

  Preferred DX version                **26.1.3 stable baseline**; upgrade to 26.1.4
                                      only after a stable 26.1.4 release is available
                                      and validated

  DevExpress prerelease policy        **Do not use 26.1.4-pre-\* for the foundation**
                                      unless explicitly approved for a specific reason

  CSS                                 **Bootstrap 5**

  Database                            SQL Server

  ORM                                 EF Core compatible with .NET 10

  DbContext                           `IDbContextFactory<TContext>`

  Existing login table                `Userlogin`

  ASP.NET Identity                    **No**

  Password hashing                    Existing **BCrypt**

  Authentication                      **ASP.NET Core Cookie Authentication**

  Sign-in / sign-out                  **HTTP endpoints** (`POST /account/login`,
                                      `POST /account/logout`)

  Auth state provider                 **Server authentication-state infrastructure with
                                      revalidation**; no fake provider

  Auth revalidation                   **App-specific
                                      `RevalidatingServerAuthenticationStateProvider`
                                      only when validation is required; configurable
                                      5-minute baseline**

  JWT                                 **Defer for v1 unless an actual API requires it**

  ApiClient                           **Defer**

  Logging                             Serilog rolling files (**baseline from Phase 1**)

  Grid                                `CommonDataGrid<T>` based on `CommonDataGridEx`

  Theme                               DevExpress theme switcher pattern

  Architecture                        **Single project for v1**

  HTTPS                               **Required** in all non-local-insecure scenarios;
                                      cookies use `Secure` when HTTPS

  Render mode                         **Interactive Server only**; no Interactive
                                      WebAssembly or Interactive Auto

  Authentication storage              **Server cookie only**; no auth tokens in browser
                                      storage

  Login throttling                    **Evaluate/enable endpoint rate limiting before
                                      production deployment**
  -------------------------------------------------------------------------------------

The existing system confirms BCrypt, `Userlogin.id` as login username,
`CompanyCode` as part of the login lookup, `uid` as identity PK,
`UserID` as an application/audit identifier, and `userlevel` as the
access-level value. Existing inactive users cannot log in and
`changepass` forces the password-change flow.

### .NET 10 implementation baseline

This plan targets the current .NET 10 Blazor Web App model. The
application uses **Interactive Server** render mode only. The .NET 10
render-mode model explicitly supports Interactive Server, Interactive
WebAssembly, and Interactive Auto; this project intentionally enables
only Interactive Server. The global application render mode should be
configured at the `Routes` level, with `HeadOutlet` using the same
render mode.

Required render-mode rules:

-   `AddRazorComponents().AddInteractiveServerComponents()`.
-   `MapRazorComponents<App>().AddInteractiveServerRenderMode()`.
-   Global `Routes` render mode = `InteractiveServer`.
-   `HeadOutlet` render mode = `InteractiveServer`.
-   No `.Client` project.
-   No `AddInteractiveWebAssemblyComponents()`.
-   No `AddInteractiveWebAssemblyRenderMode()`.
-   No `InteractiveAuto`.
-   No per-page render-mode exceptions unless Phase 0 explicitly
    approves them.

### Authentication-state rule

Do not create a fake `CustomAuthenticationStateProvider`.

The ASP.NET Core authentication cookie is the source of authentication.
The server authentication-state infrastructure exposes that
authenticated principal to Blazor. If periodic validation of the
underlying `Userlogin` record is required, implement the smallest
possible app-specific provider derived from
`RevalidatingServerAuthenticationStateProvider`.

Initial baseline:

-   Revalidation interval: **5 minutes**, configurable.
-   Validation must confirm the authenticated account still exists and
    is active.
-   Validation must not query the database on every component render.
-   Validation failure must result in the user becoming unauthenticated
    and being redirected to login.
-   Do not silently invent additional account-state rules.

### Security boundary rule

Authentication identity and ERP data scope are server-owned.

The following values are **never authoritative when supplied by
browser/component parameters**:

-   `UserId`
-   `LoginId`
-   `UserLevel`
-   `CompanyCode`
-   `BranchCode`
-   `LocationCode`

UI filters may exist, but security-sensitive scope must always be
resolved from the authenticated user context on the server.

------------------------------------------------------------------------

# 3. Environment Reality Check

Before implementation, verify the actual development machine.

Expected baseline:

  -----------------------------------------------------------------------
  Item                                Requirement
  ----------------------------------- -----------------------------------
  .NET 10 SDK                         Installed

  .NET 11                             Not required

  DevExpress 26.1.x                   Available from NuGet; **26.1.3 is
                                      the stable baseline**

  Bootstrap 5                         Required

  SQL Server                          Accessible from development
                                      environment

  Reference source                    `ERPLiteProtoType` available

  HTTPS / certs                       Dev certificate trusted for local
                                      HTTPS
  -----------------------------------------------------------------------

Run:

``` bash
dotnet --info
dotnet --list-sdks
dotnet new list blazor
```

Do not assume template names from older .NET versions.

Preferred application template:

-   Blazor Web App with **Interactive Server only**, if that is the
    current supported .NET 10 template;
-   otherwise use the available Blazor Server template.

Do not create:

-   WebAssembly project
-   Separate SPA
-   Unnecessary API project
-   Separate frontend project

------------------------------------------------------------------------

# 4. Existing-System Rules

These are **known facts from the existing system**, not assumptions.

## 4.1 Password

Existing password implementation:

``` csharp
BCrypt.Net.BCrypt.HashPassword(text)
BCrypt.Net.BCrypt.Verify(text, hash)
```

Rules:

-   Reuse BCrypt.
-   Do not invent a new password algorithm.
-   Do not automatically migrate or rehash existing passwords.
-   Never log plaintext passwords.
-   Never log password hashes.
-   Never copy legacy debugging code that prints password/hash
    information.

## 4.2 Login Identity

  Field            Meaning
  ---------------- -----------------------------------
  `id`             Login username
  `UserID`         Application/audit user identifier
  `uid`            Identity primary key
  `CompanyCode`    Required for login lookup
  `BranchCode`     User branch
  `LocationCode`   User location
  `userlevel`      Access level/group identifier
  `changepass`     Forces password change
  `active`         Login eligibility

Login lookup:

``` text
Userlogin.id == username
AND Userlogin.CompanyCode == companyCode
        ↓
check active
        ↓
BCrypt verify password
        ↓
load user/company/branch/location information
```

## 4.3 Explicit non-goals for session behavior

Documented so agents do not invent them:

-   **No mid-session company switch** in v1 (user logs out and logs in
    to another company).
-   **No account lockout** in v1.
-   **Login endpoint rate limiting/throttling must be evaluated before
    production deployment**; do not treat unlimited password attempts as
    production-ready.
-   **No MFA** in v1.
-   Soft-delete / inactive beyond `active` is out of scope unless Phase
    0 finds more fields.

------------------------------------------------------------------------

# 5. Authentication Architecture

## 5.1 Primary mechanism

For a pure Blazor Server application, **do not make JWT the primary
authentication mechanism**.

Preferred architecture:

``` text
Login form (Blazor / Razor)
  ↓
POST /account/login  (antiforgery-protected)
  ↓
CompanyCode + Username + Password
  ↓
Userlogin
  ↓
BCrypt verification
  ↓
ClaimsPrincipal
  ↓
HttpContext.SignInAsync (cookie)
  ↓
Redirect
  ↓
Blazor circuit sees authenticated cookie
  ↓
AuthenticationStateProvider (built-in / revalidating)
  ↓
CurrentUserService
  ↓
Blazor pages/services
```

Logout:

``` text
POST /account/logout  (antiforgery-protected)
  ↓
HttpContext.SignOutAsync
  ↓
Redirect to /login
```

### Why HTTP endpoints are mandatory

Blazor interactive Server components **must not** call `SignInAsync` /
`SignOutAsync` directly on the circuit as the primary path. Cookie auth
requires an HTTP request that can set/clear the authentication cookie.

### AuthenticationStateProvider rule

-   Use the server authentication-state infrastructure backed by the
    ASP.NET Core authentication cookie.
-   **Do not** create `CustomAuthenticationStateProvider` that fakes
    sign-in without a cookie.
-   When database-backed periodic validation is required, use a small
    app-specific provider derived from
    `RevalidatingServerAuthenticationStateProvider`.
-   Initial revalidation interval: **5 minutes**, configurable through
    options.
-   Validation must confirm the user still exists and `active == true`.
-   A failed validation must invalidate the authentication state and
    force the normal login flow.
-   Do not perform database validation on every component render.
-   Do not add custom authentication-state machinery merely for
    convenience.

### JWT is deferred

JWT only when there is a real requirement such as:

``` text
Blazor Server → External REST API → JWT bearer
```

If needed later, add JWT as a separate concern --- not as core Blazor
authentication.

------------------------------------------------------------------------

# 6. Cookie Security Baseline

Configure cookie authentication explicitly in Phase 4:

  -----------------------------------------------------------------------
  Setting                             v1 baseline
  ----------------------------------- -----------------------------------
  `HttpOnly`                          **true**

  `SecurePolicy`                      **Always** when HTTPS; allow local
                                      HTTP only in Development if needed

  `SameSite`                          **Lax** (default unless cross-site
                                      POST is required)

  Sliding expiration                  **Yes** (confirm duration in Phase
                                      0 / ops preference)

  Absolute / expire time span         Document chosen values in
                                      `appsettings`

  Revalidation                        **5 minutes baseline**,
                                      configurable; validate account
                                      existence/active state

  Session invalidation                Failed revalidation invalidates the
                                      authentication state; no
                                      client-side token workaround

  Cookie name                         Explicit app-specific name (not
                                      default anonymous)

  Login path                          `/login`

  Access denied path                  `/unauthorized`
  -----------------------------------------------------------------------

After successful password change:

``` text
Update Userlogin
  ↓
Rebuild ClaimsPrincipal (change_password = false)
  ↓
SignInAsync again (refresh cookie claims)
  ↓
Redirect to /dashboard
```

Do not leave a stale `change_password` claim in the cookie after the
user has changed their password.

Never store authentication tokens in `localStorage` or `sessionStorage`.

------------------------------------------------------------------------

# 7. Login Flow

The login page must contain:

``` text
Company Code
Username
Password
```

Flow:

``` text
User submits login form
        ↓
POST /account/login (+ antiforgery)
        ↓
Validate required fields
        ↓
Find Userlogin by id + CompanyCode
        ↓
User exists?
    No → generic login failure
        ↓
Active?
    No → reject login (generic or explicit inactive — prefer generic)
        ↓
BCrypt.Verify()
        ↓
Create ClaimsPrincipal
        ↓
SignInAsync() → authentication cookie
        ↓
changepass?
    Yes → redirect /change-password
    No  → redirect /dashboard
```

Do not reveal whether the username or CompanyCode was incorrect.

Use a generic authentication failure message.

------------------------------------------------------------------------

# 8. Claims Design

Use standard ASP.NET Core claims where appropriate.

Recommended mapping:

  -----------------------------------------------------------------------
  Claim constant (`AppClaimTypes`)    Claim value source
  ----------------------------------- -----------------------------------
  `NameIdentifier` / `sub`            `uid`

  `Name`                              Full name

  `LoginId`                           Login username (`id`) --- **not** a
                                      bare claim type string `"id"`

  `UserId`                            `UserID`

  `CompanyCode`                       `CompanyCode`

  `BranchCode`                        `BranchCode`

  `LocationCode`                      `LocationCode`

  `Level`                             `userlevel`

  `ChangePassword`                    `changepass`
  -----------------------------------------------------------------------

Rules:

-   Use `uid` for `sub` / name identifier because it is the identity
    primary key.
-   Do not blindly preserve a legacy `sub` that contains display name.
-   Centralize all custom claim type strings in `AppClaimTypes.cs`.
-   Never scatter literal claim names throughout the application.

If legacy API compatibility later requires old claims such as `comp`,
`branch`, `location`, `rol`, add them deliberately and document why.

``` text
Authentication/
└── Claims/
    └── AppClaimTypes.cs
```

------------------------------------------------------------------------

# 9. Current User Context

Create:

``` text
ICurrentUserService
CurrentUserService
```

Implementation rules:

-   Resolve identity from **`AuthenticationStateProvider`** (scoped),
    not by reading `HttpContext` from interactive components.
-   Treat claims as authenticated identity data, not as a substitute for
    authorization logic.
-   Never allow component parameters or browser storage to override
    authenticated CompanyCode/BranchCode/LocationCode.
-   Expose authenticated ERP context, for example:

``` text
UserId
LoginId
FullName
CompanyCode
BranchCode
LocationCode
UserLevel
MustChangePassword
IsAuthenticated
```

Example conceptual usage:

``` csharp
_currentUser.CompanyCode
_currentUser.BranchCode
_currentUser.LocationCode
_currentUser.UserId
```

Purpose: avoid every page independently reading claims.

------------------------------------------------------------------------

# 10. ERP Data-Scope Security

Authorization and data scope are different.

### Authorization

> Can this user access Inventory?

### Data scope

> Which company/branch/location can this user access?

Foundation rule:

> **ERP queries must obtain CompanyCode/BranchCode/LocationCode from the
> authenticated current-user context rather than trusting values
> supplied by the browser.**

Prefer:

``` text
Authenticated User
        ↓
CurrentUserService
        ↓
CompanyCode / Branch / Location
        ↓
Application query
```

Do not build a full tenant/permission framework in v1, but make this
boundary explicit from the beginning.

### Non-negotiable data-scope rule

A component may request a business filter, but it must not be able to
widen the authenticated user's security scope.

Bad:

``` text
LoadInventory(companyCodeFromQueryString)
```

Good:

``` text
CurrentUserService.CompanyCode
        ↓
Inventory service/query
```

Any future multi-company or cross-branch feature must be deliberately
authorized rather than inferred from UI input.

------------------------------------------------------------------------

# 11. Change Password Gate

When `changepass` / `MustChangePassword` is true, allow **only**:

  -----------------------------------------------------------------------
  Path                                Allowed
  ----------------------------------- -----------------------------------
  `/change-password`                  Yes

  `/account/logout` (POST)            Yes

  `/login`                            Yes (after logout)

  Static assets / framework endpoints Yes
  required to render change-password  

  All other ERP pages                 **No** --- redirect to
                                      `/change-password`
  -----------------------------------------------------------------------

Implement the gate as **one centralized application security rule**. Do
not allow individual pages to decide independently whether the
password-change requirement applies.

The gate must cover:

-   Direct URL navigation.
-   Blazor internal navigation.
-   Full-page refresh.
-   Interactive Server circuit navigation.
-   Any ERP page or protected endpoint.

Allowed destinations while `MustChangePassword == true`:

-   `/change-password`
-   `POST /account/logout`
-   `/login`
-   Required framework/static resources.

Do not implement duplicate page-level `if (MustChangePassword)` checks
as the primary security mechanism.

Change-password flow:

``` text
Current password
New password
Confirm password
        ↓
Verify current password
        ↓
Validate new password
        ↓
BCrypt.HashPassword()
        ↓
Update password
        ↓
Set changepass = false
        ↓
Save
        ↓
Re-issue auth cookie with updated claims
        ↓
Continue to dashboard
```

Never log current password, new password, or password hash.

------------------------------------------------------------------------

# 12. Authorization (`userlevel` convention)

Do not implement a large permission framework yet.

### v1 convention

Until the real rights model is confirmed and designed:

1.  Phase 0 **inspects** the existing rights model (read-only). **No
    permission tables/handlers in v1.**
2.  Protect pages with `[Authorize]` by default for ERP pages.
3.  Use a thin helper / constant map for demo pages only, for example:

``` text
AppAccessLevels
  AdminDemo   → requires userlevel in { ... }  // filled from Phase 0 findings
  InventoryDemo → requires userlevel in { ... }
```

4.  Prefer simple checks via `ICurrentUserService.UserLevel` or a single
    small `IAccessService` with **one** implementation.
5.  Unauthorized access → `/unauthorized`.

Do **not** create in v1:

-   permission tables
-   policy trees
-   authorization handlers (unless one tiny policy is clearly cleaner
    than scattered checks)
-   dynamic permission engines

### Inspect vs implement (Phase 0 vs Phase 5)

  Activity                                 Allowed
  ---------------------------------------- ---------------
  Inspect legacy rights / menus / levels   Yes (Phase 0)
  Document findings                        Yes
  Implement full rights engine             **No**

------------------------------------------------------------------------

# 13. Authentication Test Matrix

Authentication is not complete until these cases are tested:

``` text
[ ] Valid CompanyCode + Username + Password
[ ] Wrong password
[ ] Unknown username
[ ] Wrong CompanyCode
[ ] Inactive user
[ ] changepass = true → forced to /change-password
[ ] changepass = true → cannot open /dashboard or demo pages
[ ] changepass = false → normal access
[ ] Password change refreshes cookie claims
[ ] Logout clears auth cookie
[ ] Authentication expiration
[ ] Direct access to [Authorize] page while unauthenticated
[ ] Authenticated user accessing unauthorized page
[ ] Company context comes from authenticated identity
[ ] User cannot authenticate against another company's Userlogin record
[ ] Login/logout use HTTP endpoints (cookie set/cleared)
[ ] Antiforgery enforced on login/logout POSTs
```

------------------------------------------------------------------------

# 14. Database Architecture

Use:

``` text
AppDbContext
IDbContextFactory<AppDbContext>
```

For Blazor Server, do not keep a long-lived `DbContext` inside a
component.

Preferred pattern:

``` csharp
await using var db = await _dbFactory.CreateDbContextAsync();
```

Rules:

-   Use `AsNoTracking()` for read-only queries where appropriate.
-   Do not expose `IQueryable` beyond the layer where its DbContext
    lifetime is guaranteed.
-   Do not store DbContext in component state.
-   Do not enable EF sensitive-data logging in production.
-   Use a configurable EF Core command timeout; initial baseline **30
    seconds** unless a specific operation requires otherwise.
-   Prefer `CancellationToken` propagation for service/database
    operations that can reasonably be cancelled.
-   Do not globally increase command timeouts to hide slow queries.

------------------------------------------------------------------------

# 15. Existing Userlogin Table

Map the existing table exactly.

Important:

-   Confirm actual SQL object name.
-   Confirm column names/types.
-   Confirm nullability.
-   Confirm primary key.
-   Confirm existing indexes.
-   Do not rename columns.
-   Do not modify the table.
-   Do not create Identity tables.

Use Fluent configuration:

``` text
Data/
├── AppDbContext.cs
├── Entities/
│   └── UserLogin.cs
└── Configurations/
    └── UserLoginConfiguration.cs
```

Database migration must never modify the production `Userlogin` schema.

------------------------------------------------------------------------

# 16. Connection Strings and Secrets

Development:

-   Use User Secrets where appropriate.
-   Do not commit passwords or connection strings containing secrets.

Production:

-   Use secure deployment configuration/environment variables/secret
    storage appropriate to the hosting environment.

Never log:

``` text
Connection strings
Passwords
JWT secrets
Encryption keys
Cookies
Authorization headers
```

------------------------------------------------------------------------

# 17. Architecture

Keep **one project** for v1.

Recommended:

``` text
ErpWeb/
├── Authentication/
│   ├── IAuthService.cs
│   ├── AuthService.cs
│   ├── ICurrentUserService.cs
│   ├── CurrentUserService.cs
│   ├── AccountEndpoints.cs          # POST /account/login, /account/logout
│   └── Claims/
│       └── AppClaimTypes.cs
│
├── Components/
│   ├── Layout/
│   │   └── ThemeSwitcher/
│   ├── Pages/
│   │   ├── Login
│   │   ├── Dashboard
│   │   ├── InventoryDemo
│   │   ├── AdminDemo
│   │   ├── ChangePassword
│   │   └── Unauthorized
│   └── Common/
│       └── DataGrid/
│
├── Configuration/
│   ├── AuthenticationOptions.cs
│   ├── CookieOptions.cs
│   ├── LoggingOptions.cs
│   └── DatabaseOptions.cs
│
├── Data/
│   ├── AppDbContext.cs
│   ├── Entities/
│   │   └── UserLogin.cs
│   └── Configurations/
│       └── UserLoginConfiguration.cs
│
├── Services/
│   ├── Access/                      # thin userlevel helpers only
│   └── Theme/
│
├── Models/
│
├── wwwroot/
│
├── Program.cs
└── appsettings*.json
```

Do **not** add `CustomAuthenticationStateProvider.cs` by default.

Do not create empty folders just for theoretical future architecture.

Defer:

``` text
Services/Api
Full permission framework
Multi-project solution
Domain / Application / Infrastructure projects
Separate API project
Extra ERP modules
```

------------------------------------------------------------------------

# 18. API Client Decision

Do not create `IApiClient` in v1 unless there is an actual API
requirement.

Primary architecture:

``` text
Blazor component → In-process service → EF Core → SQL Server
```

Not:

``` text
Blazor component → HttpClient → API → EF Core → SQL Server
```

------------------------------------------------------------------------

# 19. CommonDataGrid\<T\>

The reusable grid is based on the existing `CommonDataGridEx`.

Existing useful capabilities:

-   Dynamic typed columns
-   Search, filter, group, sort
-   Single/multiple selection
-   Toolbar, row actions
-   Summaries, export, column chooser
-   Persistent layout

The new grid must remove legacy technical debt.

Delivered across **Phases 7a--7d** (see §27), not one giant pass.

------------------------------------------------------------------------

# 20. CommonDataGrid Rules

  -----------------------------------------------------------------------
  Legacy issue                        New rule
  ----------------------------------- -----------------------------------
  `async void`                        `async Task` unless framework event
                                      requires otherwise

  Parameter mutation                  Parameters are treated as immutable

  Business logic inside grid          Parent component owns business
                                      logic

  Title as persistence key            Use explicit `GridKey`

  Scattered JS                        Centralize through
                                      `IGridLayoutStorage`

  Exposing `DxGrid` everywhere        Prefer wrapper methods

  Inventory-specific properties       Remove from generic grid

  Excess `!`                          Prefer proper null handling
  -----------------------------------------------------------------------

The grid must be reusable without knowing Inventory, Sales, Purchase,
HR, or Accounting.

------------------------------------------------------------------------

# 21. CommonDataGrid v1 Scope

Supported base types:

``` text
string
datetime
time
int
decimal
double
bool
```

Keep advanced behavior template-based.

Do not hardcode:

``` text
if column == "StockCode"
if page == "Inventory"
if action == "PostInvoice"
```

Expose callbacks/templates only when a real page needs them:

``` text
ColumnTemplate
CellTemplate
RowActions
ToolbarContent
EmptyState
```

------------------------------------------------------------------------

# 22. Grid Persistence

Use an explicit `GridKey` (never the displayed title).

Example:

``` text
GridKey = "inventory-item-list"
```

Persist only safe layout information.

Remove unsupported or undesirable state such as `FilterCriteria` before
saving if required by existing behavior.

JS access isolated behind `IGridLayoutStorage`.

------------------------------------------------------------------------

# 23. Theme Architecture

Use the DevExpress theme-switching approach as the reference
implementation.

Initial themes:

``` text
Fluent Light
Fluent Dark
DevExpress Classic
Bootstrap External
```

Treat **Fluent Light / Fluent Dark** as the primary ERP theme pair.

`Classic` and `Bootstrap External` are compatibility/demo choices and
must not multiply the production theme state unnecessarily.

Theme switching must be centralized. Do not scatter manual `<link>`
operations throughout components.

Adapt the official DevExpress theme and size-mode switcher example
rather than blindly copying it.

Persist the selected theme using a cookie or equivalent
server-compatible persistence mechanism.

### Known risk (must prove in Phase 2)

Avoid conflicting states between:

``` text
DevExpress theme
Bootstrap theme
data-bs-theme
```

When Bootstrap color modes are used, manage `data-bs-theme="light|dark"`
deliberately.

Theme changes must:

-   Work at runtime
-   Survive refresh
-   Not require application restart
-   Not break DevExpress components
-   Not break custom Bootstrap layout

------------------------------------------------------------------------

# 24. Serilog

Use Serilog with rolling files.

**Baseline Serilog is part of Phase 1** so database and authentication
work is diagnosable.

Recommended baseline:

  -----------------------------------------------------------------------
  Setting                             Value
  ----------------------------------- -----------------------------------
  Location                            `Logs/`

  Rolling                             Daily

  Maximum file size                   10 MB

  Retention                           30 files (file-count based; not
                                      guaranteed to equal 30 calendar
                                      days)

  Size rolling                        `RollOnFileSizeLimit = true`

  Request logging                     Yes, sanitized

  Structured logging                  Yes
  -----------------------------------------------------------------------

Never log:

``` text
Passwords
Password hashes
JWTs
Secrets
Connection strings
Authorization headers
Cookies
Session tokens
```

Do not enable `EnableSensitiveDataLogging()` in production.

When request logging is enabled, explicitly exclude or sanitize:

-   Password fields.
-   Authorization headers.
-   Cookies.
-   Request/response bodies containing credentials or secrets.
-   Connection strings.
-   Tokens and hashes.

Do not assume a generic request logger is safe merely because it is
structured.

Safe contextual enrichment may include `UserId`, `CompanyCode`,
`BranchCode` when volume remains reasonable.

------------------------------------------------------------------------

# 25. Logging Levels

``` text
Development:
Debug / Information

Production:
Information / Warning / Error
```

Avoid globally enabling extremely verbose framework/EF logging in
production.

------------------------------------------------------------------------

# 25A. Error Handling and Failure Boundaries

The foundation must distinguish between **user-facing errors**,
**expected business failures**, and **unexpected technical failures**.

### Production rules

-   Do not display exception stack traces to users.
-   Unexpected exceptions must be logged through Serilog with
    correlation/context information.
-   Use Blazor `ErrorBoundary` where appropriate for component-level
    failures.
-   Use the application's exception-handling pipeline for HTTP request
    failures.
-   Return user-friendly error messages without leaking SQL, connection
    strings, claims, cookies, stack traces, or internal paths.
-   Development may expose detailed diagnostics locally.
-   Production must expose only safe error information.

### Logging rule

Every unexpected exception should have enough structured context to
diagnose it without logging secrets.

Recommended contextual fields:

``` text
Environment
RequestPath
TraceId
UserId
CompanyCode
BranchCode
LocationCode
```

Only add user/company context after authentication exists and only where
the resulting log volume is acceptable.

------------------------------------------------------------------------

# 26. Project Configuration

``` text
appsettings.json
appsettings.Development.json
appsettings.Production.json
```

Use strongly typed options where appropriate.

Recommended options boundaries:

``` text
AuthenticationOptions
CookieOptions
DatabaseOptions
LoggingOptions
ThemeOptions
```

Keep security-sensitive defaults safe. Configuration must not allow an
operator to accidentally disable authentication, antiforgery, HTTPS
cookie protection, or data-scope checks through a casual setting.

Avoid hardcoding connection strings, secret keys, environment-specific
URLs, and deployment-specific file paths.

------------------------------------------------------------------------

# 27. Implementation Phases

## Phase 0 --- Final Decisions and Inspection

Before writing application code:

-   Confirm project output path.
-   Confirm actual .NET 10 SDK.
-   Confirm current Blazor template.
-   Confirm DevExpress 26.1.x package.
-   Confirm Bootstrap 5.
-   Inspect the reference system.
-   Confirm `Userlogin` schema.
-   Confirm CompanyCode requirement.
-   Confirm BCrypt behavior.
-   Confirm `userlevel` meaning and demo-page level map.
-   Confirm branch/location loading.
-   Confirm `changepass`.
-   Inspect rights model **read-only** (document only; no engine).
-   Confirm production database must remain untouched.
-   Decide cookie expiration values.
-   Decide Inventory demo data source (see Phase 7 note).
-   Verify the exact .NET 10 Blazor Web App template/render-mode
    configuration.
-   Verify DevExpress **26.1.3 stable** package; do not use prerelease
    26.1.4 builds for the baseline.
-   Verify the intended authentication-state/revalidation approach.
-   Decide initial cookie expiration and **5-minute revalidation**
    values.
-   Decide database command timeout baseline (30 seconds).
-   Define the centralized `changepass` gate and its allowlist.
-   Define the minimum automated authentication/security test suite.
-   Decide production login rate-limiting requirements before
    deployment.

**Gate:** No implementation until authentication/data assumptions are
verified.

------------------------------------------------------------------------

## Phase 1 --- Project Foundation + Serilog Baseline

Create:

-   .NET 10 Blazor Web App
-   Interactive Server only
-   Global `Routes` + `HeadOutlet` render mode = `InteractiveServer`
-   No `.Client` project
-   No Interactive WebAssembly
-   No Interactive Auto
-   Bootstrap 5
-   DevExpress 26.1.x
-   MainLayout / NavMenu
-   Basic error handling
-   Environment configuration
-   **Serilog baseline** (rolling files, sanitized request logging, no
    secrets)

Verify:

``` text
dotnet restore
dotnet build
dotnet run
```

Confirm logs write to `Logs/` without leaking secrets.

**Gate:** Clean build, app starts, baseline logging works.

------------------------------------------------------------------------

## Phase 2 --- Theme System

Implement:

-   DevExpress theme resources
-   Theme switcher
-   Fluent Light / Fluent Dark / Classic / Bootstrap
-   Persistence
-   Deliberate `data-bs-theme` coordination with DX theme

Test:

``` text
[ ] Change theme
[ ] Refresh — theme remains
[ ] Navigate between pages
[ ] DevExpress controls render correctly
[ ] No conflicting DX vs Bootstrap theme state
```

**Gate:** Theme system works without layout/resource conflicts.

------------------------------------------------------------------------

## Phase 3 --- Database Foundation

Implement:

-   Connection configuration
-   User Secrets for development
-   `AppDbContext`
-   `IDbContextFactory`
-   `UserLogin` + Fluent mapping
-   Read-only database verification

Do not perform migrations against `Userlogin`.

Test:

``` text
[ ] Connect to database
[ ] Read Userlogin
[ ] Correct field mapping / null handling / key mapping
```

**Gate:** Existing data can be read safely.

------------------------------------------------------------------------

## Phase 4 --- Authentication

Implement:

-   Login page (CompanyCode + Username + Password)
-   **`POST /account/login`** and **`POST /account/logout`** with
    antiforgery
-   BCrypt verification
-   Cookie authentication with §6 security baseline
-   ClaimsPrincipal + `AppClaimTypes`
-   Built-in / revalidating `AuthenticationStateProvider` (no fake
    custom provider)
-   `CurrentUserService` from auth state
-   Expiration
-   Account revalidation strategy
-   **5-minute configurable authentication-state revalidation baseline**
-   Centralized change-password gate + re-issue cookie after change
-   Revalidation must reject missing/inactive users
-   No database query on every render

Do not implement JWT unless a concrete API requirement exists.

**Gate:** Authentication test matrix passes.

------------------------------------------------------------------------

## Phase 5 --- Authorization and ERP Context

Implement:

-   `[Authorize]` on ERP pages
-   `AuthorizeView` where useful
-   Thin `userlevel` checks per Phase 0 map
-   Unauthorized page
-   Company/branch/location data-scope rules in services
-   Explicit server-side rule that UI-supplied scope cannot widen
    authenticated scope
-   No trust of CompanyCode/BranchCode/LocationCode from query string,
    form fields, component parameters, localStorage, or sessionStorage

Test:

``` text
[ ] Unauthenticated user blocked
[ ] Authorized user allowed
[ ] Unauthorized userlevel blocked
[ ] User context available
[ ] Company context cannot be spoofed through UI
```

**Gate:** AuthN/AuthZ secure enough for demo ERP pages.

------------------------------------------------------------------------

## Phase 6 --- Logging Hardening

Phase 1 already added Serilog. This phase hardens it:

-   Confirm 10 MB / 30-file retention
-   Enrich with safe user/company context after auth exists
-   Structured exception review
-   Leakage review (passwords, hashes, cookies, headers, connection
    strings)
-   Production vs Development level review
-   Verify `RollOnFileSizeLimit`
-   Verify retention semantics
-   Verify request logging does not capture request/response secrets
-   Verify correlation/trace context is available for unexpected
    exceptions

Test:

``` text
[ ] Logs written
[ ] Rolling / retention works
[ ] No password/hash/JWT/cookie/secret leakage
[ ] Auth failures logged safely (no credentials)
```

**Gate:** Logging is production-safe for the foundation.

------------------------------------------------------------------------

## Phase 7 --- CommonDataGrid\<T\> (split)

First inspect existing `CommonDataGridEx`.

### Inventory demo data (decide in Phase 0)

Pick **one**:

  -----------------------------------------------------------------------
  Option                              When
  ----------------------------------- -----------------------------------
  **A.** Read-only query against an   Table exists and is safe to read
  existing inventory/list table       

  **B.** In-memory / stub DTO list    No safe inventory table yet
  for UI validation only              
  -----------------------------------------------------------------------

Do not invent production inventory schema in the foundation.

### Grid architecture boundary

`CommonDataGrid<T>` is a **UI component only**.

It must not:

-   Own `DbContext`.
-   Query SQL Server directly.
-   Decide CompanyCode/BranchCode/LocationCode scope.
-   Contain ERP business rules.
-   Become a generic repository/query engine.

Parent pages/services own data retrieval and business actions.

### Phase 7a --- Core grid

-   Typed columns, bind data, search, filter, sort
-   Remove `async void`, inventory-specific props, business logic from
    base

**Gate:** Grid renders and filters/sorts on the demo page.

### Phase 7b --- Interaction

-   Selection, toolbar, row actions (callbacks/templates)

**Gate:** Parent owns actions; grid stays generic.

### Phase 7c --- Analytics surface

-   Summaries, export, column chooser

**Gate:** Features work without page-specific hardcoding.

### Phase 7d --- Persistence

-   Explicit `GridKey`
-   `IGridLayoutStorage`
-   Safe layout persistence

**Gate:** Layout survives refresh; title is not the key.

Use **one Inventory demo page** as the first real consumer across
7a--7d.

------------------------------------------------------------------------

## Phase 8 --- Demo Pages and Hardening

Thin examples only:

``` text
Dashboard
Admin Demo
Inventory Demo
Change Password
Unauthorized
```

Do not build real ERP business modules in this phase.

Perform:

-   Security review (cookie, claims, changepass gate, data scope)
-   Error-handling review
-   Logging review
-   Grid review
-   Theme review
-   Database lifetime review
-   `dotnet build`
-   Manual test checklist
-   Automated authentication/security tests
-   ErrorBoundary / exception-path verification
-   Cancellation and timeout behavior review
-   Production configuration review
-   Login rate-limiting readiness review

**Gate:** Foundation stable enough to begin real ERP modules.

------------------------------------------------------------------------

# 28. Mandatory Phase Gate

After every major phase (and each 7a--7d sub-phase):

``` text
BUILD → TEST → REVIEW → APPROVE → CONTINUE
```

If a phase fails, fix it before moving forward.

Do not accumulate multiple unresolved phases.

### Gate evidence

Each phase must leave behind:

``` text
BUILD result
TEST result
REVIEW result
Known issues
Explicit approval
```

A phase is not complete merely because the application starts.

------------------------------------------------------------------------

# 29. AI Coding-Agent Rules

### Rule 1 --- Inspect before inventing

Before changing authentication, database mapping, grid behavior, or
business rules: read the reference implementation first.

### Rule 2 --- Do not redesign production behavior without evidence

Existing behavior is preserved unless the plan explicitly changes it.

### Rule 3 --- Small implementation batches

One phase or one sub-phase at a time (especially 7a--7d).

### Rule 4 --- Build after changes

Every meaningful change followed by `dotnet build`.

### Rule 5 --- Review generated code

Do not assume generated code is production-ready.

### Rule 6 --- No unnecessary abstractions

If an abstraction has only one implementation and no demonstrated reuse
requirement, do not create it automatically.

### Rule 7 --- No unrelated refactoring

Do not modify unrelated modules while implementing the foundation.

### Rule 8 --- Security-sensitive changes require explicit review

Authentication, authorization, cookies, claims, database access, and
logging must receive separate review.

### Rule 9 --- No fake cookie auth

Do not implement "login" that only mutates a custom
`AuthenticationStateProvider` without setting the ASP.NET Core auth
cookie via HTTP endpoints.

### Rule 10 --- Do not invent framework APIs

Before using a .NET 10 or DevExpress API, verify it against the
installed package/reference assemblies or official documentation. Do not
copy APIs from older .NET/DevExpress versions without validation.

### Rule 11 --- Security changes are evidence-driven

For authentication, authorization, cookies, claims, password handling,
antiforgery, database scope, and logging:

``` text
Inspect existing behavior
        ↓
State intended rule
        ↓
Implement smallest change
        ↓
Build
        ↓
Test negative cases
        ↓
Review
```

### Rule 12 --- Preserve the database contract

Do not rename, normalize, migrate, or "clean up" existing production
tables during foundation work.

### Rule 13 --- Do not weaken security to fix UI problems

Never solve a Blazor/DevExpress rendering issue by disabling
authorization, antiforgery, HTTPS, cookie security, or data-scope
validation.

------------------------------------------------------------------------

# 30. Definition of Done

## Framework

-   [ ] .NET 10
-   [ ] Blazor Server / Interactive Server only
-   [ ] DevExpress Blazor 26.1.x
-   [ ] Bootstrap 5
-   [ ] No WASM project
-   [ ] HTTPS cookie policy understood and configured
-   [ ] Interactive Server is the only enabled interactive render mode
-   [ ] No `.Client` project / WASM packages / Interactive Auto

## Database

-   [ ] SQL Server configured securely
-   [ ] EF Core configured
-   [ ] `IDbContextFactory`
-   [ ] Existing `Userlogin` mapped
-   [ ] Existing schema untouched
-   [ ] No ASP.NET Identity tables
-   [ ] No long-lived DbContext in components

## Authentication

-   [ ] BCrypt verified against existing data
-   [ ] CompanyCode + username + password
-   [ ] `POST /account/login` and `POST /account/logout` with
    antiforgery
-   [ ] ASP.NET Core cookie authentication
-   [ ] HttpOnly + Secure (+ SameSite) configured
-   [ ] Claims documented via `AppClaimTypes`
-   [ ] `sub` / name identifier maps to `uid`
-   [ ] LoginId claim is not a bare `"id"` literal scattered in code
-   [ ] Logout handled
-   [ ] Expiration handled
-   [ ] `changepass` gate allowlist enforced
-   [ ] Cookie re-issued after password change
-   [ ] No custom fake auth state provider
-   [ ] Authentication-state revalidation strategy documented
-   [ ] 5-minute configurable revalidation baseline tested
-   [ ] Inactive/deleted user invalidation tested
-   [ ] No localStorage authentication token
-   [ ] No password/hash/token logging

## Authorization

-   [ ] `[Authorize]`
-   [ ] `AuthorizeView` where useful
-   [ ] Thin `userlevel` handling per Phase 0 map
-   [ ] Unauthorized page
-   [ ] CurrentUserService from AuthenticationStateProvider
-   [ ] Company/branch/location context
-   [ ] UI cannot arbitrarily override authenticated company context
-   [ ] Company/branch/location scope is resolved server-side
-   [ ] Negative spoofing tests pass

## Grid

-   [ ] `CommonDataGrid<T>` delivered via 7a--7d
-   [ ] Search / filter / sort / group as scoped
-   [ ] Selection / toolbar / row actions
-   [ ] Summaries / export / column chooser
-   [ ] Persistent layout + explicit GridKey
-   [ ] No business-specific logic
-   [ ] No unnecessary parameter mutation
-   [ ] No `async void` unless framework requires it

## Theme

-   [ ] Fluent Light / Fluent Dark / Classic / Bootstrap
-   [ ] Runtime switching + persistence
-   [ ] No conflicting theme state

## Logging

-   [ ] Serilog from Phase 1; hardened in Phase 6
-   [ ] Daily rolling / 10 MB / 30-file retention
-   [ ] Structured + sanitized request logging
-   [ ] No secrets / passwords / hashes / tokens / cookies / auth
    headers
-   [ ] No production sensitive EF logging
-   [ ] Request logging sanitization verified
-   [ ] File-size rolling and retention behavior verified

## Quality

-   [ ] Builds and runs successfully
-   [ ] Automated authentication/security tests pass
-   [ ] Error handling does not leak sensitive implementation details
-   [ ] Database command timeout and cancellation rules are documented
-   [ ] No unnecessary abstractions / unrelated refactoring
-   [ ] Authentication / authorization / company isolation tests pass
-   [ ] Theme / grid / logging / security reviews completed
-   [ ] Final implementation report written

------------------------------------------------------------------------

# 31. Final Architecture

``` text
                    Browser
                       │
                       ▼
              Blazor Server
              Interactive Server
                       │
        ┌──────────────┼───────────────┐
        │              │               │
        ▼              ▼               ▼
 Authentication   UI Components    CommonDataGrid
 (cookie + HTTP
  account endpoints)
        │              │               │
        ▼              ▼               │
CurrentUser       Services            │
         │              │               │
         └──── ErrorBoundary / Logging ─┘
        │              │               │
        └──────────────┼───────────────┘
                       │
                       ▼
                IDbContextFactory
                       │
                       ▼
                  EF Core
                       │
                       ▼
                   SQL Server
```

Authentication:

``` text
Form POST /account/login
       ↓
CompanyCode + Username + Password
       ↓
Userlogin + BCrypt
       ↓
ClaimsPrincipal
       ↓
Cookie Authentication
       ↓
AuthenticationStateProvider
       ↓
CurrentUserService
```

Future API (only if required):

``` text
Blazor Server ──► Internal Services ──► SQL Server

Blazor Server ──► External API
                      └── JWT only where required
```

------------------------------------------------------------------------

# 32. Non-Goals for v1

Do **not** build as part of the foundation:

-   Full ERP modules
-   Full permission engine
-   ASP.NET Identity migration
-   Database redesign / Userlogin schema changes
-   Separate API project / WASM / mobile API
-   Multi-project clean architecture
-   CQRS/MediatR unless required
-   Generic repository / unit-of-work over EF Core without need
-   Universal grid framework / complex caching / message bus / jobs /
    microservices
-   Mid-session company switching
-   Account lockout / MFA (explicitly deferred)

Objective: strong foundation, not over-engineering.

------------------------------------------------------------------------

# 33. Recommended Future Expansion

``` text
Foundation → Inventory → Sales → Purchase → Production → Accounting → Reporting / BI → External API / Mobile
```

Each module reuses foundation auth, current-user/company context, DB
access pattern, logging, theme, grid, and authorization conventions.

------------------------------------------------------------------------

# 34. Final Engineering Principles

### Keep it simple

Do not build infrastructure before it is needed.

### Reuse existing behavior

Production behavior is the source of truth unless deliberately changed.

### Secure by default

Auth, company scope, cookies, logging, and DB access must not rely on
browser-supplied trust.

### Prefer composition over giant generic abstractions

Build reusable components from proven use cases.

### Keep business logic out of infrastructure components

`CommonDataGrid<T>` knows grids, not ERP rules.

### Keep the database boundary explicit

`IDbContextFactory` + short-lived DbContexts.

### Make AI agents work incrementally

One phase → build → test → review → next phase.

------------------------------------------------------------------------

# 35. Final Recommendation

  ----------------------------------------------------------------------------
  \#                      Decision                Recommended
  ----------------------- ----------------------- ----------------------------
  1                       Framework               **.NET 10**

  2                       UI                      **Blazor Server /
                                                  Interactive Server only**

  3                       Project path            `C:\BlazorProjects\ErpWeb`
                                                  or agreed path

  4                       CompanyCode login       **Yes**

  5                       Authentication          **Cookie auth via HTTP
                                                  account endpoints**

  6                       Auth state              **Built-in / revalidating
                                                  --- no fake custom
                                                  provider**

  7                       JWT / ApiClient         **Defer**

  8                       DevExpress              **26.1.3 stable baseline;
                                                  26.1.4 only after stable
                                                  release + validation**

  9                       Database                Existing SQL Server /
                                                  `Userlogin` unchanged

  10                      Password                Existing BCrypt

  11                      Architecture            **Single project**

  12                      Grid                    `CommonDataGrid<T>` in
                                                  phases 7a--7d

  13                      Logging                 Serilog from Phase 1; harden
                                                  in Phase 6

  14                      Theme                   DevExpress theme switcher +
                                                  Bootstrap 5

  15                      Render mode             **Interactive Server only;
                                                  global Routes/HeadOutlet
                                                  configuration**

  16                      Auth revalidation       **5-minute configurable
                                                  server revalidation
                                                  baseline**

  17                      Security scope          **Company/branch/location
                                                  resolved server-side; UI
                                                  cannot widen scope**

  18                      Testing                 **Automated auth/security
                                                  tests + phase-specific
                                                  manual tests**

  19                      Error handling          **Centralized exception
                                                  handling + ErrorBoundary +
                                                  sanitized Serilog**

  20                      DB operations           **30s configurable timeout +
                                                  CancellationToken
                                                  propagation where
                                                  appropriate**
  ----------------------------------------------------------------------------

------------------------------------------------------------------------

# 36. Approval Gate

Before implementation begins, confirm:

``` text
[ ] Project output path
[ ] .NET 10 SDK verified
[ ] DevExpress **26.1.3 stable** package verified (no prerelease baseline)
[ ] CompanyCode login confirmed
[ ] Cookie auth + HTTP login/logout endpoints confirmed
[ ] JWT deferred
[ ] ApiClient deferred
[ ] Existing Userlogin schema verified
[ ] BCrypt behavior verified
[ ] userlevel demo map documented (inspect-only rights model)
[ ] changepass allowlist agreed
[ ] Cookie expiration values agreed
[ ] 5-minute authentication revalidation baseline agreed
[ ] Centralized changepass gate agreed
[ ] Automated authentication/security test cases agreed
[ ] Production login rate-limiting requirement assessed
[ ] Inventory demo data option A or B chosen
[ ] Reference project available
```

After approval:

> **Implement Phase 1 only. Do not implement Phase 2+ until Phase 1
> passes Build → Test → Review.**

------------------------------------------------------------------------

# 37. Win Condition

The project succeeds when it provides:

``` text
Simple + Reusable + Secure + Maintainable + ERP-ready
```

The foundation should make the next ERP module **faster and safer to
build**, not make the foundation itself unnecessarily complicated.

### 10/10 quality bar

The foundation is considered implementation-ready only when:

``` text
Framework choices are verified
        +
Authentication is real cookie authentication
        +
Authentication state can be revalidated
        +
ERP data scope is server-owned
        +
Database lifetime is safe for Blazor Server
        +
Logging is useful but sanitized
        +
Theme/grid are reusable without becoming frameworks
        +
Security negative cases are tested
        +
Every phase has build/test/review evidence
```

This is the standard the AI coding agent must meet before the first real
ERP module is started.
