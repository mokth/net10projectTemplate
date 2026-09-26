# Quick Deployment Reference

## What Was Fixed

### 1. JSON Configuration Errors
- Removed invalid comments from `appsettings.json` (Einvoice section)
- Removed invalid comments from `appsettings.local.json`
- Removed invalid comments from `appsettings.Production.json`

### 2. Production Configuration
- Created proper `appsettings.Production.json` with all required settings
- Set `AppBasePath` to `erpweb/` for sub-application deployment
- Added production-ready logging levels

## Deployment Steps (Quick Version)

### Option A: Using the Deployment Script

```powershell
# 1. Run the deployment script
.\deploy.ps1 -SqlServer "YOUR_SQL_SERVER" -DbUser "YOUR_USER" -DbPassword "YOUR_PASSWORD"

# 2. Upload the generated zip file to your server
# 3. Extract to IIS directory
# 4. Set environment variable: ASPNETCORE_ENVIRONMENT=Production
# 5. Test: https://werp3.wincomcloud.com/erpweb
```

### Option B: Manual Deployment

```powershell
# 1. Build and publish
cd C:\wincom\net10projects\ErpWeb
dotnet publish -c Release -o ./publish

# 2. Upload ./publish folder to server
# 3. Update appsettings.Production.json on server with your database details
# 4. Configure IIS (see DEPLOYMENT_GUIDE.md)
```

## Required Configuration

Update `appsettings.Production.json` on your server:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SERVER;Database=ERPWeb;User Id=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=true;Encrypt=True"
  }
}
```

## Environment Variables (Alternative)

Instead of editing config files, you can set environment variables:

```
ConnectionStrings__DefaultConnection=Server=...;Database=...
ASPNETCORE_ENVIRONMENT=Production
```

## Verify Deployment

1. Check logs: `./logs/erpweb-logfile.txt`
2. Test login: https://werp3.wincomcloud.com/erpweb/login
3. Check database connectivity

## Common Issues

| Issue | Solution |
|-------|----------|
| 500.30 Error | Check logs, verify database connection |
| Database Connection Failed | Verify SQL Server accessibility, firewall |
| Static Files Not Loading | Ensure wwwroot folder is deployed |
| Blazor Connection Issues | Enable WebSocket in IIS |

## Files Created

- `appsettings.Production.json` - Production configuration
- `DEPLOYMENT_GUIDE.md` - Detailed deployment instructions
- `deploy.ps1` - Automated deployment script
- `test-config.ps1` - Configuration validation script
- `QUICK_DEPLOY.md` - This file

## Next Steps

1. **Get your database details** from your DBA
2. **Run the deployment script** or follow manual steps
3. **Test the application** on the server
4. **Monitor logs** for any issues

---

**Need Help?** Check `DEPLOYMENT_GUIDE.md` for detailed troubleshooting.