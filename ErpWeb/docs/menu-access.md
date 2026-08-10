# Menu & Access Control — Final Architecture & Implementation Plan

## 1. Purpose

This document defines the production-ready architecture for the ErpWeb menu navigation and authorization system.

The design replaces the legacy:

* `LevelGroup`
* `LevelRights`
* `vAccessRight`
* Bitwise `AccessRightEnum`

with:

* Named permissions
* Company-scoped roles
* Explicit user-role mappings
* XML → database menu synchronization
* Scoped permission caching
* Immutable navigation filtering
* Declarative page authorization
* Declarative action authorization
* Core/business-layer authorization

### Core security principle

> **Menu visibility is a UI convenience. Authorization is enforced independently at the page and business-operation layers.**

Hiding a menu item or button is never considered a security boundary.

---

# 2. Technology Stack

| Layer           | Technology                         |
| --------------- | ---------------------------------- |
| Host            | ASP.NET Core / Blazor Server       |
| UI              | ErpWeb.UI                          |
| Navigation      | Current application navigation UI  |
| Services        | `ErpWeb.Core.Menus`                |
| Data            | SQL Server + EF Core               |
| Menu definition | Version-controlled XML             |
| Runtime menu    | Database                           |
| Authorization   | Named permission codes             |
| Roles           | Company-scoped RBAC                |
| Authentication  | Existing authenticated user/claims |

The authorization architecture must not depend on DevExpress `DxTreeView`.

DevExpress can be used by the UI, but authorization remains UI-framework independent.

---

# 3. High-Level Architecture

```text
                    Menus/menus.xml
                           │
                           ▼
              MenuDefinitionService
                           │
                    Parse + Validate
                           │
                           ▼
                     MenuSyncService
                           │
                           ▼
                      dbo.Menu
                           │
              ┌────────────┴────────────┐
              │                         │
              ▼                         ▼
        MenuPermission             Permission
              │
              │
UserLogin ──► UserRoleMapping
              │
              ▼
             Role
              │
              ▼
       RoleMenuPermission
              │
              ▼
       AccessRightService
          Scoped Cache
              │
       ┌──────┼──────────────┐
       │      │              │
       ▼      ▼              ▼
Navigation  Page          Actions
Service     Authorization  / Core Services
       │      │              │
       ▼      ▼              ▼
    NavMenu  MenuAuthorize  PermissionAuthorize
                            +
                       Core CanAsync()
```

---

# 4. Authorization Layers

There are three independent authorization layers.

## 4.1 Navigation Authorization

Purpose:

> Show only menus that the user can access.

```text
Permission
    ↓
NavigationService
    ↓
Filtered menu tree
    ↓
NavMenu
```

This is purely for usability.

---

## 4.2 Page Authorization

Every protected page must independently verify `ACCESS`.

Example:

```razor
<MenuAuthorize MenuCode="@MenuCodes.InventoryDemo">
    ...
</MenuAuthorize>
```

Direct navigation to the URL must not bypass authorization.

Expected behaviour:

```text
No ACCESS
    ↓
/unauthorized
```

---

## 4.3 Action / Business Authorization

Actions such as:

* ADD
* EDIT
* DELETE
* POST
* PRINT
* ROLLBACK
* APPROVE
* RESET_PASSWORD
* FORCE_CHANGE_PASSWORD

must be independently authorized.

UI:

```razor
<PermissionAuthorize
    MenuCode="@MenuCodes.InventoryDemo"
    Permission="@PermissionCodes.Add">

    <button>Add</button>

</PermissionAuthorize>
```

But hiding the button is insufficient.

The actual action must re-check permission:

```text
UI
 ↓
PermissionAuthorize
 ↓
Action
 ↓
IAccessRightService.CanAsync()
 ↓
Core service
```

---

# 5. Permission Model

## 5.1 Named Permissions

Permissions are strings/codes rather than bitmasks.

Single source of truth:

```text
ErpWeb.Core/Menus/PermissionCodes.cs
```

Example:

```csharp
public static class PermissionCodes
{
    public const string Access = "ACCESS";
    public const string Add = "ADD";
    public const string Edit = "EDIT";
    public const string Delete = "DELETE";
    public const string Print = "PRINT";
    public const string Post = "POST";
    public const string Rollback = "ROLLBACK";
    public const string Approve = "APPROVE";
}
```

Additional permissions may be added as required:

```text
EXPORT
IMPORT
CANCEL
REOPEN
RESET_PASSWORD
FORCE_CHANGE_PASSWORD
VIEW_COST
VIEW_SALARY
```

---

# 6. Permission Rules

## 6.1 Default Deny

If no applicable permission grants access:

```text
DENY
```

Never default to allow.

---

## 6.2 ACCESS Is Required for Actions

