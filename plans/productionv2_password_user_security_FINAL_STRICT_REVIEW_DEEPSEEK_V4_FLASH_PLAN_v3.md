# productionv2 Password & User Security Hardening
## FINAL STRICT RE-REVIEW v3 — AI Coding Agent Plan for DeepSeek V4 Flash

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Final review date:** 2026-10-22  
**Repository head observed during this review:** `99e9abbd7646c4c7796875bc316b55a3901aa9fc`  
**Target implementation agent:** DeepSeek V4 Flash  
**Status:** **APPROVED FOR IMPLEMENTATION, CONDITIONAL ON ALL PHASE GATES**  
**Important:** This plan deliberately does **not** claim 10/10 before implementation. The authentication/cookie/circuit integration, SQL Server concurrency paths, IIS sub-path behavior, and final tests must pass before any 10/10 claim is justified.

> This file supersedes all previous password-management plan versions.

---

# 1. Objective

Harden the CURRENT `productionv2` password management, User Accounts, authentication-session, role-security and forgot-password behavior without replacing the existing custom authentication architecture.

The implementation must fix the current repository defects while preserving:

- existing `dbo.userlogin`,
- BCrypt.Net-Next,
- cookie authentication,
- CompanyCode-based login scope,
- current `ADMIN` / `SYSTEM_ADMIN` concepts,
- current `AppBasePath` / IIS sub-application behavior,
- current ERP transaction/costing/quantity logic.

Do **not** migrate to ASP.NET Core Identity.

---

# 2. Critical Repository Findings Confirmed

## 2.1 Edit User is coupled to password validation

Current:

```text
AdminUser
    -> ListForAdminAsync returns UserLogin
    -> repository loads password hash then blanks password
    -> CloneForEdit sets password=""
    -> EditForm uses DataAnnotationsValidator
    -> UserLogin.password is [Required]
    -> password control is hidden during Edit
```

Result:

```text
ordinary profile edit can fail password validation
```

The fix is NOT removing `[Required]` from `UserLogin.password`.

The fix is to remove `UserLogin` from User Accounts UI binding.

---

## 2.2 Current repository already has a password-free projection

Existing:

`ErpWeb.Model/Repositories/PasswordAdminUserRow.cs`

Current fields include:

- Uid
- LoginId
- Name
- UserId
- Active
- ChangePass

Do **not** create a competing `UserAdminRow` class unless the existing projection is demonstrably unsuitable.

Preferred solution:

**extend and rename only if necessary** the existing password-free projection so the full User Accounts grid can use it.

This is a correction to the previous plan.

---

## 2.3 `CanViewPrice` is silently lost during edit

Current `CloneForEdit(...)` does not copy `CanViewPrice`.

Current repository update writes:

```csharp
existing.CanViewPrice = user.CanViewPrice;
```

So unrelated edits can turn an existing `true` into `false`.

Must be fixed.

---

## 2.4 User Accounts over-posts persistence fields

Current `UpdateUserAsync(UserLogin user)` and `UpdateProfileAsync(UserLogin user)` permit UI models to carry fields that User Accounts does not actually edit, including:

- CompanyCode
- BranchCode
- LocationCode
- ImagePath
- password
- audit fields

The new edit contract must expose only fields the screen intentionally edits.

Do not add Branch/Location editing as a side effect of this task.

---

## 2.5 ADMIN -> SYSTEM_ADMIN privilege escalation

Current User Accounts:

```text
Group Level combo
AllowUserInput="true"
```

Current service does not validate requested role.

Current login creates role claim directly from:

```text
userlogin.userlevel
```

Therefore a company ADMIN can potentially set:

```text
SYSTEM_ADMIN
```

and on next login become a global/platform administrator.

This is a release-blocking security defect.

---

## 2.6 SYSTEM_ADMIN target protection is missing

Current UserAdminService allows ADMIN and SYSTEM_ADMIN into the service but does not implement a target hierarchy.

A company ADMIN can currently attempt to:

- edit SYSTEM_ADMIN,
- deactivate SYSTEM_ADMIN,
- reset SYSTEM_ADMIN password,
- toggle force-change on SYSTEM_ADMIN,
- soft-delete SYSTEM_ADMIN.

Must be blocked server-side.

---

## 2.7 Built-in Role administration also undermines this boundary

Current:

`ErpWeb.Core/Security/RoleAdminService.cs`

A normal ADMIN can currently:

- create a role named `SYSTEM_ADMIN` if it does not already exist in that company,
- edit SYSTEM_ADMIN role metadata,
- deactivate SYSTEM_ADMIN,
- deactivate ADMIN.

Although User Accounts will be hardened separately, leaving Role Administration unrestricted weakens the same privilege boundary and can cause lockout/DoS.

This was missing from v2 and is now mandatory.

---

## 2.8 UserRoleSyncService creates roles during login

Current:

```text
userlevel role not found
    -> creates new Role
    -> CreatedBy="LOGIN"
```

Authentication must never create authorization metadata from an unexpected user row value.

Remove this behavior.

---

## 2.9 Current role synchronization is effectively single-authoritative-userlevel

Documentation says multiple roles are supported.

However current productionv2 `UserRoleSyncService` uses:

```text
userlogin.userlevel
```

as authoritative and removes other current-company mappings.

For this hardening task:

- preserve this current legacy behavior,
- do not redesign multi-role administration,
- require the authoritative role to already exist and be active,
- never leave stale elevated current-company mappings,
- never auto-create roles.

True multi-role lifecycle redesign is a separate project.

---

## 2.10 Current password writers are last-write-wins

Current README explicitly documents:

```text
Concurrent password writers are last-write-wins
```

Current password write methods do not compare the state that was read.

This can cause:

- self password change overwriting admin reset,
- admin reset overwriting another password update,
- force-change clear defeating a concurrent reset.

Must use compare-and-set or equivalent concurrency protection.

---

## 2.11 Soft-deleted account behavior is inconsistent

Soft-delete marker:

```text
UpdatedUID = "deleted"
active = false
```

But:

