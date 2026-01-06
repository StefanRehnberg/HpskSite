# Database Setup Instructions

## Quick Setup for New Computers

### Step 1: Clone the Repository
```bash
git clone https://github.com/yourusername/HpskSite.git
cd HpskSite
```

### Step 2: Copy Database Files
Copy these files from another computer to the `umbraco/Data/` folder:
- `Umbraco.mdf` (the database file)
- `Umbraco_log.ldf` (the log file)

**Important**: Make sure to stop the application on the source computer before copying the database files.

### Step 3: Install Prerequisites
Make sure you have:
- .NET 9.0 SDK
- SQL Server LocalDB (usually comes with Visual Studio)
- Visual Studio or VS Code

### Step 4: Run the Application
```bash
dotnet restore
dotnet run
```

## How It Works
- **Database files copied manually**: `Umbraco.mdf` and `Umbraco_log.ldf` are copied from another computer
- **Automatic LocalDb setup**: The connection string uses LocalDb which works out of the box
- **Media files included**: All uploaded media is in the repository
- **Temporary files regenerated**: Cache and indexes are created automatically

## Troubleshooting

### If you get database connection errors:
1. Make sure SQL Server LocalDb is installed
2. Check that the database files exist in `umbraco/Data/`
3. Try running as Administrator

### If you get permission errors:
1. Make sure no other applications are using the database files
2. Check file permissions on the `umbraco/Data/` folder

## Alternative: Fresh Database
If you don't have access to the database files, you can let Umbraco create a fresh database, but you'll need to set up your content again through the Umbraco backoffice.