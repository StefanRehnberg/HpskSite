# Fixing and Preventing Encoding Issues

## ✅ Quick Fix

Run this PowerShell script to fix all corrupted emojis:

```powershell
.\fix-encoding.ps1
```

This will:
- Fix all corrupted emojis (ðŸ → 🏆, etc.)
- Save all files with UTF-8 encoding (no BOM)
- Report which files were fixed

## 🛡️ Prevention (Visual Studio)

### 1. EditorConfig File
I've added `.editorconfig` to your project. This tells Visual Studio to:
- Use UTF-8 encoding for all `.cshtml` files
- Use UTF-8 with BOM for `.cs` files (C# standard)
- Maintain consistent formatting

**Visual Studio automatically respects `.editorconfig` files!**

### 2. Visual Studio Settings (Optional)

To ensure UTF-8 is default in Visual Studio:

1. **Tools → Options → Environment → Documents**
   - Check "Save documents as Unicode (UTF-8) when data cannot be saved in codepage"

2. **File → Advanced Save Options** (when editing a file)
   - Select "Unicode (UTF-8 without signature) - Codepage 65001"
   - This is now the default for `.cshtml` files thanks to `.editorconfig`

### 3. Check File Encoding

In Visual Studio, you can see a file's encoding in the bottom-right of the editor window.
- For `.cshtml` files: Should show "UTF-8"
- For `.cs` files: Should show "UTF-8 with signature"

## 📋 After Fixing

After running the fix script:

1. **Test locally**:
   ```powershell
   dotnet run
   ```

2. **Rebuild and redeploy**:
   ```powershell
   dotnet publish -c Release -o publish
   cd publish
   tar -czf ../hpsk-deploy.tar.gz *
   cd ..
   scp hpsk-deploy.tar.gz root@37.27.45.188:/tmp/
   ```

3. **On server**:
   ```bash
   systemctl stop hpsk
   cd /var/www/hpsk
   tar -xzf /tmp/hpsk-deploy.tar.gz
   systemctl start hpsk
   ```

## 🎯 What Was Fixed

### Corrupted Emojis:
- `🥇` → 🥇 (Gold medal)
- `🥈` → 🥈 (Silver medal)
- `🥉` → 🥉 (Bronze medal)
- `🏆` → 🏆 (Trophy)
- `🎯` → 🎯 (Target)
- `📅` → 📅 (Calendar)
- `🕐` → 🕐 (Clock)
- `🟢` → 🟢 (Green circle - "Open")
- `🟡` → 🟡 (Yellow circle - "Coming soon")
- `🔵` → 🔵 (Blue circle - "Ongoing")

### Swedish Characters:
Already fixed in previous step:
- `Ã¤` → ä, `Ã¥` → å, `Ã¶` → ö
- `Ã„` → Ä, `Ã…` → Å, `Ã–` → Ö

## 🔍 Why This Happened

The files were saved with **Windows-1252** encoding instead of **UTF-8**, causing:
- Swedish characters to show as weird combinations (Ã¤ instead of ä)
- Emojis to show as `ðŸ` followed by garbled characters or question marks

The `.editorconfig` file now prevents this from happening again.