- `GetAdminRowAsync` does not exclude soft-deleted rows,
- `ResetPasswordAsync` does not exclude soft-deleted rows,
- `SetChangePassAsync` does not exclude soft-deleted rows,
- `LoginExistsAsync` currently excludes deleted rows even though the DB unique index still reserves `(id, CompanyCode)`.

This creates hidden reset targets and false "available login ID" behavior.

Must be corrected.

---

## 2.12 Forced-password-change security is incomplete

Current UI redirect:

`MainLayout.razor.cs`

redirects when `MustChangePassword=true`.

But the repository's own authentication design requires the state to be enforced centrally across:

- direct page navigation,
- internal Blazor navigation,
- protected endpoints,
- business authorization.

ADMIN/SYSTEM_ADMIN must not bypass forced change.

---

## 2.13 Blazor revalidation alone is not enough

Current Microsoft .NET 10 behavior:

```text
RevalidatingServerAuthenticationStateProvider returning false
    -> invalidates circuit auth state
    -> DOES NOT invalidate the underlying authentication cookie
```

A full navigation can recreate the user from the stale cookie.

Therefore implementation requires:

1. cookie principal validation, AND
2. Blazor circuit revalidation.

One without the other is incomplete.

---

## 2.14 CurrentUserService can resurrect stale identity

Current `CurrentUserService` prefers:

```text
HttpContext.User
```

before:

```text
AuthenticationStateProvider
```

In an Interactive Server circuit, that can expose an old authenticated principal after the revalidating provider has invalidated the circuit authentication state.

Must be fixed.

---

## 2.15 AccessRightService circuit cache can retain stale permissions

Current `AccessRightService` caches effective menu permissions in a scoped field:

```text
_cache
```

In Interactive Server, that scoped instance can live for the entire circuit.

Role-menu permission changes do NOT necessarily modify any cookie claim.

Therefore:

```text
identity validation succeeds
but permission cache remains old
```

unless it is explicitly refreshed.

The circuit revalidation path must call:

```text
IAccessRightService.RefreshPermissionsAsync()
```

after each successful periodic security validation.

This was missing from v2.

---

## 2.16 NavMenu loads authorized menu items once

Current `NavMenu` loads sidebar items in `OnInitializedAsync()`.

It does not rebuild the sidebar when permission cache changes.

Core/service authorization will still be authoritative, but UI can remain visually stale.

After permission-cache refresh, the menu must be refreshed at least on the next navigation, or through a small security-context-change notification.

Prefer the simplest solution:

```text
OnLocationChanged
    -> reload authorized sidebar
    -> StateHasChanged
```

Do not introduce a large event bus.

---

## 2.17 Role deactivation must invalidate authentication

Current ADMIN bypass is claim based:

```text
UserLevel == ADMIN / SYSTEM_ADMIN
```

If the Role row is later deactivated, the old claim could still bypass authorization unless revalidation verifies:

```text
user.userlevel corresponds to an ACTIVE Role in the current CompanyCode
```

The shared security-state validator must validate active role existence.

---

## 2.18 Forgot Password is currently fake

Current implementation:

```csharp
await Task.Delay(350);
Submitted = true;
```

No reset request is created.

The new implementation will use a real **admin-assisted password reset request**.

No email reset token is added in this plan.

---

## 2.19 Antiforgery infrastructure exists but auth endpoints opt out

Current `Program.cs` already has:

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
```

Current AccountEndpoints uses:

```text
.DisableAntiforgery()
```

for login/change-password/logout POSTs.

Raw HTML POST forms can render:

```razor
<AntiforgeryToken />
```

Remove the opt-outs.

---

## 2.20 BCrypt 72-byte input limit is not enforced

BCrypt input is limited to 72 bytes.

Current password policy checks character length but not UTF-8 byte count.

Long ASCII or multibyte Unicode input must fail cleanly before BCrypt rather than creating an exception/500 path.

---

# 3. Scope Decisions

## In scope

- User Accounts list/create/edit/view.
- admin password reset.
- force-change.
- self password change.
- role assignment validation.
- SYSTEM_ADMIN hierarchy.
- built-in role hardening required to preserve hierarchy.
- strict legacy role synchronization.
- password writer concurrency.
- soft-deleted account correctness.
- BCrypt input safety.
- forced-change authorization.
- cookie security revalidation.
- Blazor circuit security revalidation.
- permission-cache refresh.
- CurrentUserService stale-principal fix.
- forgot-password admin request workflow.
- antiforgery.
- auth/recovery rate limiting.
- focused security audit.
- automated tests.

## Explicitly out of scope

- ASP.NET Identity migration.
- JWT as main Blazor authentication.
- MFA.
- OTP.
- email reset links.
- SMS.
- SSO.
- password history.
- periodic forced expiry.
- true multi-role admin redesign.
- branch/location assignment redesign.
- restoring soft-deleted accounts.
- changing BCrypt algorithm.
- unrelated ERP transaction/costing changes.
- delegated Helpdesk password-reset permissions.

---

# 4. Do NOT Add RESET_PASSWORD / FORCE_CHANGE_PASSWORD Permissions Yet

Previous plan versions proposed new password permission codes.

Do **not** add them in this task.

Current service security already requires:

```text
ADMIN or SYSTEM_ADMIN
```

and those roles have ADMIN bypass in `AccessRightService`.

Adding permission rows without redesigning delegated administration would create complexity without actually restricting these roles.

A future Helpdesk/Security Admin plan can add granular password-reset authority.

---

# 5. Non-Negotiable Security Rules

## 5.1 Password secrecy

- store BCrypt hash only.
- no plaintext password persistence.
- no hash sent to UI.
- admin list query must not load hash.
- password/hash never logged.
- password/hash never placed in audit detail.

## 5.2 Company isolation

Every user mutation must identify target using:

```text
uid + authenticated CompanyCode
```

Never trust CompanyCode from posted UI.

## 5.3 Privilege hierarchy

```text
SYSTEM_ADMIN > ADMIN > other roles
```

Mandatory:

- ADMIN cannot create a SYSTEM_ADMIN user.
- ADMIN cannot assign SYSTEM_ADMIN.
- ADMIN cannot mutate SYSTEM_ADMIN target.
- ADMIN cannot create SYSTEM_ADMIN role metadata.
- ADMIN cannot deactivate SYSTEM_ADMIN role.
- built-in ADMIN/SYSTEM_ADMIN roles cannot be deleted.
- built-in ADMIN/SYSTEM_ADMIN roles cannot be deactivated.
- only SYSTEM_ADMIN may modify SYSTEM_ADMIN role descriptive metadata.

## 5.4 Self-protection

Through User Accounts:

- cannot delete self.
- cannot deactivate self.
- cannot change own role.
- cannot admin-reset own password.
- cannot toggle own force-change.

Self password change uses `/change-password`.

## 5.5 Soft-deleted users

- cannot login.
- cannot edit/reset/force-change.
- remain reserved under existing `(id, CompanyCode)` unique index.
- no restore feature added here.

## 5.6 Forced change

While change is required:

Allowed:

```text
/change-password
POST /account/change-password
POST /account/logout
/login
required framework/static transport
```

Normal ERP access denied.

ADMIN/SYSTEM_ADMIN bypass must not override this.

## 5.7 Current authoritative role

At login/revalidation:

```text
userlevel must be nonblank
AND matching Role must exist
AND Role.CompanyCode == user's CompanyCode
AND Role.IsActive == true
```

Otherwise authentication fails.

## 5.8 Password concurrency

No silent last-write-wins for:

- self change,
- admin reset,
- force-change toggle.

## 5.9 Stale-session window

Security state changes must take effect in Interactive Server and cookie requests within configured interval.

Target baseline:

```text
5 minutes
```

No claim of instant push revocation.

---

# 6. Phase 0 — Baseline

Before editing, re-open CURRENT branch files.

At minimum:

```text
ErpWeb.UI/Admin/AdminUser.razor
ErpWeb.UI/Admin/AdminUser.razor.cs
ErpWeb.UI/Admin/AdminRole.razor
ErpWeb.UI/Admin/AdminRole.razor.cs