Every non-ACCESS action also requires:

```text
ACCESS
```

Therefore:

```text
EDIT
```

effectively means:

```text
ACCESS + EDIT
```

Example:

```text
ACCESS = false
EDIT = true
```

Result:

```text
Can EDIT = false
```

This prevents a user from modifying something they cannot access.

---

# 7. MenuPermission

`MenuPermission` defines which permissions are valid/applicable to each menu.

Example:

```text
Inventory
    ACCESS
    ADD
    EDIT
    DELETE
    PRINT
    POST

Employee
    ACCESS
    ADD
    EDIT
    DELETE

Report
    ACCESS
    PRINT
```

This prevents meaningless permissions from being assigned.

For example:

```text
Employee
    POST
```

should be rejected or ignored if POST is not defined for that menu.

---

# 8. Permission Validation

When checking a permission:

1. Verify the menu exists.
2. Verify the requested permission is valid for that menu.
3. Verify the user has `ACCESS`.
4. Resolve the user's roles.
5. Resolve role/menu permission rules.
6. Apply allow/deny precedence.
7. Return the final result.

Unknown permissions must never automatically grant access.

---

# 9. Database Schema

## 9.1 Permission

Purpose:

> Catalog of all named permission codes.

```text
Permission
-----------
PermissionCode
Description
Active
```

Example:

```text
ACCESS
ADD
EDIT
DELETE
PRINT
POST
ROLLBACK
APPROVE
```

`PermissionCode` must be unique.

---

# 10. Menu

Purpose:

> Runtime representation of the XML menu hierarchy.

Recommended fields:

```text
Menu
----
MenuId
MenuCode
Name
Route
ParentMenuCode
AlwaysVisible
Active
SortOrder
```

`MenuCode` must be unique.

Hierarchy is determined by:

```text
ParentMenuCode
```

and/or the synced XML structure.

Do not infer hierarchy from dotted IDs.

---

# 11. Role

Purpose:

> Company-scoped security role.

```text
Role
----
RoleId
CompanyCode
RoleCode
RoleName
Active
```

Recommended unique constraint:

```text
CompanyCode + RoleCode
```

Example:

```text
Company A
    ADMIN
    MANAGER
    HR
    USER
```

---

# 12. UserRoleMapping

Purpose:

> Associates an authenticated user with one or more roles.

```text
UserRoleMapping
---------------
UserLoginUid
RoleId
```

### Multiple roles are supported.

A user may have:

```text
User
 ├── HR
 ├── MANAGER
 └── REPORT_VIEWER
```

within the same company.

Recommended unique constraint:

```text
UserLoginUid + RoleId
```

Duplicate mappings must not be allowed.

---

# 13. MenuPermission

Purpose:

> Defines which permissions are applicable to each menu.

```text
MenuPermission
--------------
MenuId
PermissionId
```

Recommended unique constraint:

```text
MenuId + PermissionId
```

---

# 14. RoleMenuPermission

Purpose:

> Defines whether a role allows or denies a permission on a menu.

```text
RoleMenuPermission
------------------
RoleId
MenuId
PermissionId
Effect
```

Where:

```text
Effect =
    ALLOW
    DENY
```

Recommended unique constraint:

```text
RoleId + MenuId + PermissionId
```

A role should not contain duplicate permission rules for the same menu.

---

# 15. Multiple Role Resolution

A user may have multiple roles.

Example:

```text
User A
    ├── MANAGER
    ├── HR
    └── REPORT_VIEWER
```

The effective permission must be calculated from all applicable roles.

---

# 16. Allow / Deny Precedence

When multiple roles provide conflicting permissions:

```text
Explicit DENY > ALLOW
```

Resolution:

```text
Any DENY
    ↓
DENY

Otherwise, any ALLOW
    ↓
ALLOW

Otherwise
    ↓
DENY
```

Example:

```text
MANAGER
    EDIT = ALLOW

HR
    EDIT = DENY
```

Effective result:

```text
EDIT = DENY
```

This gives administrators a reliable mechanism for restricting a permission granted by another role.

---

# 17. Role Resolution Example

```text
User
 │
 ├── Role A
 │      └── EDIT = ALLOW
 │
 ├── Role B
 │      └── PRINT = ALLOW
 │
 └── Role C
        └── EDIT = DENY
```

Effective permissions:

```text
EDIT  = DENY
PRINT = ALLOW
```

---

# 18. ADMIN Role

`ADMIN` is a special application role.

For an ADMIN in the current company:

```text
CanAsync(...)
    → true

CanAccessAsync(...)
    → true
```

The permission UI should expose the full known permission catalog to ADMIN.

---

# 19. ADMIN Company Isolation

ADMIN bypass must remain company-scoped.

Example:

```text
Company A
    ADMIN
       ↓
Full access to Company A
```

The same user must not automatically receive:

```text
Company B
Company C
```

access unless the system explicitly introduces a separate global/system administrator concept.

### Recommended future distinction

```text
ADMIN
    = company administrator

SYSTEM_ADMIN
    = global administrator
```

Do not introduce `SYSTEM_ADMIN` unless required.

---

# 20. Authentication vs Authorization

ADMIN bypass does not bypass authentication.

A user must still be authenticated.

Similarly:

```text
ADMIN ≠ bypass company isolation
```

unless explicitly implemented as a system-level administrator.

---

# 21. Company Scope

Authorization is company-scoped.

The authenticated user's company must come from trusted authentication/server-side context.

Do not trust client-supplied:

```text
CompanyCode
UserID
Role
Permission
```

for authorization decisions.

---

# 22. Branch Scope

Branch-scoped permissions are explicitly **out of scope for v1**.

`BranchCode` may remain available in the authenticated user/claims for display and future use.

It must not accidentally become part of permission evaluation.

Future extension:

```text
Role
 + Company
 + Branch
 + Menu
 + Permission
```

can be introduced later if required.

---

# 23. Menu Definition Service

```text
IMenuDefinitionService
```

Responsibilities:

* Read XML
* Parse XML
* Validate XML
* Validate duplicate menu codes
* Validate required properties
* Validate routes
* Validate permissions
* Build normalized menu definition

Lifetime:

```text
Singleton
```

because the XML definition is application-wide and not user-specific.

---

# 24. Menu Cache

`MenuCache` contains the complete active menu definition.

Lifetime:

```text
Singleton
```

It must contain:

```text
Complete menu definition
```

and never:

```text
User-specific filtered menu
```

---

# 25. Immutable Menu Definition

The shared menu definition must never be mutated during user filtering.

Incorrect:

```csharp
item.Children = filteredChildren;
```

if `item` belongs to the shared cached tree.

Correct:

```text
Shared complete tree
        │
        ├── User A → new filtered tree
        ├── User B → new filtered tree
        └── User C → new filtered tree
```

---

# 26. Menu Sync Service

```text
IMenuSyncService
```

Responsibilities:

```text
menus.xml
    ↓
Parse
    ↓
Validate
    ↓
Compare with DB
    ↓
Preview changes
    ↓
Apply changes
```

Sync is company-independent because the menu definition itself is application-wide.

---

# 27. XML Source of Truth

Path:

```text
Menus/menus.xml
```

configured through:

```text
Menus:XmlPath
```

The XML is version controlled.

The database stores the runtime menu representation.

---

# 28. XML Rules

* Hierarchy comes from nested XML.
* Do not parse dotted menu codes to determine hierarchy.
* `MenuCode` is stable.
* MenuCode must be unique.
* Route belongs to the leaf menu.
* Removed XML menus are deactivated, not hard-deleted.
* XML sync must preserve existing `MenuId` values by `MenuCode`.

---

# 29. XML Validation

Before synchronization:

```text
Validate:
    Duplicate MenuCode
    Missing MenuCode
    Missing Name
    Invalid parent
    Invalid route
    Invalid permission
    Duplicate permission
    Invalid hierarchy
```

If validation fails:

```text
Do not modify the database.
```

---

# 30. Transactional Menu Synchronization

Actual sync must execute in a database transaction.

```text
Parse XML
    ↓
Validate complete XML
    ↓
Generate sync plan
    ↓
BEGIN TRANSACTION
    ↓
Insert/update Menu
    ↓
Insert/update MenuPermission
    ↓
Deactivate removed menus
    ↓
Commit
```

If any operation fails:

```text
ROLLBACK
```

No partial synchronization is allowed.

---

# 31. Sync Preview

Endpoint:

```text
POST /admin/menus/sync/preview
```

ADMIN only.

Preview should report:

```text
Added
Changed
Deactivated
Unchanged
Permission changes
Route changes
Hierarchy changes
```

Example:

```text
Added:
    Inventory.StockTransfer

Changed:
    Inventory.Stock route

Deactivated:
    Legacy.StockReport

Permission changes:
    Employee → APPROVE added
```

Preview must not modify the database.

---

# 32. Actual Sync

Endpoint:

```text
POST /admin/menus/sync
```

ADMIN only.

Recommended flow:

```text
Preview
    ↓
Review
    ↓
Confirm
    ↓
Transactional sync
    ↓
Result
```

---

# 33. Startup Sync

Default:

```text
Menus:SyncOnStartup=false
```

Production startup must not automatically modify the security database unless explicitly configured.

Recommended production workflow:

```text
Deploy XML
    ↓
Preview sync
    ↓
Review
    ↓
Execute sync
```

---

# 34. Removed Menus

