# Antiforgery Token Fix

## Problem

The application was throwing this error:

```
Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException: The antiforgery token could not be decrypted.
---> System.Security.Cryptography.CryptographicException: The key {43932c9c-3329-4719-9dae-3cf395a2b652} was not found in the key ring.
```

## Root Cause

ASP.NET Core Data Protection keys were being stored in memory by default. When the application restarts (IIS app pool recycle, server restart, redeployment), these keys are lost, and previously issued antiforgery tokens cannot be decrypted.

## Solution

Configured Data Protection to persist keys to the file system.

### Changes Made

#### 1. Program.cs

**Added using directive:**
```csharp
using Microsoft.AspNetCore.DataProtection;
```

**Added Data Protection configuration:**
```csharp
// Configure Data Protection to persist keys to file system
// This prevents antiforgery token errors when the application restarts
var keysFolder = Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysFolder))
    .SetApplicationName("ErpWeb");
```

#### 2. DataProtection-Keys Directory

**Created:** `ErpWeb/DataProtection-Keys/`

**Added:** `.gitignore` file to prevent keys from being committed to source control

## How It Works

1. **First Run**: Data Protection creates XML key files in `DataProtection-Keys/`
2. **Subsequent Runs**: Keys are loaded from the file system
3. **Application Restart**: Keys persist, so antiforgery tokens remain valid
4. **Security**: Keys are stored locally and not committed to source control

## Important Notes

### Directory Permissions

The `DataProtection-Keys/` directory must be:
- ✅ Writable by the application pool identity
- ✅ Readable by the application pool identity
- ✅ NOT accessible from the web (not in wwwroot)

### Security Considerations

1. **Keys are sensitive**: They protect authentication cookies and antiforgery tokens
2. **Don't commit keys**: The `.gitignore` file prevents this
3. **Backup keys**: Consider backing up the `DataProtection-Keys/` directory
4. **Multi-server deployments**: For multiple servers, use a shared key storage (Redis, SQL Server, Azure Blob Storage)

### Multi-Server Deployments

If you deploy to multiple servers, you need to configure a shared key storage:

```csharp
// Option 1: SQL Server
builder.Services.AddDataProtection()
    .PersistKeysToSqlServer(connectionString, "DataProtectionKeys");

// Option 2: Redis
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redisConnection, "DataProtectionKeys");

// Option 3: Azure Blob Storage
builder.Services.AddDataProtection()
    .PersistKeysToAzureBlobStorage(new Uri("..."));
```

## Testing

After deploying this fix:

1. **First Visit**: Application creates keys in `DataProtection-Keys/`
2. **Login**: Should work without errors
3. **Restart Application**: Keys persist, login should still work
4. **Check Directory**: Verify `DataProtection-Keys/` contains XML files

## Troubleshooting

### Issue: Keys directory not created

**Solution**: Ensure the application has write permissions to the directory

### Issue: Permission denied errors

**Solution**: Grant write permissions to the application pool identity:
```powershell
icacls "C:\inetpub\wwwroot\erpweb\DataProtection-Keys" /grant "IIS AppPool\YourAppPoolName:(OI)(CI)F"
```

### Issue: Keys lost after deployment

**Solution**: 
1. Include `DataProtection-Keys/` in your deployment package
2. Or configure shared key storage for multi-server deployments

## Files Modified

1. `ErpWeb/Program.cs` - Added Data Protection configuration
2. `ErpWeb/DataProtection-Keys/.gitignore` - Prevent keys from being committed
3. `ErpWeb/DataProtection-Keys/` - Directory for key storage (auto-created)

## Verification

Check that the fix is working:

1. Deploy the application
2. Visit https://werp3.wincomcloud.com/erpweb
3. Login successfully
4. Restart the application (IIS app pool recycle)
5. Try to login again - should work without antiforgery errors
6. Check `DataProtection-Keys/` directory for XML files

---

**Status**: ✅ Fixed
**Impact**: Prevents antiforgery token errors on application restart