ErpWeb.Core/Security/IUserAdminService.cs
ErpWeb.Core/Security/UserAdminService.cs
ErpWeb.Core/Security/RoleAdminService.cs
ErpWeb.Core/Services/AuthService.cs
ErpWeb.Core/Services/CurrentUserService.cs
ErpWeb.Core/Menus/UserRoleSyncService.cs
ErpWeb.Core/Menus/IUserRoleSyncService.cs
ErpWeb.Core/Menus/AccessRightService.cs
ErpWeb.Core/Menus/NavigationService.cs

ErpWeb.Library/Security/PasswordHasher.cs
ErpWeb.Core/Security/PasswordPolicy.cs
ErpWeb.Core/Security/PasswordPolicyOptions.cs
ErpWeb.Core/Security/PasswordPolicyOptionsValidator.cs

ErpWeb.Model/Entities/UserLogin.cs
ErpWeb.Model/Configurations/UserLoginConfiguration.cs
ErpWeb.Model/Repositories/UserLoginRepository.cs
ErpWeb.Model/Repositories/PasswordAdminUserRow.cs
ErpWeb.Model/Data/AppDbContext.cs

ErpWeb/Authentication/AccountEndpoints.cs
ErpWeb/Authentication/CookieSignInService.cs
ErpWeb/Program.cs

ErpWeb.UI/Components/Pages/PageBase.cs
ErpWeb.UI/Components/Pages/Login.razor
ErpWeb.UI/Components/Pages/ChangePassword.razor
ErpWeb.UI/Components/Pages/ForgotPassword.razor
ErpWeb.UI/Components/Layout/MainLayout.razor.cs
ErpWeb.UI/Components/Layout/NavMenu.razor
ErpWeb.UI/Components/Layout/NavMenu.razor.cs
ErpWeb.UI/Components/Layout/Header/Header.razor
```

Existing security tests are under:

```text
ErpWeb.Tests/Admin/Master/
```

including:

```text
PasswordAdminServiceTests.cs
PasswordPolicyTests.cs
UserLoginRepositoryPasswordAdminTests.cs
UserRoleSyncServiceTests.cs
CurrentUserServiceTests.cs
AccessRightServiceTests.cs
CompanyServiceBootstrapTests.cs
```

Do NOT create duplicate tests elsewhere unless they test a genuinely new concern.

Baseline commands:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Admin&Category!=SqlServer"
```

Record existing failures.

---

# 7. Phase 1 — Password-Free User Accounts Projection and Form Models

## 7.1 Reuse existing PasswordAdminUserRow

Extend existing password-free projection to carry what User Accounts grid needs:

```text
Uid
LoginId
Name
Email
MobileNo
Active
RoleCode
ChangePass
CanViewPrice
Created
CreatedBy
Updated
UpdatedBy
```

Do not include:

```text
password
passwordHash
CompanyCode form field
BranchCode form field
LocationCode form field
ImagePath form field
```

If the class name becomes misleading after extension, rename it once to a neutral security DTO, but do not keep two near-identical row DTOs.

## 7.2 Remove ListForAdminAsync returning UserLogin

Replace grid listing with direct SQL projection:

```csharp
.Select(...)
```

into the password-free row.

Do not:

```text
load UserLogin including password
then set password=""
```

## 7.3 Dedicated create/edit/reset models

Create focused Core security models.

### Create

```text
LoginId
Name
TemporaryPassword
ConfirmPassword
Email
MobileNo
Active
RoleCode
CanViewPrice
```

Server derives:

```text
CompanyCode
BranchCode
LocationCode
Created/UserID
changepass=true
```

### Edit

```text
Uid
Name
Email
MobileNo
Active
RoleCode
CanViewPrice
```

No password.

No company/branch/location/image/audit mutation fields.

### Reset

```text
Uid
LoginId display only
NewPassword
ConfirmPassword
```

## 7.4 Repository profile update allow-list

Update only:

```text
name
email
mobileno
active
userlevel
CanViewPrice
Updated
UpdatedUID
```

Never through profile edit:

```text
password
id
CompanyCode
BranchCode
LocationCode
ImagePath
Created
UserID
changepass
```

## 7.5 Role combo

No free text.

Use strict active-role lookup.