If a menu exists in DB but no longer exists in XML:

```text
Do not DELETE
```

Instead:

```text
Active = false
```

This preserves:

* Historical permissions
* Auditability
* Existing role configuration
* Database references

---

# 35. AccessRightService

```text
IAccessRightService
```

Lifetime:

```text
Scoped
```

It is responsible for:

* Current user's effective permissions
* Company context
* Permission cache
* Permission evaluation
* ADMIN handling
* Cache refresh

### Critical rule

> `AccessRightService` must never be Singleton.

Its state is user/context-specific.

---

# 36. Permission Cache

Use dictionary-based lookup.

Conceptually:

```csharp
Dictionary<
    string,
    EffectiveMenuPermissions>
```

where the key is:

```text
MenuCode
```

and the value contains the user's effective permissions.

Example:

```text
Inventory
    ACCESS
    ADD
    EDIT
    PRINT

Employee
    ACCESS
    ADD
    EDIT
```

---

# 37. Effective Permission Representation

Do not expose mutable database entities as the authorization cache.

Use an immutable/read-only representation.

Example:

```csharp
public sealed record EffectiveMenuPermissions(
    string MenuCode,
    IReadOnlySet<string> Permissions);
```

The cache should represent the final effective result after:

```text
Roles
    ↓
Allow/Deny resolution
    ↓
MenuPermission validation
    ↓
Effective permissions
```

---

# 38. Permission Evaluation

Conceptual API:

```csharp
Task<bool> CanAsync(
    string menuCode,
    string permission);
```

and:

```csharp
Task<bool> CanAccessAsync(
    string menuCode);
```

Rules:

```text
ADMIN
    → ALLOW

Unknown menu
    → DENY

Unknown permission
    → DENY

Permission not applicable to menu
    → DENY

No ACCESS
    → DENY

Explicit DENY
    → DENY

Explicit ALLOW + ACCESS
    → ALLOW
```

---

# 39. AccessRightService Refresh

Permissions must be refreshed when security context changes.

Required events:

```text
Successful login
Sign out
Re-login
Company change
Role assignment change
Permission configuration change
Password-change sign-in if applicable
```

The refresh must completely replace the old permission cache.

Do not merge the new user's permissions into the old cache.

---

# 40. Login Cache Safety

A reused Blazor circuit must not retain the previous user's permissions.

Flow:

```text
User A
   ↓
Logout
   ↓
Clear permission cache
   ↓
User B login
   ↓
Resolve User B roles
   ↓
Build User B permission cache
```

Expected:

```text
User B never inherits User A permissions.
```

---

# 41. NavigationService

```text
INavigationService
```

Lifetime:

```text
Scoped
```

Responsibilities:

* Load complete menu
* Resolve current user's permissions
* Build ACCESS-filtered tree
* Return a user-specific immutable tree

It must never modify the singleton menu cache.

---

# 42. Navigation Filtering Rules

## Leaf

A leaf requires:

```text
ACCESS
```

unless:

```text
AlwaysVisible = true
```

## Parent

A parent does not require its own ACCESS permission.

It remains visible when at least one child remains.

Example:

```text
Master
 └── Employee
      └── Employee List
```

If Employee List is accessible:

```text
Master
 └── Employee
      └── Employee List
```

remains visible.

---

# 43. AlwaysVisible

`AlwaysVisible` may be used for navigation items such as:

```text
Home
Dashboard
Help
Logout
```

However:

> `AlwaysVisible` only controls navigation visibility. It does not bypass page authorization.

For example:

```text
AlwaysVisible = true
```

does not automatically grant:

```text
ACCESS
```

to the target page.

---

# 44. Immutable Navigation Filtering

Filtering must create new nodes.

Conceptual implementation:

```csharp
private MenuNode? Filter(MenuNode source)
{
    if (source.IsLeaf)
    {
        return CanAccess(source.MenuCode)
            ? source with { Children = [] }
            : null;
    }

    var children = source.Children
        .Select(Filter)
        .Where(x => x is not null)
        .ToList();

    if (children.Count == 0)
        return null;

    return source with
    {
        Children = children!
    };
}
```

The important rule is:

```text
source tree is never mutated
```

---

# 45. Navigation UI

`NavMenu` should only consume the filtered tree.

It must not:

* Query SQL
* Resolve roles
* Evaluate permission bitmasks
* Perform XML parsing
* Implement allow/deny logic

Architecture:

```text
NavMenu
    ↓
INavigationService
    ↓
Filtered menu tree
```

---

# 46. Page Authorization Component

Create:

```text
ErpWeb.UI/Components/Security/MenuAuthorize.razor
```

Usage:

```razor
<MenuAuthorize MenuCode="@MenuCodes.InventoryDemo">
    ...
</MenuAuthorize>
```

