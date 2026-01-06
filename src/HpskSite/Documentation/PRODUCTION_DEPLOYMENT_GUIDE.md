# Production Deployment Guide - Umbraco CMS

**Project:** HPSK Site (Umbraco v16.2, .NET 9.0)
**Last Updated:** 2025-11-07
**Hosting:** Simply.com (Windows/IIS, no .NET 9 runtime support)

---

## Quick Reference: Incremental vs Full Deployment

### 🚀 Incremental Deployment (Simple Code Changes - 2 minutes)

**SAFE for:** View-only changes, CSS/JS changes, simple controller fixes

**NOT safe for:** Adding new classes, changing dependencies, structural changes

```bash
# For VIEW changes only - no build needed!
# Just upload the changed .cshtml file from Views/ folder

# For CODE changes in existing files:
dotnet build -c Release

# Check what changed (shows files modified in last 5 minutes):
Get-ChildItem 'bin\Release\net9.0\win-x86\' -Recurse |
  Where-Object {$_.LastWriteTime -gt (Get-Date).AddMinutes(-5)} |
  Select-Object FullName

# Upload the files listed above
```

**⚠️ NOTE:** Media files in `wwwroot/media/` should NEVER be included in deployments

### 🔄 Full Deployment (Recommended for Safety - 10 minutes)

**RECOMMENDED for:** Any code changes you're not 100% sure about

**REQUIRED for:** NuGet packages, Umbraco upgrades, new files, .NET changes

```bash
# 1. Clean everything
dotnet clean -c Release
Remove-Item -Path 'C:\temp\publish\*' -Recurse -Force

# 2. Full publish
dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"

# 3. Copy production config
Copy-Item 'appsettings.Production.json' -Destination 'C:\temp\publish\' -Force

# 4. ⚠️ CRITICAL: Remove media folder to preserve production media
Remove-Item -Path 'C:\temp\publish\wwwroot\media' -Recurse -Force

# 5. Upload ALL files from C:\temp\publish\ (media excluded)
```

**⚠️ IMPORTANT:** Never upload `wwwroot/media/` - it will overwrite production images!

**💡 When in doubt, do a full deployment!**

**See full process below** ⬇️

---

## Critical Deployment Configuration

### Issue: Runtime Razor Compilation Failures in Self-Contained Deployments

When deploying Umbraco as a self-contained application (required when hosting provider doesn't support .NET 9 runtime), you may encounter `CompilationFailedException` errors with messages like:

```
error CS0246: The type or namespace name 'ClubsPage' could not be found
error CS0246: The type or namespace name 'AdminPage' could not be found
```

This occurs because:
1. Self-contained deployments bundle the .NET runtime
2. Runtime Razor compilation requires all reference assemblies to be available
3. Umbraco's ModelsBuilder generates strongly-typed models that may not be available at runtime
4. The `Microsoft.Identity.Client` and other dependencies may not have their reference assemblies included

### Solution

#### Option 1: Disable ModelsBuilder (Recommended for Production)

**Configuration:**

In `appsettings.Production.json`:
```json
{
  "Umbraco": {
    "CMS": {
      "ModelsBuilder": {
        "ModelsMode": "Nothing"
      }
    }
  },
  "MvcRazorRuntimeCompilation": {
    "Enabled": false
  }
}
```

**View Updates:**

Change all views from strongly-typed to dynamic models:

**Before:**
```csharp
@using Umbraco.Cms.Web.Common.PublishedModels
@inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage<ClubsPage>
```

**After:**
```csharp
@inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage
```

Access properties via `Model.Value<T>()` instead of strongly-typed properties.

#### Option 2: Include Reference Assemblies (If Models Required)

If you need strongly-typed models, ensure reference assemblies are published:

In `HpskSite.csproj`:
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <UseStaticWebAssets>false</UseStaticWebAssets>
  <MvcRazorExcludeRefAssembliesFromPublish>false</MvcRazorExcludeRefAssembliesFromPublish>
  <PreserveCompilationContext>true</PreserveCompilationContext>
  <PreserveCompilationReferences>true</PreserveCompilationReferences>
  <RazorCompileOnPublish>true</RazorCompileOnPublish>
</PropertyGroup>
```

This creates a `refs` folder in the publish output with all reference assemblies needed for runtime compilation.

## Deployment Types

### Full Deployment (Initial or Major Changes)

Use full deployment when:
- First time deploying to production
- .NET version upgrade
- Major dependency changes (NuGet packages)
- Umbraco version upgrade
- Project configuration changes (.csproj file)

### Incremental Deployment (Code Changes)

⚠️ **WARNING: Incremental deployments can be risky!**

Use incremental deployment ONLY for these scenarios:

#### Safe Incremental Deployments:

**1. View-Only Changes** (Zero Risk)
- Edit `.cshtml` files
- Upload changed view files directly - no build needed
- Can't break anything - views are interpreted at runtime

**2. CSS/JS Changes** (Zero Risk)
- Edit static files in `wwwroot/`
- Upload changed files directly - no build needed
- Browser cache may require Ctrl+F5 to see changes

**3. Simple Code Fixes** (Low Risk)
- Small bug fix in existing method
- No new classes or dependencies
- No signature changes

**How to track what changed:**
```bash
# After making code changes, build:
dotnet build -c Release

# See what files were modified in last 10 minutes:
Get-ChildItem 'bin\Release\net9.0\win-x86\' -Recurse -File |
  Where-Object {$_.LastWriteTime -gt (Get-Date).AddMinutes(-10)} |
  Select-Object Name, LastWriteTime, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB,2)}}