Normal ADMIN must not receive SYSTEM_ADMIN as assignable option.

## 7.6 New User confirm password

Current create UI lacks confirmation.

Add it.

### Gate

- edit works without password.
- hash unchanged.
- CanViewPrice preserved.
- branch/location/image preserved.
- list DTO has no password property.
- no query path exposes password to grid.

---

# 8. Phase 2 — User Privilege Boundary + Atomic Target Validation

Simple "check target then update later" is not enough for SYSTEM_ADMIN security because the target may change between check and mutation.

## 8.1 Create/edit requested role

Role must be:

```text
existing
same CompanyCode
active
```

Normalize role code to existing repository convention.

ADMIN requesting SYSTEM_ADMIN:

```text
DENY
```

## 8.2 SYSTEM_ADMIN target

For all security mutations:

```text
edit
deactivate
delete
reset
force-change
```

ADMIN may not target a live SYSTEM_ADMIN.

## 8.3 Atomic authorization + mutation

For single-row profile/security update:

Use one of:

- transaction with appropriate locking/isolation,
- guarded SQL UPDATE predicate,
- another repository pattern that guarantees the target cannot become SYSTEM_ADMIN between authorization and write.

Do not rely on:

```text
SELECT role
await
UPDATE without role guard
```

for the privilege boundary.

## 8.4 Multi-delete all-or-none

Required:

```text
validate exact selected uid set
same company
none deleted
none self
none protected SYSTEM_ADMIN
```

and perform deletion atomically.

If any selected row is invalid/protected:

```text
delete NONE
```

Do not partially delete other selected users.

Use transaction/locking rather than a precheck followed by an unguarded bulk update.

## 8.5 Self-security

Reject:

- self deactivate,
- self role change,
- self delete,
- self admin reset,
- self force-change.

### Gate

Add service + repository tests for all target races/guards.

---

# 9. Phase 3 — Harden Built-in Role Administration

This phase is mandatory because User Accounts privilege security depends on the Role catalog.

## 9.1 AddRoleAsync

Normal ADMIN:

```text
cannot create RoleCode SYSTEM_ADMIN
```

SYSTEM_ADMIN may create it only when current-company design legitimately requires it and duplicate does not exist.

Do not auto-create SYSTEM_ADMIN as part of user assignment.

## 9.2 UpdateRoleAsync

Built-in roles:

```text
ADMIN
SYSTEM_ADMIN
```

must not be deactivated.

For SYSTEM_ADMIN row:

```text
normal ADMIN cannot modify it
```

SYSTEM_ADMIN may edit descriptive name if needed, but must not deactivate it through this UI.

## 9.3 Delete

Preserve existing no-delete protection for:

```text
ADMIN
SYSTEM_ADMIN
```

## 9.4 Role list UX

Normal ADMIN may:

- hide SYSTEM_ADMIN from editable list,
- or show as protected/read-only.

Server protection remains authoritative.

### Gate

- ADMIN cannot create SYSTEM_ADMIN role.
- ADMIN cannot deactivate/edit SYSTEM_ADMIN.
- ADMIN role cannot be deactivated.
- SYSTEM_ADMIN role cannot be deactivated.
- built-in delete protection remains.

---

# 10. Phase 4 — Strict UserRoleSync

## 10.1 No role creation during login

Delete the current:

```csharp
new Role { CreatedBy = "LOGIN" }
```

behavior.

## 10.2 Return explicit result

Change role sync to report:

```text
Success
BlankRole
RoleMissing
RoleInactive
WrongCompany
ConcurrencyFailure
```

Expected configuration errors fail authentication cleanly.

## 10.3 Resolve only active company role

Required:

```text
CompanyCode == user.CompanyCode
RoleCode == userlevel
IsActive == true
```

## 10.4 Current legacy mapping policy

Within current company:

- remove stale mappings,
- ensure desired mapping exists exactly once,
- do not touch mappings belonging to another company.

Do not implement true multi-role redesign.

## 10.5 Concurrency

Simultaneous logins must not throw duplicate mapping exceptions.

Unique-key race where desired final mapping exists:

```text
treat as success
```

Do not swallow unrelated DB failures.

### Gate

Update existing:

`UserRoleSyncServiceTests.cs`

The old test:

```text
Reconcile_creates_role_and_mapping_from_userlevel
```

must be changed because role auto-creation is now invalid.

---

# 11. Phase 5 — BCrypt 72-Byte Safety

## 11.1 Central byte rule

Use UTF-8 byte count:

```csharp
Encoding.UTF8.GetByteCount(password)
```

Maximum supported by BCrypt:

```text
72 bytes
```

Do not silently truncate.

## 11.2 Password policy option

Add:

```text
MaximumUtf8Bytes = 72
```

Startup validation:

```text
1..72
```

Keep current mandatory policy:

- min >= 8 chars,
- ASCII letter,
- ASCII digit,
- username mismatch.

## 11.3 Safe PasswordHasher.Verify

Attacker-controlled login password must never produce a 500 because of:

- >72 bytes,
- malformed stored hash,
- BCrypt credential-format error.

Return false for invalid credential/hash conditions.

Do not broadly swallow infrastructure exceptions.

### Gate

Extend existing:

`PasswordPolicyTests.cs`

and password hasher tests if needed.

Test:

- 72-byte boundary.
- >72 ASCII.
- multibyte >72.
- malformed hash.
- existing normal BCrypt hashes.

---

# 12. Phase 6 — Self Password Change with Compare-and-Set

## 12.1 Live company-scoped target

Load using:

```text
uid + authenticated CompanyCode
not deleted
active=true
```

Do not use current unscoped `GetByUidAsync(uid)`.

## 12.2 Same password

After verifying current password:

```csharp
PasswordHasher.Verify(newPassword, currentHash)
```

must be false.

## 12.3 Compare-and-set

Update predicate at minimum:

```text
uid
CompanyCode
active=true
not deleted
password == expected old hash
changepass == expected old flag
```

If zero rows:

```text
ConcurrencyConflict
```

This prevents self-change from silently clearing a newly-set forced-change flag or overwriting reset.

## 12.4 Result categories

Return safe codes/messages:

```text
Success
IdentityInvalid
CurrentPasswordIncorrect
SamePassword
PolicyFailure
ConcurrencyConflict
```

### Gate

Race tests:

- self vs self.
- self vs reset.
- self vs force-change.
- only one valid outcome commits.

For SQL Server semantics add a real SQL Server concurrency test carrying:

```text
Category=Admin
Category=SqlServer
```

following existing test style.

---

# 13. Phase 7 — Admin Reset and Force-Change Concurrency

Rename admin method if practical:

```text
ChangePasswordAsync(uid,...)
```

to:

```text
ResetPasswordAsync(...)
```

to avoid confusion with self-change.

## 13.1 Target guards

At mutation time:

- same company.
- not deleted.
- not self.
- SYSTEM_ADMIN protected from ADMIN.
- target role state rechecked.

## 13.2 Reset compare-and-set

Repository may internally read current hash, but hash must remain inside Model/Core.

Reset UPDATE must guard on expected security state so a concurrent password writer is not silently overwritten.

Success:

```text
new hash
changepass=true
Updated/UpdatedUID
```

## 13.3 Force-change compare-and-set

Important race:

```text
Admin A opens force-change state
Admin B resets password -> changepass=true + NEW HASH
Admin A clears flag
```

A predicate checking only old `changepass` is insufficient.

Force-change mutation must also protect against password/security state changing between read and update.

Repository can internally read:

```text
password hash
changepass
Updated
```

then issue guarded update.

Do not expose hash to UI/service DTO.

### Gate

- reset/reset race.
- reset/self race.
- reset/force-clear race.
- force-change never changes hash.
- stale force-clear cannot defeat concurrent reset.

---

# 14. Phase 8 — Login Final-State Recheck

Current expensive password verification creates a race window.

New login:

```text
normalize company/user
validate company
read user
verify active/not deleted
safe BCrypt verify
strict role sync
FINAL live user read
```

Final read must confirm:

```text
same uid/company
active
not deleted
password hash still equals verified hash
userlevel still valid/active
```

Claims are created from FINAL live row.

If password changed while login was running:

```text
generic login failure
```

No stale cookie issued.

---

# 15. Phase 9 — Forced-Password-Change Authorization Policy

## 15.1 ErpAccess policy

Configure:

```text
authenticated
AND change_password claim == "false"
```

## 15.2 PageBase

Normal ERP pages:

```csharp
[Authorize(Policy = "ErpAccess")]
```

instead of plain `[Authorize]`.

## 15.3 ChangePassword exception

Current ChangePassword inherits PageBase.

Refactor it so it uses:

```text
authenticated only
```

not ErpAccess.

It must not become anonymous.

## 15.4 Route coverage test

Do not rely only on manual grep.

Add a test that verifies all known routable UI components are either:

- explicitly anonymous allowed page,
- authenticated ChangePassword exception,
- or covered by ErpAccess/PageBase.

The explicit anonymous list must be small:

```text
/login
/forgot-password
error/unauthorized only as intentionally designed
```

## 15.5 AccessRightService defense

Before ADMIN/SYSTEM_ADMIN bypass:

```text
if MustChangePassword
    deny normal ERP menu/action permissions
```

## 15.6 HTTP endpoints

Any protected non-component endpoint must also deny normal ERP action while forced change is active.

Do not block required:

```text
/_blazor
/_framework
/_content
static assets
```

## 15.7 Keep MainLayout redirect only for UX

It is not the security boundary.

---

# 16. Phase 10 — Shared DB Security-State Validator

Create one reusable validator, e.g.:

```text
IAuthSecurityStateValidator
AuthSecurityStateValidator
```

It accepts a ClaimsPrincipal.

It must NOT depend on `ICurrentUserService`.

## 16.1 Validate user state

Reject if:

- malformed/missing subject/company claim,
- user missing,
- soft-deleted,
- inactive,
- CompanyCode mismatch,
- `userlevel` mismatch,
- `changepass` mismatch,
- BranchCode mismatch,
- LocationCode mismatch,
- CanViewPrice mismatch.

## 16.2 Validate authoritative role

Also require:

```text
matching Role row
same company
RoleCode == userlevel
Role.IsActive == true
```

This is essential for ADMIN/SYSTEM_ADMIN because bypass is claim-based.

## 16.3 Company state

Preserve existing login compatibility:

```text
Company row absent -> legacy allow
Company row present + inactive -> reject
```

## 16.4 Do not compare non-security profile fields

Do not logout merely because:

- name,
- email,
- mobile

changed.

---

# 17. Phase 11 — Cookie Principal Revalidation

A circuit provider alone is not enough.

## 17.1 Cookie events

Add custom:

```text
CookieAuthenticationEvents
```

using shared validator.

Register via:

```text
options.EventsType = ...
```

## 17.2 Revalidation interval

Configuration:

```json
"Authentication": {
  "SecurityRevalidationMinutes": 5
}
```

Validate > 0.

## 17.3 Avoid DB query every request

Persist last validation timestamp in authentication ticket properties.

If interval not reached:

```text
skip DB validation
```

When reached:

```text
validate
valid -> refresh timestamp
invalid -> RejectPrincipal
```

Renew only as required to persist the timestamp.

Do not intentionally change existing sliding-expiration semantics.

Do not silently extend expiration solely because password changed.

## 17.4 Full refresh test

After DB security invalidation:

```text
old circuit invalid
full page reload
old cookie must not permanently restore access
```

---

# 18. Phase 12 — Blazor Circuit Revalidation + Permission Cache Refresh

Add:

```text
ErpRevalidatingAuthenticationStateProvider
```

using same validator and interval.

On invalid:

```text
return false
```

On valid:

```text
await AccessRightService.RefreshPermissionsAsync()
return true
```

This is mandatory.

Why:

```text
RoleMenuPermission can change without changing any auth claim
```

Without cache refresh the circuit can keep old rights.

## 18.1 CurrentUserService

New resolution principle:

```text
Interactive circuit:
    AuthenticationStateProvider is authoritative

Pure HTTP scope where provider is not initialized:
    fall back to HttpContext.User
```

Critical:

If provider explicitly returns unauthenticated after revalidation:

```text
DO NOT fall back to old authenticated HttpContext.User
```

Catch only the framework-specific provider-not-initialized case needed for HTTP scope.

Do not broad-catch and reuse stale identity.

## 18.2 NavMenu stale visuals

After permission cache refresh, authorized business actions are safe because services use refreshed permissions.

For UI correctness, change NavMenu location handler to reload sidebar:

```text
OnLocationChanged
    -> NavigationService.GetSidebarAsync()
    -> remap items
    -> StateHasChanged
```

This keeps menu visibility aligned after the next navigation without adding a large event framework.

### Gate

Extend:

- `CurrentUserServiceTests.cs`
- `AccessRightServiceTests.cs`

Add circuit provider tests.

Test role-menu permission change:

```text
permission allowed
cache loaded
DB grant removed
periodic revalidation succeeds
permission cache refreshed
next CanAsync -> denied
```

---

# 19. Phase 13 — Preserve Cookie Properties on Self Password Change

Current:

```text
Remember Me login
-> self password change
-> SignInAsync(user) defaults rememberMe=false
```

Fix.

Before mutation capture current authentication properties.

After successful password change:

- issue new principal,
- preserve `IsPersistent`,
- preserve current `ExpiresUtc` for this reissue,
- preserve appropriate `AllowRefresh`,
- reset security validation timestamp.

Do not introduce a new absolute-expiry policy in this task.

Future normal cookie sliding behavior stays as currently configured.

---

# 20. Phase 14 — Real Forgot Password Request

Use admin-assisted recovery.

## 20.1 Use real HTTP POST

Add:

```text
POST /account/forgot-password
```

Forgot page uses normal form and:

```razor
<AntiforgeryToken />
```

Do not use an Interactive Server callback as the primary submit path.

Redirect to:

```text
/forgot-password?submitted=1
```

using PathBase-safe redirect helper.

## 20.2 PasswordResetRequest table

Recommended:

```text
PasswordResetRequestId bigint identity
UserUid int NOT NULL
CompanyCode nvarchar(5) NOT NULL
Status nvarchar(20) NOT NULL
RequestedAtUtc datetime2 NOT NULL
ResolvedAtUtc datetime2 NULL
ResolvedBy nvarchar(10) NULL
```

Statuses:

```text
PENDING
COMPLETED
CANCELLED
```

No password/hash/token.

## 20.3 Race-safe deduplication

Enforce one pending request per user at SQL level where practical:

```text
unique filtered index on UserUid WHERE Status='PENDING'
```

Service catches expected duplicate race and returns normal generic response.

## 20.4 Enumeration safety

Visible response always identical.

Suggested:

> If the account details match an active account, a password-reset request is available to your company administrator.

Invalid/inactive/deleted identity creates no target request.

---

# 21. Phase 15 — Admin Reset Request Queue

Integrate compactly into User Accounts.

Show current-company pending requests:

- Login ID
- User Name
- Requested At

Actions:

- Reset Password
- Cancel

## 21.1 Atomic reset completion

If reset originates from pending request:

```text
guarded password reset
changepass=true
request COMPLETED
security audit
```

must occur in one DB transaction.

If reset fails/conflicts:

```text
request stays PENDING
```

Normal manual admin reset should complete existing pending request(s) for same user in same transaction where practical.

SYSTEM_ADMIN/self/company/deleted protections still apply.

---

# 22. Phase 16 — Antiforgery

Current middleware already exists.

Do not duplicate it.

## Login

Add:

```razor
<AntiforgeryToken />
```

Remove `.DisableAntiforgery()`.

## Change Password

Same.

## Forgot Password

Same.

## Logout

Remove normal GET logout route.

Replace current anchors:

```text
Header Sign out
ChangePassword Sign out
```

with POST forms/buttons containing antiforgery token.

Preserve BaseUri/AppBasePath-safe actions.

---

# 23. Phase 17 — Rate Limiting

Apply only to real HTTP auth/recovery endpoints.

Use built-in ASP.NET Core rate limiter.

Suggested configurable starting baseline:

```text
/account/login:
    60/minute/IP

/account/forgot-password:
    10/minute/IP
```

Why not an extremely low login limit:

```text
SME users may share a NAT/public IP
```

Do not create permanent account lockout.

Do not trust forwarded IP headers unless current reverse-proxy forwarded-header handling is explicitly configured.

Rejected response must remain generic.

---

# 24. Phase 18 — Security Audit

Final CURRENT-repo search shows domain-specific audit tables but no verified general security audit store.

Add a narrow append-only security audit table.

Recommended:

```text
SecurityAuditEventId
CompanyCode
ActorUid
ActorUserId
TargetUid
EventType
Outcome
OldValue
NewValue
OccurredAtUtc
Details
```

Never store password/hash/token.

Audit:

- USER_CREATED
- USER_PROFILE_UPDATED
- USER_ACTIVATED
- USER_DEACTIVATED
- USER_ROLE_CHANGED
- USER_SOFT_DELETED
- PASSWORD_RESET_ADMIN
- PASSWORD_CHANGE_SELF
- FORCE_PASSWORD_CHANGE_SET
- FORCE_PASSWORD_CHANGE_CLEARED
- PASSWORD_RECOVERY_REQUESTED
- PASSWORD_RECOVERY_COMPLETED
- PASSWORD_RECOVERY_CANCELLED

For DB security mutations, write the audit row in the same transaction where practical.

Do not DB-audit every failed login; use structured logger to avoid attacker-driven write flooding.

---

# 25. Database Impact

## Do NOT modify userlogin schema

No:

- rowversion,
- security stamp,
- alternate password column,
- Identity table migration.

Password concurrency uses guarded compare-and-set.

## New tables

Expected:

```text
PasswordResetRequest
SecurityAuditEvent
```

Use one focused idempotent script:

```text
scripts/security-password-hardening.sql
```

Safe to rerun.

No unrelated schema changes.

---

# 26. Test Strategy — Use Existing Test Structure

Do not create random root-level test files.

Use:

```text
ErpWeb.Tests/Admin/Master/
```

for normal security tests.