Behaviour:

```text
Authenticated?
    ↓
Yes
    ↓
Can ACCESS?
    ↓
Yes → Render
No  → /unauthorized
```

---

# 47. Action Authorization Component

Create:

```text
ErpWeb.UI/Components/Security/PermissionAuthorize.razor
```

Usage:

```razor
<PermissionAuthorize
    MenuCode="@MenuCodes.InventoryDemo"
    Permission="@PermissionCodes.Add">

    <DxButton Text="Add" />

</PermissionAuthorize>
```

The component only controls UI visibility.

The actual operation must still perform authorization.

---

# 48. Business Service Authorization

Core services must not trust UI authorization.

Example:

```csharp
public async Task ResetPasswordAsync(
    string userId)
{
    if (!await accessRightService.CanAsync(
            MenuCodes.PasswordAdmin,
            PermissionCodes.Edit))
    {
        throw new UnauthorizedAccessException();
    }

    // Perform operation
}
```

This guarantees:

```text
API/service call
    ↓
authorization
    ↓
business operation
```

even if the UI is bypassed.

---

# 49. Example: Password Admin

`PasswordAdminService` requires:

### List users

```text
ACCESS
```

### Reset password

```text
ACCESS + EDIT
```

### Force change password

```text
ACCESS + EDIT
```

The service must check permissions itself.

---

# 50. API Authorization

For independent ASP.NET Core API requests:

```text
HTTP Request
    ↓
Authenticated user
    ↓
Trusted company context
    ↓
Resolve roles
    ↓
Resolve permissions
    ↓
IAccessRightService / authorization layer
    ↓
ALLOW / DENY
```

Never trust client-provided:

```text
UserID
CompanyCode
RoleCode
Permission
```

for authorization.

---

# 51. Service Lifetime Summary

| Service                  | Lifetime   | Purpose                                 |
| ------------------------ | ---------- | --------------------------------------- |
| `IMenuDefinitionService` | Singleton  | Parse/validate XML                      |
| `MenuCache`              | Singleton  | Complete immutable menu definition      |
| `IMenuService`           | Scoped     | Runtime menu access                     |
| `IMenuSyncService`       | Scoped     | XML → DB synchronization                |
| `IAccessRightService`    | **Scoped** | Current-user effective permission cache |
| `INavigationService`     | Scoped     | User-specific filtered tree             |
| `IUserRoleSyncService`   | Scoped     | User → role reconciliation              |

---

# 52. Security Context

Authorization is based on:

```text
Authenticated User
+
Current Company
+
Assigned Roles
+
Menu
+
Permission
```

Branch is not part of v1 permission evaluation.

---

# 53. User Role Synchronization

Existing:

```text
userlogin.userlevel
```

must be reconciled into:

```text
UserRoleMapping
```

through:

```text
IUserRoleSyncService
```

On successful login:

```text
userlogin.userlevel
    ↓
Resolve company role
    ↓
Create/update UserRoleMapping
    ↓
RefreshPermissionsAsync
```

This allows the existing authentication/user-level model to transition into the new RBAC model.

---

# 54. Role Mapping Rules

Role synchronization must:

* Resolve the role within the authenticated company.
* Create the mapping if missing.
* Avoid duplicate mappings.
* Remove obsolete mappings only according to the defined synchronization policy.
* Never map a user to a role belonging to another company.

---

# 55. Database Constraints

Recommended constraints:

```text
Permission
    UNIQUE PermissionCode

Menu
    UNIQUE MenuCode

Role
    UNIQUE CompanyCode + RoleCode

UserRoleMapping
    UNIQUE UserLoginUid + RoleId

MenuPermission
    UNIQUE MenuId + PermissionId

RoleMenuPermission
    UNIQUE RoleId + MenuId + PermissionId
```

These constraints prevent duplicate security configuration.

---

# 56. Auditability

Security configuration changes should be auditable.

At minimum, record:

```text
Who
When
Company
Action
Target
Old value
New value
```

Recommended audit targets:

* Role creation
* Role modification
* User role assignment
* User role removal
* Permission allow
* Permission deny
* Menu sync
* Menu deactivation

---

# 57. Logging

Log security-relevant events such as:

```text
Login permission refresh
Permission refresh
Company change
Unauthorized page access
Unauthorized business operation
Unknown menu
Unknown permission
Invalid XML
Menu sync
Menu sync failure
Role mapping change
```

Do not log passwords or sensitive credential information.

---

# 58. Menu Sync Security

Only ADMIN users in the current company/application administration context may execute:

```text
POST /admin/menus/sync/preview
POST /admin/menus/sync
```

The sync endpoints must not be accessible merely because the URL is known.

---

# 59. Error Handling

The system must fail securely.