# Typically you'll see:
# - HpskSite.dll         (your code - ALWAYS upload this)
# - HpskSite.pdb         (debug info - optional)
# - HpskSite.deps.json   (dependencies - upload if changed)
# - HpskSite.exe         (wrapper - upload if changed)
```

**Safe incremental process:**
```bash
# 1. Build
dotnet build -c Release

# 2. Create temp folder
New-Item -ItemType Directory -Path 'C:\temp\deploy' -Force

# 3. Copy all recently changed files
$threshold = (Get-Date).AddMinutes(-10)
Get-ChildItem 'bin\Release\net9.0\win-x86\' -File |
  Where-Object {$_.LastWriteTime -gt $threshold} |
  Copy-Item -Destination 'C:\temp\deploy\'

# 4. Review what's in C:\temp\deploy\ before uploading
Get-ChildItem 'C:\temp\deploy\'

# 5. Upload files from C:\temp\deploy\ to production
```

#### When Incremental is NOT Safe:

❌ **Do NOT use incremental for:**
- Adding new classes/files
- Changing method signatures
- Adding new NuGet packages
- Changing dependencies
- Database migrations
- Configuration changes to .csproj
- When you're not sure what changed

➡️ **Use full deployment instead!**

### When Full Deployment IS Required

⚠️ **You MUST do a full deployment when:**
- Adding/removing NuGet packages
- Changing .NET target framework
- Modifying .csproj settings
- Updating Umbraco CMS version
- Adding new third-party DLLs
- Changing runtime identifier (win-x86, win-x64, etc.)

## Step-by-Step Full Deployment Process

### 1. Clean and Build

```bash
# Clean previous builds
dotnet clean -c Release

# Remove publish folder
Remove-Item -Path 'C:\temp\publish\*' -Recurse -Force
```

### 2. Publish Self-Contained Build

```bash
dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"
```

**Why self-contained?**
- Simply.com doesn't support .NET 9 runtime
- Self-contained includes the runtime in the deployment
- Larger deployment size (~150MB vs ~50MB)

### 3. Configure Production Settings

Copy production configuration:
```bash
Copy-Item 'C:\Repos\HpskSite\appsettings.Production.json' -Destination 'C:\temp\publish\appsettings.Production.json' -Force
```

Ensure `appsettings.Production.json` contains:
- Database connection string with `User Id` (with space, not `UserId`)
- Email SMTP configuration
- ModelsBuilder mode set to `Nothing`
- MvcRazorRuntimeCompilation disabled
- Static web assets disabled
- Debug mode off

### 4. Update web.config

Ensure `C:\temp\publish\web.config` contains:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath=".\HpskSite.exe" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
          <environmentVariable name="ASPNETCORE_HOSTINGSTARTUPASSEMBLIES" value="" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
```

**Critical settings:**
- `ASPNETCORE_ENVIRONMENT` = "Production"
- `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` = "" (disables static web assets)

### 5. Create Required Folders

```bash
New-Item -ItemType Directory -Path 'C:\temp\publish\wwwroot\media' -Force
New-Item -ItemType Directory -Path 'C:\temp\publish\logs' -Force
```

### 6. Remove Test Files

Ensure no test assemblies are included:
```bash
Remove-Item -Path 'C:\temp\publish\Tests' -Recurse -Force -ErrorAction SilentlyContinue
```

### 6.5. Preserve Production Media Files

⚠️ **CRITICAL DATA PRESERVATION**: Do NOT overwrite the `wwwroot/media/` folder on production!

**Why This Matters:**
- Media files (club logos, banners, user uploads) are NOT in source control (excluded by `.gitignore`)
- Media files are NOT included in the deployment package (not published by `dotnet publish`)
- The publish process creates an **empty** `wwwroot/media/` folder
- Uploading this empty folder overwrites existing production media → **data loss**
- Database still references old media paths → broken images requiring manual re-upload