Extend existing:

```text
PasswordAdminServiceTests.cs
PasswordPolicyTests.cs
UserLoginRepositoryPasswordAdminTests.cs
UserRoleSyncServiceTests.cs
CurrentUserServiceTests.cs
AccessRightServiceTests.cs
```

Add only genuinely new concerns, e.g.:

```text
AuthenticationRevalidationTests.cs
PasswordRecoveryTests.cs
RolePrivilegeBoundaryTests.cs
```

For real SQL Server concurrency:

```text
Category=Admin
Category=SqlServer
```

follow existing `*SqlServerConcurrencyTests` pattern.

---

# 27. Required Test Commands

After each focused security phase:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Admin&Category!=SqlServer"
```

Before final completion:

```powershell
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category!=SqlServer"
```

Then run security SQL Server concurrency tests if configured:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "Category=Admin&Category=SqlServer"
```

Finally run the full suite if the environment supports all configured integrations:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

Do not hide skipped/unavailable SQL Server validation.

---

# 28. Mandatory Test Matrix

## User list/edit

- grid projection has no password/hash.
- list query does not materialize hash.
- edit does not require password.
- edit preserves password.
- edit preserves branch/location/image.
- CanViewPrice preserved.
- over-posted company/branch/location ignored.

## User hierarchy

- ADMIN cannot assign SYSTEM_ADMIN.
- ADMIN cannot create SYSTEM_ADMIN user.
- ADMIN cannot edit/deactivate/delete/reset/force-change SYSTEM_ADMIN.
- self delete/deactivate/role/reset/force denied.
- race cannot change normal target to SYSTEM_ADMIN between check and write.

## Role catalog

- ADMIN cannot create SYSTEM_ADMIN role.
- ADMIN cannot mutate SYSTEM_ADMIN role.
- ADMIN role cannot be deactivated.
- SYSTEM_ADMIN role cannot be deactivated.
- built-ins cannot be deleted.

## Soft delete

- deleted target invisible to mutations.
- deleted login ID remains reserved.
- duplicate create returns friendly conflict.
- direct uid reset fails.

## Role sync

- valid active role maps.
- missing role fails.
- inactive role fails.
- blank userlevel fails.
- no role created.
- stale current-company mapping removed.
- concurrency safe.

## BCrypt

- 72 bytes.
- >72 bytes.
- multibyte overflow.
- malformed hash.
- normal hashes.

## Login race

- reset during login -> old password does not receive cookie.
- deactivate during login -> no cookie.
- role deactivation during login -> no cookie.

## Password concurrency

- self/self.
- self/reset.
- reset/reset.
- reset/force-clear.
- stale writer gets conflict.

## Forced change

- direct route blocked.
- internal route blocked.
- ADMIN blocked.
- SYSTEM_ADMIN blocked.
- endpoint blocked.
- password page/logout/framework work.

## Revalidation

- inactive user.
- deleted user.
- changepass change.
- userlevel change.
- role deactivated.
- company inactive.
- branch/location/CanViewPrice mismatch.
- missing company legacy behavior.
- stale cookie rejected.
- circuit invalidated.
- CurrentUserService does not resurrect old HttpContext identity.

## Permission-cache refresh

- role permission cached.
- DB grant changes.
- next periodic validation refreshes cache.
- next service CanAsync reflects new DB state.

## Navigation

- after cache refresh + next navigation, sidebar reload reflects permission.

## Remember Me

- persistence preserved after self password change.
- current ExpiresUtc preserved during reissue.
- no new lifetime invented.

## Recovery

- valid creates pending.
- invalid same response.
- inactive/deleted same response.
- duplicate race -> one pending.
- company isolation.
- reset + request completion atomic.
- failed reset leaves request pending.
- SYSTEM_ADMIN protection cannot be bypassed through queue.

## Antiforgery

- login missing token rejected.
- change missing token rejected.
- logout missing token rejected.
- forgot missing token rejected.
- valid requests succeed.

## AppBasePath

Test all auth operations at:

```text
/
and
/erpweb (or configured base path)
```

---

# 29. Acceptance Criteria

All mandatory items must pass.

1. User Accounts no longer binds UserLogin entity.
2. User grid never receives password/hash.
3. Edit User works without password.
4. Profile edit never changes password.
5. CanViewPrice bug fixed.
6. Hidden over-posting of Branch/Location/Image prevented.
7. ADMIN cannot become SYSTEM_ADMIN.
8. ADMIN cannot target SYSTEM_ADMIN.
9. Role Administration cannot undermine SYSTEM_ADMIN boundary.
10. Built-in ADMIN/SYSTEM_ADMIN roles cannot be deactivated.
11. Login cannot create roles.
12. Invalid/inactive authoritative role cannot authenticate.
13. Deleted users cannot be reset/force-changed.
14. Deleted login IDs aren't falsely reusable.
15. BCrypt oversized input never causes 500.
16. Same-password forced-change bypass is closed.
17. Password writers are no longer silent last-write-wins.
18. Login final-state race is guarded.
19. Forced-change covers Interactive Server navigation and endpoints.
20. ADMIN/SYSTEM_ADMIN bypass cannot bypass forced change.
21. Cookie and circuit both revalidate.
22. CurrentUserService cannot resurrect invalidated circuit identity.
23. Active role is checked during revalidation.
24. Circuit permission cache refreshes periodically.
25. Changed role permissions take effect within the configured interval.
26. Nav menu corrects itself on next navigation.
27. Remember Me behavior is preserved on self-change.
28. Forgot Password creates real pending request.
29. Recovery remains enumeration-safe.
30. Reset request completion is transactional.
31. Login/change/logout/recovery POSTs use antiforgery.
32. GET logout is removed from normal workflow.
33. Auth/recovery endpoints have bounded rate limits.
34. Security audit contains no credentials.
35. userlogin schema unchanged.
36. no unrelated ERP logic changed.
37. Admin non-SQL tests pass.
38. fast full suite passes.
39. SQL Server security concurrency tests pass when environment available.
40. IIS/AppBasePath auth flow is manually verified.

---

# 30. Things DeepSeek V4 Flash MUST NOT Do