| Situation            | Result                  |
| -------------------- | ----------------------- |
| Unknown menu         | DENY                    |
| Unknown permission   | DENY                    |
| No permission        | DENY                    |
| No ACCESS            | DENY                    |
| Explicit DENY        | DENY                    |
| Invalid XML          | No DB modification      |
| Sync failure         | Transaction rollback    |
| Unauthenticated      | Authentication required |
| Wrong company        | DENY                    |
| Invalid role mapping | DENY / fail safely      |

---

# 60. Performance

The system should:

* Parse XML once
* Cache complete menu definition
* Load permissions once per security context
* Use dictionary permission lookup
* Avoid database queries for every button render
* Use `AsNoTracking()` for read-only queries
* Build filtered navigation once per relevant context change
* Avoid mutating shared objects

Expected normal flow:

```text
Login
  ↓
Role reconciliation
  ↓
Load effective permissions
  ↓
Build dictionary
  ↓
Load cached menu definition
  ↓
Build filtered navigation
  ↓
Normal navigation
  ↓
No repeated permission DB query
```

---

# 61. Testing Strategy

Testing must cover both positive and negative cases.

---

## 61.1 Permission Tests

### No permission

```text
Expected = DENY
```

### ACCESS only

```text
ACCESS = ALLOW
ADD = DENY
EDIT = DENY
DELETE = DENY
```

### EDIT without ACCESS

```text
Expected = DENY
```

### ACCESS + EDIT

```text
Expected:
ACCESS = ALLOW
EDIT = ALLOW
```

---

# 62. Multiple Role Tests

Example:

```text
Role A:
    EDIT = ALLOW

Role B:
    EDIT = DENY
```

Expected:

```text
EDIT = DENY
```

Example:

```text
Role A:
    EDIT = ALLOW

Role B:
    PRINT = ALLOW
```

Expected:

```text
EDIT = ALLOW
PRINT = ALLOW
```

---

# 63. MenuPermission Tests

If:

```text
Employee
    ACCESS
    EDIT
```

and role contains:

```text
POST = ALLOW
```

Expected:

```text
POST = DENY
```

because POST is not applicable to the Employee menu.

---

# 64. ADMIN Tests

Test:

```text
Company A ADMIN
```

Expected:

```text
All Company A permissions = ALLOW
```

Then switch to:

```text
Company B
```

Expected:

```text
No Company B authorization unless the user is assigned there.
```

---

# 65. Menu Filtering Tests

### Accessible child

```text
Parent
 └── Child ACCESS
```

Expected:

```text
Parent
 └── Child
```

### No accessible child

```text
Parent
 └── Child NO ACCESS
```

Expected:

```text
Nothing
```

### One accessible among many

```text
Parent
 ├── Child A NO ACCESS
 └── Child B ACCESS
```

Expected:

```text
Parent
 └── Child B
```

---

# 66. Shared Menu Isolation Test

Mandatory test:

```text
User A:
    Payroll = DENY

User B:
    Payroll = ALLOW
```

Expected:

```text
User A → Payroll hidden

User B → Payroll visible
```

The result must remain correct even when the complete menu definition is singleton/cached.

This verifies that navigation filtering does not mutate shared state.

---

# 67. Login/Logout Isolation Test

```text
Login User A
    ↓
Verify A permissions
    ↓
Logout
    ↓
Login User B
    ↓
Verify B permissions
```

Expected:

```text
No permission from User A remains.
```

---

# 68. Company Isolation Test

```text
Company A
    Role A
        EDIT Employee

Company B
    No Employee EDIT
```

User must not receive Company A permission while operating in Company B.

---

# 69. Direct URL Test

User without ACCESS manually navigates to:

```text
/employeelist
```

Expected:

```text
/unauthorized
```

---

# 70. Business Operation Test

User has:

```text
ACCESS
```

but not:

```text
DELETE
```

Attempt:

```text
Delete Employee
```

Expected:

```text
UnauthorizedAccessException
```

or equivalent application-level authorization response.

The operation must not execute.

---

# 71. Sync Tests

Test:

```text
New menu
Changed menu
Removed menu
Changed route
Changed hierarchy
Added permission
Removed permission
Invalid XML
Duplicate MenuCode
```

For invalid XML:

```text
Database must remain unchanged.
```

For runtime sync failure:

```text
Entire transaction must rollback.
```

---

# 72. File Inventory