**How Umbraco Media Works:**
- **Database**: Stores media metadata and paths (e.g., `/media/35vdei10/hpklogo.png`)
- **File System**: Stores actual files in `wwwroot/media/{folder-id}/{filename}`
- Both must stay synchronized - if files are deleted, images break

**Solution: Exclude Media from Deployment**

**Option A: Remove media folder before upload (RECOMMENDED)**
```bash
# Remove the empty media folder from publish output
Remove-Item -Path 'C:\temp\publish\wwwroot\media' -Recurse -Force

# Now upload C:\temp\publish\ - media folder won't be touched on production
```

**Option B: Use FTP exclusion filters**
```
In FileZilla or your FTP client:
1. Select all files/folders in C:\temp\publish\
2. Right-click wwwroot\media folder
3. Select "Add to exclusion list" or "Skip this directory"
4. Upload remaining files
```

**Option C: Manual FTP upload (careful!)**
```
1. Upload all files from C:\temp\publish\ to production
2. EXCEPT: Do not upload wwwroot\media\ folder
3. Production's existing wwwroot/media/ remains untouched
```

**Backup Strategy (Recommended):**
```bash
# Before each deployment, backup production media (optional but recommended):
# Use FTP client to download: production:/wwwroot/media/ → local:/media-backup/

# Store backup with date:
# C:\Backups\media-2025-11-06\
```

**First-Time Deployment:**
If this is the first deployment, the media folder won't exist on production yet:
```bash
# Keep the empty media folder in publish output
# Upload it along with everything else
# Umbraco will populate it as users upload media
```

**Verification After Deployment:**
```bash
# Check that production has media folder with files:
# production:/wwwroot/media/
#   ├── 35vdei10/
#   │   └── hpklogo.png
#   ├── go1jvp04/
#   │   └── haaplingebana.jpg
#   └── ... (other media files)

# Folder should NOT be empty (unless first deployment)
```

### 7. Deploy to Server

Upload **ALL** files from `C:\temp\publish\` to production server **EXCEPT media folder**:

⚠️ **IMPORTANT: Exclude wwwroot/media/ from upload** (see Section 6.5)

**Recommended approach:**
```bash
# Remove media folder before upload
Remove-Item -Path 'C:\temp\publish\wwwroot\media' -Recurse -Force

# Upload everything from C:\temp\publish\ via FTP
```

**What to upload:**
- ✅ All DLLs and executables (HpskSite.exe, *.dll)
- ✅ Configuration files (appsettings.Production.json, web.config)
- ✅ Views, CSS, JS, static assets
- ✅ All subdirectories: refs/, runtimes/, wwwroot/, umbraco/, etc.
- ❌ **DO NOT upload wwwroot/media/** - preserve existing production media

**Critical Notes:**
- Self-contained deployments require ALL DLLs - don't skip any
- Ensure all subdirectories are uploaded except media
- Production media folder should remain untouched

### 8. Verify Deployment

Check these files exist on server:
- `HpskSite.exe` - Main executable
- `web.config` - IIS configuration
- `appsettings.Production.json` - Production settings
- `refs/` folder - Reference assemblies (if using Option 2)
- `wwwroot/media/` folder - Umbraco media storage
- `umbraco/` folder - Umbraco CMS files

## Common Deployment Errors

### Error: "Cannot find compilation library location for package 'Microsoft.Identity.Client'"

**Cause:** Reference assemblies not published for runtime compilation

**Fix:**
- Option 1: Disable ModelsBuilder (set to "Nothing") and use dynamic models
- Option 2: Add PreserveCompilationReferences to project file (see above)

### Error: "The type or namespace name 'ClubsPage' could not be found"

**Cause:** View using strongly-typed model but ModelsBuilder disabled

**Fix:** Change view inheritance from `UmbracoViewPage<ClubsPage>` to `UmbracoViewPage`

### Error: "Keyword not supported: 'userid'"

**Cause:** Connection string uses `UserId` instead of `User Id` (with space)

**Fix:** Change connection string to use `User Id=` (with space)

### Error: "DirectoryNotFoundException: C:\Repos\HpskSite\wwwroot\"

**Cause:** Static web assets trying to access development paths in production

**Fix:** Ensure `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=""` in web.config

### Error: HTTP 500.30 - ASP.NET Core app failed to start

**Cause:** Multiple possible causes

**Troubleshooting:**
1. Check `logs\stdout-*.log` file on server
2. Check `umbraco\Logs\*.json` for detailed errors
3. Verify `ASPNETCORE_ENVIRONMENT=Production` in web.config
4. Ensure all DLLs were uploaded (check file count)

## Project Configuration Files

### HpskSite.csproj - Key Settings

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>

<!-- Disable static web assets in Release mode (production) -->
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <UseStaticWebAssets>false</UseStaticWebAssets>
  <MvcRazorExcludeRefAssembliesFromPublish>false</MvcRazorExcludeRefAssembliesFromPublish>
  <PreserveCompilationContext>true</PreserveCompilationContext>
  <PreserveCompilationReferences>true</PreserveCompilationReferences>
</PropertyGroup>

<PropertyGroup>
  <!-- Compile Razor views for production to avoid runtime compilation -->
  <RazorCompileOnBuild>false</RazorCompileOnBuild>
  <RazorCompileOnPublish>true</RazorCompileOnPublish>
</PropertyGroup>
```

