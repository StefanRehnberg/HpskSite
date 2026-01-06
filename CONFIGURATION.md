# Configuration Guide

This document explains how to configure the HPSK Site application for local development and production deployment.

## Quick Start (Local Development)

1. Copy the template file:
   ```bash
   cp src/HpskSite/appsettings.Development.template.json src/HpskSite/appsettings.Development.json
   ```

2. The development template uses LocalDB by default, which requires no additional configuration.

3. Run the application:
   ```bash
   dotnet run --project src/HpskSite
   ```

## Production Configuration

1. Copy the production template:
   ```bash
   cp src/HpskSite/appsettings.Production.template.json src/HpskSite/appsettings.Production.json
   ```

2. Edit `appsettings.Production.json` and replace all placeholder values:

### Required Settings

| Setting | Description |
|---------|-------------|
| `ConnectionStrings.umbracoDbDSN` | SQL Server connection string with credentials |
| `JwtSettings.SecretKey` | Secret key for JWT tokens (minimum 32 characters) |
| `Email.SmtpHost` | SMTP server hostname |
| `Email.Username` | SMTP authentication username |
| `Email.Password` | SMTP authentication password |
| `Email.FromAddress` | Email address for outgoing mail |
| `Email.AdminEmail` | Admin notification email address |
| `Email.SiteUrl` | Public URL of the site |
| `Firebase.CredentialPath` | Path to Firebase Admin SDK JSON file |

### Firebase Setup

1. Create a Firebase project at https://console.firebase.google.com
2. Go to Project Settings > Service Accounts
3. Generate a new private key (downloads a JSON file)
4. Place the JSON file in `src/HpskSite/App_Data/`
5. Update `Firebase.CredentialPath` with the filename

### JWT Secret Generation

Generate a secure random string (minimum 32 characters):
```bash
# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])

# Or use an online generator for a 256-bit key
```

## Environment Variables (Alternative)

Instead of configuration files, you can use environment variables:

```bash
# Database
ConnectionStrings__umbracoDbDSN="Server=...;Database=...;..."

# JWT
JwtSettings__SecretKey="your-secret-key"

# Email
Email__SmtpHost="smtp.example.com"
Email__Password="your-password"
```

## GitHub Actions / CI/CD

For CI/CD pipelines, use GitHub Secrets or your CI provider's secret management:

1. Go to your repository Settings > Secrets and variables > Actions
2. Add the following secrets:
   - `PRODUCTION_DB_CONNECTION_STRING`
   - `JWT_SECRET`
   - `SMTP_PASSWORD`
   - `FIREBASE_CREDENTIALS` (base64-encoded JSON)

## Security Notes

- **Never commit** `appsettings.Production.json` or `appsettings.Development.json`
- These files are listed in `.gitignore` for protection
- Use the `.template.json` files as reference
- Rotate credentials immediately if accidentally exposed
- Use strong, unique passwords for all services

## Mobile App Configuration

The mobile app (`HpskSite.Mobile`) connects to the backend API. Update the API base URL in:
- `src/HpskSite.Mobile/Services/ApiService.cs` (or equivalent configuration)

For Android signing, see `.github/workflows/mobile-build.yml` for required secrets:
- `ANDROID_KEYSTORE_BASE64`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`
- `ANDROID_STORE_PASSWORD`
- `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`