| Area          | Path                                                      | Responsibility             |
| ------------- | --------------------------------------------------------- | -------------------------- |
| Seed          | `scripts/init-menu-access.sql`                            | Initial DB schema/data     |
| Permission    | `ErpWeb.Core/Menus/PermissionCodes.cs`                    | Named permissions          |
| Menu codes    | `ErpWeb.Core/Menus/MenuCodes.cs`                          | Stable menu codes          |
| Access        | `ErpWeb.Core/Menus/AccessRightService.cs`                 | Effective permission cache |
| Navigation    | `ErpWeb.Core/Menus/NavigationService.cs`                  | Immutable filtered tree    |
| XML           | `ErpWeb/Menus/menus.xml`                                  | Source menu definition     |
| Page gate     | `ErpWeb.UI/Components/Security/MenuAuthorize.razor`       | Page ACCESS                |
| Action gate   | `ErpWeb.UI/Components/Security/PermissionAuthorize.razor` | UI permission visibility   |
| Business auth | `ErpWeb.Core/Security/*Service.cs`                        | Server-side authorization  |
| Sync          | `ErpWeb.Core/Menus/MenuSyncService.cs`                    | XML → DB                   |
| Role sync     | `ErpWeb.Core/Menus/UserRoleSyncService.cs`                | User → Role                |
| Tests         | `ErpWeb.Tests/*Access*.cs`                                | Authorization tests        |

---

# 73. Implementation Phases

## Phase 1 — Database

Implement:

* Permission
* Menu
* Role
* UserRoleMapping
* MenuPermission
* RoleMenuPermission
* Constraints
* Indexes

---

## Phase 2 — Permission Catalog

Implement:

```text
PermissionCodes
```

and seed:

```text
ACCESS
ADD
EDIT
DELETE
PRINT
POST
ROLLBACK
APPROVE
```

---

## Phase 3 — Menu Definition

Implement:

```text
MenuDefinitionService
MenuCache
menus.xml
XML validation
```

---

## Phase 4 — Menu Sync

Implement:

```text
MenuSyncService
Preview
Transactional sync
Deactivation
ADMIN protection
```

---

## Phase 5 — Role Synchronization

Implement:

```text
UserRoleSyncService
```

and migrate:

```text
userlevel
    ↓
UserRoleMapping
```

---

## Phase 6 — AccessRightService

Implement:

```text
Effective permission resolution
Multiple roles
ALLOW/DENY precedence
ACCESS implication
ADMIN bypass
Dictionary cache
Refresh
Company isolation
```

---

## Phase 7 — Navigation

Implement:

```text
NavigationService
Immutable filtering
Parent/leaf rules
AlwaysVisible
NavMenu integration
```

---

## Phase 8 — Page Authorization

Implement:

```text
MenuAuthorize
/unauthorized
Direct URL protection
```

---

## Phase 9 — Action Authorization

Implement:

```text
PermissionAuthorize
UI visibility
```

---

## Phase 10 — Business Authorization

Protect:

```text
Core services
Critical operations
API endpoints
```

---

## Phase 11 — Testing

Implement the complete security test matrix:

```text
Permission
Multiple roles
Allow/Deny
ACCESS implication
ADMIN
Company isolation
MenuPermission
Menu filtering
Shared-tree isolation
Login/logout
Direct URL
Business operation
Menu sync
```

---

# 74. Migration from Legacy System

The following legacy components are explicitly removed from the new design:

```text
LevelGroup
LevelRights
vAccessRight
AccessRightEnum
bitwise rights
legacy CheckUserRight()
legacy menu filtering logic
```

The migration direction is:

```text
Old:
User
 ↓
userlevel
 ↓
LevelGroup
 ↓
LevelRights
 ↓
bitmask


New:
User
 ↓
UserRoleMapping
 ↓
Role
 ↓
RoleMenuPermission
 ↓
Named Permission
```

The old database structures should not be reintroduced into the new runtime architecture.

---

# 75. Explicit Non-Goals for v1

The following are intentionally excluded:

* Bitwise permissions
* `AccessRightEnum`
* `LevelGroup`
* `LevelRights`
* `vAccessRight`
* Branch-scoped permissions
* Global SYSTEM_ADMIN role
* Full role administration UI unless separately requested
* DevExpress-specific authorization logic
* Treating menu visibility as authorization

---

# 76. Definition of Done

## Database

* [ ] All six security tables implemented
* [ ] Unique constraints implemented
* [ ] Required indexes verified
* [ ] Seed script works
* [ ] Company isolation verified

## Permission

* [ ] Named PermissionCodes implemented
* [ ] ACCESS implication implemented
* [ ] Unknown permission denied
* [ ] MenuPermission validation implemented
* [ ] Multiple-role resolution implemented
* [ ] DENY > ALLOW implemented
* [ ] ADMIN behaviour implemented

## Menu

* [ ] XML parser implemented
* [ ] XML validation implemented
* [ ] Menu cache implemented
* [ ] Menu sync implemented
* [ ] Preview sync implemented
* [ ] Sync transactional
* [ ] Removed menus deactivated
* [ ] Immutable filtering implemented

## User / Role

