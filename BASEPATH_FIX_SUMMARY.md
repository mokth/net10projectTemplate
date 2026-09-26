# Base Path Fix Summary

## Problem
When navigating to https://werp3.wincomcloud.com/erpweb, the application was generating URLs without the `/erpweb/` prefix, causing navigation to fail.

Example: Instead of going to `https://werp3.wincomcloud.com/erpweb/account/login`, it was going to `https://werp3.wincomcloud.com/account/login`.

## Root Cause
The login and change password forms had hardcoded URLs in their `action` attributes:
- `action="/account/login"` ❌
- `action="/account/change-password"` ❌

These URLs don't include the base path `/erpweb/`, so when the browser submits the form, it goes to the wrong URL.

## Fixes Applied

### 1. Login.razor
**Before:**
```razor
<form class="login-form" method="post" action="/account/login">
```

**After:**
```razor
@inject NavigationManager NavigationManager
...
<form class="login-form" method="post" action="@($"{NavigationManager.BaseUri}account/login")">
```

### 2. ChangePassword.razor
**Before:**
```razor
<form class="login-form" method="post" action="/account/change-password">
```

**After:**
```razor
<!-- Uses Navigation from PageBase -->
<form class="login-form" method="post" action="@($"{Navigation.BaseUri}account/change-password")">
```

### 3. Configuration Files
- Fixed `appsettings.Production.json` - removed invalid JSON comments
- Fixed connection string format
- Added `web.config` for IIS deployment

## How It Works

The `NavigationManager.BaseUri` property returns the full base URL including the app base path:
- At site root: `https://werp3.wincomcloud.com/`
- At sub-application: `https://werp3.wincomcloud.com/erpweb/`

So when we use `$"{NavigationManager.BaseUri}account/login"`, it generates:
- At site root: `https://werp3.wincomcloud.com/account/login`
- At sub-application: `https://werp3.wincomcloud.com/erpweb/account/login`

## Testing

After deploying these changes:

1. Visit https://werp3.wincomcloud.com/erpweb
2. Try to login - the form should now submit to the correct URL
3. Check that navigation works correctly throughout the app

## Additional Notes

- The `AppBasePath` in `appsettings.Production.json` is set to `"erpweb/"` which is correct
- The `AppNavigation` service in `PageBase` already handles base path for programmatic navigation
- The `web.config` file ensures IIS properly handles the ASP.NET Core module

## Files Modified

1. `ErpWeb.UI/Components/Pages/Login.razor` - Added NavigationManager injection, fixed form action
2. `ErpWeb.UI/Components/Pages/ChangePassword.razor` - Fixed form action to use Navigation.BaseUri
3. `ErpWeb/appsettings.Production.json` - Fixed JSON format, removed comments
4. `ErpWeb/web.config` - Created for IIS deployment

## Verification

To verify the fix is working:
1. Deploy the updated application
2. Visit https://werp3.wincomcloud.com/erpweb
3. Open browser developer tools (F12)
4. Check the Network tab when submitting the login form
5. Verify the request goes to `/erpweb/account/login` not `/account/login`