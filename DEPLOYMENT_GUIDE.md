# ErpWeb Production Deployment Guide

## Overview
This guide helps you deploy ErpWeb to: https://werp3.wincomcloud.com/erpweb

## Pre-Deployment Checklist

### 1. Database Setup
- [ ] SQL Server is accessible from the web server
- [ ] Database `ERPWeb` exists on the production server
- [ ] Database user has proper permissions (db_owner or appropriate roles)
- [ ] Firewall allows connections on SQL Server port (default: 1433)

### 2. Server Requirements
- [ ] .NET 10.0 Runtime installed on server
- [ ] IIS configured with ASP.NET Core Module
- [ ] Application pool configured for .NET Core

### 3. Configuration Files
- [ ] Update `appsettings.Production.json` with production values
- [ ] Set environment variable `ASPNETCORE_ENVIRONMENT=Production`

## Step-by-Step Deployment

### Step 1: Update Configuration

Edit `appsettings.Production.json` on your server with these values:

```json
{
  "AppBasePath": "erpweb/",
  
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_SQL_SERVER;Database=ERPWeb;User Id=YOUR_DB_USER;Password=YOUR_DB_PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=true;Encrypt=True"
  }
}
```

**Replace:**
- `YOUR_SQL_SERVER` → Your SQL Server hostname or IP (e.g., `sql.wincomcloud.com` or `192.168.1.100`)
- `YOUR_DB_USER` → Database username
- `YOUR_DB_PASSWORD` → Database password

### Step 2: Build and Publish

Run these commands on your development machine:

```powershell
# Navigate to project directory
cd C:\wincom\net10projects\ErpWeb

# Restore packages
dotnet restore

# Build in Release mode
dotnet build -c Release

# Publish to a folder
dotnet publish -c Release -o ./publish
```

### Step 3: Deploy to Server

1. **Copy the published files** to your server:
   - Upload the contents of `./publish` folder to your IIS site directory
   - Typical path: `C:\inetpub\wwwroot\erpweb\`

2. **Set Environment Variable** on the server:
   - Open IIS Manager
   - Select your application pool
   - Click "Advanced Settings"
   - Under "Environment Variables", add:
     - Name: `ASPNETCORE_ENVIRONMENT`
     - Value: `Production`

3. **Configure IIS Application**:
   - Create a new Application in IIS
   - Set the physical path to your deployed files
   - Set the Application Pool to "No Managed Code"
   - Set the Application Pool .NET CLR Version to "No Managed Code"

### Step 4: Database Initialization

Run the database deployment in this order before enabling Delivery Request:

1. Deploy the application-compatible base schema.
2. Run `scripts/create-sales-delivery-request.sql`.
3. Verify `SaDeliveryRequest`, `SaDeliveryRequestSource`, `SaDeliveryRequestAudit`, and `PrWorkOrderDemandAllocation` exist.
4. Replace the `@Company` and `@Branch` `CHANGE_ME` inputs in `scripts/init-sales-delivery-request-menu.sql` with the real target values.
5. Run `scripts/init-sales-delivery-request-menu.sql` to seed `SA_DR`, permissions, and `DR` numbering. It is idempotent, but fails if the DR schema or inputs are missing.
6. Verify `SA_DR` permissions and `DR` numbering for the actual company/branch.
7. Smoke test one `Sales Order → Delivery Request → Work Order` flow.

The application menu sync may still run from `Menus/menus.xml` on startup, but it does not replace the explicit Delivery Request schema deployment.

### Step 5: Verify Deployment

1. **Check Application Logs**:
   - Look for logs in: `./logs/erpweb-logfile.txt`
   - Check for any startup errors

2. **Test the Application**:
   - Visit: https://werp3.wincomcloud.com/erpweb
   - Try logging in with your credentials

## Troubleshooting

### Common Issues and Solutions

#### 1. HTTP Error 500.30 - ASP.NET Core app failed to start

**Cause**: Usually database connection or configuration issues

**Solution**:
- Check the logs in `./logs/erpweb-logfile.txt`
- Verify database connection string
- Ensure SQL Server is accessible

#### 2. Database Connection Errors

**Error**: "A network-related or instance-specific error occurred"

**Solution**:
- Verify SQL Server is running and accessible
- Check firewall rules (port 1433)
- Test connection using SQL Server Management Studio

#### 3. Static Files Not Loading

**Solution**:
- Ensure the `wwwroot` folder is included in deployment
- Check IIS MIME types configuration

#### 4. Blazor SignalR Connection Issues

**Solution**:
- Ensure WebSocket is enabled in IIS
- Check if the server supports long-running connections

## Environment Variables Reference

You can override any appsettings value using environment variables:

```
ConnectionStrings__DefaultConnection=Server=...;Database=...
Einvoice__EInv_SecretID=your_secret_id
Einvoice__EInv_SecretKey=your_secret_key
```

## Security Considerations

1. **Database Credentials**: Never commit database passwords to source control
2. **HTTPS**: Ensure SSL certificate is properly configured
3. **Firewall**: Only open necessary ports (80, 443, 1433)
4. **Updates**: Keep .NET runtime and dependencies updated

## Support

If you encounter issues:
1. Check the application logs first
2. Verify all configuration values
3. Test database connectivity separately
4. Contact your system administrator

---

**Last Updated**: $(Get-Date -Format "yyyy-MM-dd")