* [ ] UserRoleMapping implemented
* [ ] Multiple roles supported
* [ ] Duplicate mappings prevented
* [ ] Company-scoped roles enforced
* [ ] Login reconciliation implemented

## UI

* [ ] NavMenu uses filtered tree
* [ ] MenuAuthorize implemented
* [ ] PermissionAuthorize implemented
* [ ] Direct URL authorization works
* [ ] Unauthorized page works

## Business

* [ ] Core services enforce permissions
* [ ] Mutating operations protected
* [ ] API endpoints protected
* [ ] Client-supplied authorization context ignored

## Cache

* [ ] AccessRightService is Scoped
* [ ] Permission cache is dictionary-based
* [ ] Cache refresh works
* [ ] Logout clears cache
* [ ] Re-login clears/replaces cache
* [ ] Company change refreshes cache
* [ ] No cross-user cache leakage

## Security

* [ ] Default deny
* [ ] ACCESS required for actions
* [ ] DENY overrides ALLOW
* [ ] ADMIN remains company-scoped
* [ ] Company isolation verified
* [ ] Direct URL protection verified
* [ ] Business authorization verified
* [ ] Shared menu isolation verified

## Sync

* [ ] Preview does not modify DB
* [ ] Actual sync is transactional
* [ ] Invalid XML causes no DB change
* [ ] Failed sync rolls back
* [ ] Removed XML menus are deactivated
* [ ] Sync endpoints are protected

---

# 77. Final Architecture Rules

These rules are mandatory:

1. **Use named permissions, never bitmasks.**
2. **`PermissionCodes` is the single permission source of truth.**
3. **No permission means DENY.**
4. **Unknown permissions mean DENY.**
5. **Every non-ACCESS operation requires ACCESS.**
6. **`MenuPermission` defines which permissions are valid for a menu.**
7. **Users may have multiple roles.**
8. **Roles are company-scoped.**
9. **Explicit DENY overrides ALLOW.**
10. **ADMIN bypass is company-scoped.**
11. **Authentication is always required.**
12. **Client-supplied authorization values cannot be trusted.**
13. **Branch permissions are out of scope for v1.**
14. **The XML menu definition is the source-controlled menu definition.**
15. **The database contains the runtime menu/security model.**
16. **XML synchronization must validate before modifying DB.**
17. **Actual synchronization must be transactional.**
18. **Removed menus are deactivated, not deleted.**
19. **The complete menu definition may be singleton/cached.**
20. **The shared menu tree must never be mutated.**
21. **User-specific navigation must create a new filtered tree.**
22. **`AccessRightService` must be Scoped.**
23. **Permission cache must be user/company-specific.**
24. **Cache must be replaced on security-context changes.**
25. **Page authorization is independent of menu visibility.**
26. **Business authorization is independent of UI visibility.**
27. **API authorization must use trusted authenticated context.**
28. **Navigation must not contain authorization business logic.**
29. **DevExpress must not be a security dependency.**
30. **Security failures must default to DENY.**
31. **All critical authorization paths must have automated tests.**
32. **Cross-user and cross-company isolation must be tested.**

---

# 78. Final Target Architecture

```text
                         menus.xml
                            │
                            ▼
                 MenuDefinitionService
                            │
                      Parse / Validate
                            │
                            ▼
                       MenuCache
                    Immutable Complete Tree
                            │
                            ▼
                       MenuSyncService
                            │
                            ▼
                       SQL Server
                            │
        ┌───────────────────┼──────────────────┐
        │                   │                  │
        ▼                   ▼                  ▼
     Menu              Permission           Role
        │                                      │
        ▼                                      ▼
 MenuPermission                      UserRoleMapping
        │                                      │
        └──────────────────┬───────────────────┘
                           ▼
                  RoleMenuPermission
                           │
                           ▼
                 AccessRightService
                    Scoped Cache
                           │
             ┌─────────────┼──────────────┐
             │             │              │
             ▼             ▼              ▼
       Navigation      MenuAuthorize   PermissionAuthorize
        Service             │              │
             │              │              │
             ▼              ▼              ▼
          NavMenu         Pages        UI Actions
                                            │
                                            ▼
                                      Core Services
                                            │
                                            ▼
                                       Final Authz
```

## Final quality target

**10/10 — production-ready RBAC architecture**

The important characteristics are:

```text
Named permissions
+
Multiple company-scoped roles
+
Explicit DENY > ALLOW
+
ACCESS prerequisite
+
MenuPermission validation
+
Immutable menu filtering
+
Scoped effective-permission cache
+
XML → DB transactional synchronization
+
Page authorization
+
Business/API authorization
+
Company isolation
+
Comprehensive security tests
```

This should be treated as the **master implementation plan** for the new menu/access-control architecture.