### appsettings.Production.json - Template

```json
{
  "ConnectionStrings": {
    "umbracoDbDSN": "Server=SERVER;Database=DB;User Id=USER;Password=PASS;TrustServerCertificate=True;Encrypt=True;",
    "umbracoDbDSN_ProviderName": "Microsoft.Data.SqlClient"
  },
  "Email": {
    "SmtpHost": "websmtp.simply.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "Username": "admin@domain.com",
    "Password": "password",
    "FromAddress": "admin@domain.com",
    "FromName": "Site Name",
    "AdminEmail": "admin@domain.com",
    "SiteUrl": "https://domain.com"
  },
  "Umbraco": {
    "CMS": {
      "Global": {
        "UseHttps": true
      },
      "Hosting": {
        "Debug": false
      },
      "ModelsBuilder": {
        "ModelsMode": "Nothing"
      },
      "RuntimeMinification": {
        "UseInMemoryCache": true,
        "CacheBuster": "Version"
      }
    }
  },
  "MvcRazorRuntimeCompilation": {
    "Enabled": false
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning"
    }
  },
  "HostBuilder": {
    "ReloadConfigOnChange": false
  }
}
```

## ModelsBuilder Modes Explained

| Mode | Description | Use Case | Runtime Compilation? |
|------|-------------|----------|---------------------|
| `Nothing` | No models generated | Production with dynamic models | No |
| `InMemoryAuto` | Models generated in memory | Development only | Yes |
| `SourceCodeAuto` | Models generated to disk | Development | No |
| `SourceCodeManual` | Models manually generated | Advanced scenarios | No |

**Production Recommendation:** Use `Nothing` mode to avoid runtime compilation issues.

## Deployment Checklist

- [ ] Clean build folders (`bin`, `obj`, `publish`)
- [ ] Build as self-contained with win-x86 runtime
- [ ] Copy production appsettings.json
- [ ] Verify web.config environment variables
- [ ] Create wwwroot/media and logs folders
- [ ] Remove Tests folder from output
- [ ] **⚠️ CRITICAL: Remove wwwroot/media from publish output before upload**
- [ ] **Backup production media folder (optional but recommended)**
- [ ] Verify no leftover temppublish folders
- [ ] Check connection string format (User Id with space)
- [ ] Verify ModelsBuilder mode is "Nothing"
- [ ] Verify MvcRazorRuntimeCompilation is disabled
- [ ] Ensure all views use dynamic models (no strongly-typed)
- [ ] Upload ALL files to server EXCEPT wwwroot/media (don't skip any DLLs)
- [ ] Verify refs folder exists if using compilation references
- [ ] **Verify production media folder was NOT overwritten**
- [ ] Test site after deployment
- [ ] Check Umbraco logs for errors
- [ ] **Verify images display correctly (logos, banners, etc.)**

## Recovery Steps

If deployment fails:

1. **Check stdout logs:** `logs\stdout-YYYYMMDD-*.log`
2. **Check Umbraco logs:** `umbraco\Logs\UmbracoTraceLog.*.json`
3. **Verify environment:** Ensure `ASPNETCORE_ENVIRONMENT=Production`
4. **Test connection string:** Use SQL Management Studio to verify
5. **Check file permissions:** Ensure IIS app pool has write access to umbraco/Data, umbraco/Logs, wwwroot/media
6. **Restart app pool:** In IIS Manager, restart the application pool

## Related Documentation

- [AUTHORIZATION_SECURITY_AUDIT.md](./AUTHORIZATION_SECURITY_AUDIT.md) - Security fixes implemented
- [CLUB_SYSTEM_MIGRATIONS.md](./CLUB_SYSTEM_MIGRATIONS.md) - Club system architecture
- [LOGIN_REGISTRATION_SYSTEM.md](./LOGIN_REGISTRATION_SYSTEM.md) - Authentication system
- [TRAINING_SCORING_SYSTEM.md](./TRAINING_SCORING_SYSTEM.md) - Training features

---

**Notes:**
- This guide is specific to deployments where the hosting provider doesn't support .NET 9 runtime
- For hosts with .NET 9 runtime support, use framework-dependent deployment instead
- Always test deployment in staging environment before production
- Keep backup of previous deployment before uploading new version
