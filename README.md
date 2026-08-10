# ErpWeb — multi-project .NET 10 ERP foundation

## Projects

| Project | Role |
|---------|------|
| `ErpWeb` | Host — `Program.cs`, cookie endpoints, `wwwroot`, DI composition |
| `ErpWeb.Model` | Entities, EF `AppDbContext`, repositories |
| `ErpWeb.Core` | Business services (`IAuthService`, `ICurrentUserService`, password admin) |
| `ErpWeb.Library` | Shared utilities (`PasswordHasher`) |
| `ErpWeb.UI` | Blazor UI (pages, layouts, grid, theme) |
| `ErpWeb.Report` | Reporting assembly (scaffold) |
| `ErpWeb.Tests` | Automated security/service/repository tests |

## Run

```powershell
cd c:\wincom\net10projects\ErpWeb
dotnet run
```

Demo login: Company `DEMO` / User `admin` / Password `Demo@123`  
Database: `.\SQLEXPRESS` / `ERPLiteEx` / table `userlogin`

ADMIN password admin: User Accounts `/adminuser` (reset password / force-change).

Menu / access rights: run `scripts/init-menu-access.sql` after `init-userlogin.sql`. Sync menus from XML via `POST /admin/menus/sync` (ADMIN). Startup sync is off (`Menus:SyncOnStartup=false`). Roles are **company-scoped** (named `PermissionCodes`, not bitmasks). Architecture: [`ErpWeb/docs/menu-access.md`](ErpWeb/docs/menu-access.md).

### Password security notes (v1)

- Policy: min 8, ASCII letter + digit, reject username match; validated at startup (non-weakenable).
- Self-change uses authenticated `LoginId` for policy checks.
- Admin reset sets `changepass=true` via EF `ExecuteUpdateAsync` (`uid` + `CompanyCode`).
- Concurrent password writers are **last-write-wins** (no rowversion).
- After admin reset, an existing auth cookie may remain until logout/expiry; next login requires the new password and hits the existing `MustChangePassword` gate when `changepass=true`.
