# Final Deployment Steps

## ✅ All Issues Fixed

1. **JSON Configuration Errors** - Fixed
2. **Base Path Navigation Issue** - Fixed  
3. **Connection String Format** - Fixed
4. **Build Errors** - Fixed
5. **Antiforgery Token Error** - Fixed (Data Protection keys now persist)

## 🚀 Ready to Deploy

### Step 1: Build the Application

```powershell
cd C:\wincom\net10projects\ErpWeb
dotnet publish -c Release -o ./publish --no-restore
```

### Step 2: Deploy to Server

1. **Upload the `./publish` folder** to your server
   - Location: `C:\inetpub\wwwroot\erpweb\` (or your IIS directory)

2. **Update Configuration** on server:
   - Edit `appsettings.Production.json` if needed
   - Verify the connection string points to your database

3. **Configure IIS**:
   - Create Application in IIS Manager
   - Physical path: Where you deployed the files
   - Application Pool: "No Managed Code"
   - .NET CLR Version: "No Managed Code"

4. **Set Environment Variable**:
   - In IIS Application Pool → Advanced Settings → Environment Variables
   - Add: `ASPNETCORE_ENVIRONMENT` = `Production`

### Step 3: Test the Application

1. **Visit**: https://werp3.wincomcloud.com/erpweb
2. **Test Login**: Try logging in with your credentials
3. **Check Navigation**: Verify all links work correctly
4. **Check Logs**: Look in `./logs/erpweb-logfile.txt` for any errors

## 🔧 Data Protection Fix

The application now persists Data Protection keys to the file system to prevent antiforgery token errors when the application restarts.

**Key Directory**: `DataProtection-Keys/` (auto-created on first run)

**Important**: 
- This directory must be writable by the application pool identity
- Do NOT delete this directory or the keys inside it
- The directory is already in `.gitignore` to prevent keys from being committed

## 🔍 What Was Fixed

### 1. Base Path Issue (Navigation Problem)
**Problem**: URLs were missing `/erpweb/` prefix
**Solution**: Updated form actions to use `NavigationManager.BaseUri`

**Files Changed**:
- `Login.razor` - Form action now uses full base URL
- `ChangePassword.razor` - Form action now uses full base URL

### 2. JSON Configuration Errors
**Problem**: Invalid comments in JSON files
**Solution**: Removed all comments from production config

**Files Changed**:
- `appsettings.Production.json` - Clean JSON, proper connection string

### 3. Build Configuration
**Problem**: .NET SDK version mismatch
**Solution**: Updated `global.json` to use correct SDK version

## 📋 Deployment Checklist

- [ ] Application builds successfully
- [ ] Upload `./publish` folder to server
- [ ] Update `appsettings.Production.json` with database details
- [ ] Configure IIS Application
- [ ] Set `ASPNETCORE_ENVIRONMENT=Production` environment variable
- [ ] Enable WebSocket in IIS (for Blazor)
- [ ] Test login functionality
- [ ] Test navigation throughout the app
- [ ] Check application logs

## ⚠️ Important Notes

1. **Database Connection**: Ensure SQL Server is accessible from the web server
2. **First Startup**: Menu sync will run on first startup (may take a few minutes)
3. **Logs**: Always check `./logs/erpweb-logfile.txt` for detailed error information
4. **HTTPS**: Ensure SSL certificate is properly configured

## 🎯 Expected Behavior After Fix

1. Visit https://werp3.wincomcloud.com/erpweb
2. Login form submits to https://werp3.wincomcloud.com/erpweb/account/login
3. Navigation throughout the app uses `/erpweb/` prefix
4. All static assets load correctly
5. Blazor SignalR connection works properly

## 🆘 Troubleshooting

If you still have issues after deployment:

1. **Check the logs** in `./logs/erpweb-logfile.txt`
2. **Verify database connectivity** from the web server
3. **Check IIS configuration** (WebSocket, Application Pool)
4. **Test with browser developer tools** (F12 → Network tab)

## 📞 Support

If issues persist:
1. Check application logs
2. Verify all configuration values
3. Test database connectivity separately
4. Contact your system administrator

---

**Status**: ✅ Ready for deployment
**Last Updated**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")