- do not migrate to ASP.NET Identity.
- do not add Identity tables.
- do not change BCrypt.
- do not truncate long passwords and accept them.
- do not remove UserLogin.password Required.
- do not bind UserLogin to User Accounts.
- do not create duplicate password-free row DTO unnecessarily.
- do not expose Branch/Location editing.
- do not create roles during login.
- do not implement multi-role redesign.
- do not add RESET_PASSWORD/FORCE_CHANGE_PASSWORD permissions in this task.
- do not trust client CompanyCode.
- do not separate SYSTEM_ADMIN check and mutation with an unsafe TOCTOU window.
- do not allow partial multi-delete when one row is unauthorized.
- do not let ADMIN modify SYSTEM_ADMIN Role catalog.
- do not use MainLayout as sole force-change security.
- do not implement circuit revalidation without cookie revalidation.
- do not revalidate identity but forget AccessRightService cache.
- do not let CurrentUserService fall back to stale authenticated HttpContext after provider invalidation.
- do not query the DB on every component render.
- do not add permanent account lockout.
- do not implement email reset.
- do not implement SSO.
- do not store reset password/hash.
- do not modify inventory/costing/production/sales/procurement rules.
- do not suppress failing security tests.

---

# 31. DeepSeek V4 Flash Execution Method

Work one phase at a time.

For every phase:

```text
1. Re-open CURRENT files.
2. Re-read related EXISTING tests.
3. State exact minimal changes.
4. Implement.
5. Build.
6. Run Category=Admin focused tests.
7. Inspect diff.
8. Fix only phase-related issues.
9. Verify phase gate.
10. Continue.
```

Do not feed DeepSeek the whole plan and ask it to make one giant patch.

Recommended checkpoint groups:

```text
Checkpoint A:
    Phases 1-5
    DTO/edit/role boundary/role sync/BCrypt

Checkpoint B:
    Phases 6-8
    password concurrency + login race

Checkpoint C:
    Phases 9-13
    forced-change + cookie/circuit revalidation + permission cache

Checkpoint D:
    Phases 14-18
    recovery + antiforgery + rate limit + audit
```

At every checkpoint ask:

```text
Can ADMIN become SYSTEM_ADMIN?
Can Role Admin undermine that restriction?
Can another company affect the target?
Can a deleted user be targeted?
Can two writers overwrite each other?
Can an old cookie regain access?
Can a circuit still use old permissions?
Can CurrentUserService resurrect stale identity?
Can BCrypt input throw?
Can password/hash leak?
```

---

# 32. Final Implementation Trace

Before completion trace actual code:

```text
LOGIN
-> antiforgery + rate limit
-> company check
-> user lookup
-> safe BCrypt
-> strict role sync
-> final live-state recheck
-> claims
-> cookie

USER EDIT
-> password-free row
-> narrow edit model
-> atomic target hierarchy validation
-> allow-list update
-> audit

ROLE ADMIN
-> built-in role guard
-> SYSTEM_ADMIN guard

ADMIN RESET
-> same company
-> self/protected/deleted guards
-> policy
-> CAS password reset
-> changepass=true
-> request completion
-> audit

SELF CHANGE
-> current password
-> same-password rejection
-> policy
-> CAS update
-> cookie reissue preserving auth properties

FORCED CHANGE
-> ErpAccess route authorization
-> AccessRightService denial
-> protected endpoint denial
-> change page still available

REVALIDATION
-> shared DB validator
-> active authoritative role check
-> cookie validation
-> circuit validation
-> permission cache refresh
-> CurrentUserService provider authority
-> nav refresh on next navigation

FORGOT PASSWORD
-> HTTP POST
-> antiforgery
-> rate limit
-> generic response
-> race-safe pending request
-> User Accounts queue
```

Search final repository for unsafe remnants:

```text
ListForAdminAsync returning UserLogin
AdminUser selectedRow as UserLogin
AllowUserInput="true" for Group Level
Task.Delay(350) forgot-password implementation
DisableAntiforgery on auth form POSTs
GET logout anchors
new Role inside UserRoleSyncService
GetByUidAsync used for self password mutation without company
ResetPasswordAsync lacking deleted/protected/concurrency guard
SetChangePassAsync lacking deleted/protected/concurrency guard
CurrentUserService HttpContext-first behavior
AccessRightService cache never refreshed by circuit revalidation
RoleAdmin allowing SYSTEM_ADMIN creation/edit by normal ADMIN
RoleAdmin allowing built-in role deactivation
```

Every remaining occurrence must be intentionally justified.

---

# 33. Required Completion Report

DeepSeek must report:

```text
Repository head implemented against
Phase completion status
Files changed
SQL script added
Schema objects added
Existing tests updated
New tests added
Admin test result
Full fast-suite result
SQL Server concurrency result
IIS/AppBasePath manual verification
Cookie revalidation behavior
Circuit revalidation behavior
Permission-cache behavior
Known limitations
```

Known limitations must include:

```text
No MFA
No email reset token
No password history
No true immediate server-push revocation
No delegated helpdesk password-reset permission
No multi-role administration redesign
No deleted-user restore UI
```

---

# 34. Final Review Conclusion

The v2 plan was materially stronger than v1, but it still had important gaps.

This final strict re-review corrected:

1. reuse of existing `PasswordAdminUserRow` instead of adding a duplicate row DTO,
2. existing test locations and category-aware test commands,
3. atomic privilege-check/mutation requirements to close SYSTEM_ADMIN TOCTOU races,
4. all-or-none protected multi-delete,
5. built-in Role Administration hardening,
6. active-role validation during session revalidation,
7. periodic `AccessRightService` cache refresh for role-permission changes,
8. NavMenu refresh behavior after permission changes,
9. real SQL Server password concurrency testing,
10. precise cookie-expiration preservation without inventing a new absolute-expiry rule.

**Final status: APPROVED FOR IMPLEMENTATION, conditional on the build/test/runtime gates above.**

I am intentionally **not** labeling this 10/10 before implementation because the most framework-sensitive pieces must still prove themselves in the actual .NET 10 / Interactive Server / IIS `/erpweb` runtime.
