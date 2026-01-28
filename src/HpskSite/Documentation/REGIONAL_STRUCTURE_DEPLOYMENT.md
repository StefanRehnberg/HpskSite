# Regional Structure Deployment Guide

This guide documents the steps to deploy the regional site structure that organizes clubs under 26 Swedish regional federations (kretsar).

## Overview

**Before:**
```
Home
└── Clubs (clubsPage)
    ├── Club 1
    ├── Club 2
    └── ... (500+ clubs)
```

**After:**
```
Home
├── Halland (regionalPage)
│   └── Klubbar (clubsPage)
│       ├── Club 1
│       └── ...
├── Stockholm (regionalPage)
│   └── Klubbar (clubsPage)
│       └── ...
└── ... (24 more regions)
```

**URL Structure:**
- `/halland/` - Regional landing page
- `/halland/klubbar/` - Clubs listing for Halland
- `/halland/klubbar/club-name/` - Individual club page

---

## Pre-Deployment Checklist

- [ ] Backup production database
- [ ] Backup `umbraco/Data/` folder
- [ ] Test all steps in dev environment first
- [ ] Schedule maintenance window (migration takes ~5 minutes)

---

## Step 1: Deploy Code Changes

Deploy the following changed files to production:

### New Files
| File | Description |
|------|-------------|
| `Views/RegionalPage.cshtml` | Regional landing page template |

### Modified Files
| File | Description |
|------|-------------|
| `Services/AdminAuthorizationService.cs` | Regional admin support |
| `Controllers/ClubAdminController.cs` | Migration endpoints + regional club lookup |
| `Views/ClubsPage.cshtml` | Regional context awareness |
| `Views/Master.cshtml` | "Kretsar" navigation dropdown |
| `Models/Federations.cs` | Updated Goteborg enum value |

### Deployment Command
```bash
dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"
```

Upload all files to production. See `PRODUCTION_DEPLOYMENT_GUIDE.md` for full deployment instructions.

---

## Step 2: Create Document Types in Umbraco Backoffice

Login to Umbraco backoffice at `/umbraco`

### 2a. Create `regionalPage` Document Type

**Settings → Document Types → Create → Document Type with Template**

**Name:** `Regional Page`
**Alias:** `regionalPage`
**Icon:** `icon-globe` or `icon-map-marker`

**Properties (Content tab):**

| Alias | Name | Type | Description |
|-------|------|------|-------------|
| `regionCode` | Region Code | Textstring | Enum value (e.g., "Halland") |
| `regionName` | Region Name | Textstring | Full name (e.g., "Hallands Pistolskyttekrets") |
| `welcomeTitle` | Welcome Title | Textstring | Banner title |
| `welcomeText` | Welcome Text | Rich Text Editor | Banner welcome text |
| `aboutRegion` | About Region | Rich Text Editor | About section content |

**Properties (Contact tab):**

| Alias | Name | Type |
|-------|------|------|
| `contactPerson` | Contact Person | Textstring |
| `contactEmail` | Contact Email | Textstring |
| `contactPhone` | Contact Phone | Textstring |

**Properties (Media tab):**

| Alias | Name | Type |
|-------|------|------|
| `bannerImage` | Banner Image | Media Picker |
| `logo` | Logo | Media Picker |

**Structure settings:**
- Template: `RegionalPage`
- Allow at root: **No**
- Allowed children: `clubsPage`

### 2b. Update `clubsPage` Document Type

**Settings → Document Types → clubsPage**

Add new property:

| Alias | Name | Type | Tab |
|-------|------|------|-----|
| `regionCode` | Region Code | Textstring | Settings (or Content) |

### 2c. Update `home` Document Type

**Settings → Document Types → home (or homePage)**

- Go to **Structure** tab
- Add `regionalPage` to **Allowed child node types**

### 2d. Verify Document Type Hierarchy

Ensure allowed children are set correctly:
- `home` → can have child: `regionalPage`
- `regionalPage` → can have child: `clubsPage`
- `clubsPage` → can have child: `club`

---

## Step 3: Run Migration Preview

Login as admin on the site, open browser Developer Tools (F12), go to Console tab.

**Preview what will happen (no changes made):**

```javascript
fetch('/umbraco/surface/ClubAdmin/PreviewRegionalMigration')
  .then(r => r.json())
  .then(data => {
    console.log('=== Migration Preview ===');
    console.log('Existing clubs:', data.existingClubsCount);
    console.log('Can migrate:', data.canMigrate);
    console.log('\nClubs by region:');
    Object.entries(data.clubsByRegion || {}).forEach(([region, clubs]) => {
      console.log(`  ${region || '(no region)'}: ${clubs.length} clubs`);
    });
    if (data.clubsWithoutRegion?.length > 0) {
      console.log('\nWARNING - Clubs without region:');
      data.clubsWithoutRegion.forEach(c => console.log(`  - ${c.name} (ID: ${c.id})`));
    }
  });
```

**Expected output:**
- `canMigrate: true` (no regional pages exist yet)
- All clubs grouped by their `regionalFederation` property
- List of any clubs without a region (these won't be moved automatically)

---

## Step 4: Fix Clubs Without Region (if any)

If the preview shows clubs without a `regionalFederation` value, update them first.

**Example script to set clubs to Halland:**

```javascript
const clubsToFix = [/* array of club IDs from preview */];
const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;

async function fixClubs() {
  for (const clubId of clubsToFix) {
    const getResp = await fetch(`/umbraco/surface/ClubAdmin/GetClub?id=${clubId}`);
    const getData = await getResp.json();
    if (!getData.success) continue;

    const club = getData.data;
    console.log(`Updating ${club.Name} to Halland...`);

    const formData = new URLSearchParams();
    formData.append('__RequestVerificationToken', token);
    formData.append('id', clubId);
    formData.append('name', club.Name);
    formData.append('description', club.Description || '');
    formData.append('contactPerson', club.ContactPerson || '');
    formData.append('contactEmail', club.ContactEmail || '');
    formData.append('contactPhone', club.ContactPhone || '');
    formData.append('webSite', club.WebSite || '');
    formData.append('address', club.Address || '');
    formData.append('city', club.City || '');
    formData.append('postalCode', club.PostalCode || '');
    formData.append('isActive', club.IsActive);
    formData.append('clubIdNumber', club.ClubIdNumber || '');
    formData.append('regionalFederation', 'Halland'); // Set appropriate region

    const saveResp = await fetch('/umbraco/surface/ClubAdmin/SaveClub', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: formData.toString()
    });
    const result = await saveResp.json();
    console.log(`  ${result.success ? '✓' : '✗'} ${result.message}`);
  }
}
fixClubs();
```

---

## Step 5: Run Migration

**Run the actual migration:**

```javascript
const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;

fetch('/umbraco/surface/ClubAdmin/MigrateClubsToRegionalStructure?dryRun=false', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/x-www-form-urlencoded',
    'RequestVerificationToken': token
  },
  body: '__RequestVerificationToken=' + encodeURIComponent(token)
}).then(r => r.json()).then(data => {
  console.log('=== Migration Results ===');
  console.log('Success:', data.success);
  console.log('Message:', data.message);
  data.results?.forEach(r => console.log('  ' + r));
  if (data.errors?.length) {
    console.log('Errors:');
    data.errors.forEach(e => console.log('  ERROR: ' + e));
  }
});
```

**What the migration does:**
1. Creates 26 regional pages under Home (named by enum value, e.g., "Halland")
2. Creates a "Klubbar" (clubsPage) under each regional page
3. Sets `regionCode` and `regionName` properties on each regional page
4. Creates `RegionalAdmin_{regionCode}` member groups
5. Moves clubs to their regional clubsPage based on `regionalFederation` property
6. Publishes all created content

---

## Step 6: Rename Regional Pages (Required)

The migration creates pages with enum names (e.g., "Halland"). These need to match exactly.

**In Umbraco Backoffice:**
1. Go to **Content**
2. For each regional page, verify the name matches the enum value:

| Correct Name | Description |
|--------------|-------------|
| Alvsborg | Älvsborgs Pistolskyttekrets |
| Blekinge | Blekinge Pistolskyttekrets |
| Dalarna | Dalarnas Pistolskyttekrets |
| Gavleborg | Gävleborgs Pistolskyttekrets |
| **Goteborg** | Göteborg och Bohusläns Pistol.Krets |
| Gotland | Gotlands Pistolskyttekrets |
| Halland | Hallands Pistolskyttekrets |
| Jamtland | Jämtlands Läns Pistolskyttekrets |
| Jonkoping | Jönköpings Läns Pistolskyttekrets |
| KalmarNorra | Kalmar Läns Norra Pistolskyttekrets |
| KalmarSodra | Kalmar Läns Södra Pistolskyttekrets |
| Kristianstad | Kristianstads Pistolskyttekrets |
| Kronoberg | Kronobergs Läns Pistolskyttekrets |
| Malmohus | Malmöhus Pistolskyttekrets |
| Norrbotten | Norrbottens Pistolskyttekrets |
| Orebro | Örebro Läns Pistolskyttekrets |
| Ostergotland | Östergötlands Pistolskyttekrets |
| Skaraborg | Skaraborgs Pistolskyttekrets |
| Sodermanland | Södermanlands Pistolskyttekrets |
| Stockholm | Stockholms Pistolskyttekrets |
| Uppsala | Uppsala Läns Pistolskyttekrets |
| Varmland | Värmlands Pistolskyttekrets |
| Vasterbotten | Västerbottens Läns Pistolskyttekrets |
| Vasternorrland | Västernorrlands Läns Pistolskyttekrets |
| VastgotaDal | Västgöta-Dals Pistolskyttekrets |
| Vastmanland | Västmanlands Pistolskyttekrets |

**Note:** We use `Goteborg` (not `GoteborgOchBohuslan`) to keep URLs clean.

---

## Step 7: Verify Migration

### 7a. Check Content Tree
In Umbraco Backoffice → Content:
- [ ] 26 regional pages exist under Home
- [ ] Each regional page has a "Klubbar" child
- [ ] Clubs are under the correct regional "Klubbar" page
- [ ] All content is published (green checkmarks)

### 7b. Test URLs
- [ ] `/halland/` - Shows regional landing page
- [ ] `/halland/klubbar/` - Shows clubs in Halland
- [ ] `/stockholm/klubbar/` - Shows clubs in Stockholm
- [ ] Navigation dropdown shows "Kretsar" with all 26 regions

### 7c. Test Club Counts
Run in browser console:
```javascript
fetch('/umbraco/surface/ClubAdmin/PreviewRegionalMigration')
  .then(r => r.json())
  .then(d => console.log('Total clubs:', d.existingClubsCount));
```
Should show 517 clubs (or your current count).

---

## Step 8: Delete Old Clubs Page

Once verified:
1. In Umbraco Backoffice → Content
2. Find the old root-level "Clubs" page
3. Verify it's empty (no clubs under it)
4. Right-click → Delete

---

## Step 9: Test Regional Admin (Optional)

To test regional admin permissions:
1. Create a test member
2. Assign them to `RegionalAdmin_Halland` group
3. Verify they can access clubs in Halland but not other regions

---

## Rollback Plan

If something goes wrong:

1. **Restore database backup**
2. **Restore `umbraco/Data/` folder**
3. **Revert code changes** (deploy previous version)

The migration is reversible by:
1. Moving clubs back to root-level clubsPage
2. Deleting regional pages
3. Republishing old clubsPage

---

## Troubleshooting

### "Inga klubbar hittades" (No clubs found)
- Code update may be missing - ensure `GetClubsAsContent()` in both `ClubAdminController.cs` and `AdminAuthorizationService.cs` supports the new structure
- Restart application after code deployment

### Regional page returns 404
- Check page is published
- Check page name matches URL (e.g., "Halland" not "Hallands Pistolskyttekrets")
- Check template is assigned (`RegionalPage`)

### Clubs not showing in correct region
- Check club's `regionalFederation` property matches the `regionCode` on the regional page
- Values are case-sensitive

### Navigation dropdown not showing
- Ensure regional pages are published
- Clear browser cache
- Check `Master.cshtml` was deployed

---

## Files Changed Summary

```
src/HpskSite/
├── Controllers/
│   └── ClubAdminController.cs        # Migration endpoints, regional club lookup
├── Models/
│   └── Federations.cs                # Updated Goteborg enum
├── Services/
│   └── AdminAuthorizationService.cs  # Regional admin support
└── Views/
    ├── RegionalPage.cshtml           # NEW - Regional landing page
    ├── ClubsPage.cshtml              # Regional context awareness
    └── Master.cshtml                 # Kretsar navigation dropdown
```

---

## Document Type Summary

### regionalPage (NEW)
- **Template:** RegionalPage
- **Allowed children:** clubsPage
- **Properties:** regionCode, regionName, welcomeTitle, welcomeText, aboutRegion, contactPerson, contactEmail, contactPhone, bannerImage, logo

### clubsPage (UPDATED)
- **New property:** regionCode (Textstring)

### home (UPDATED)
- **New allowed child:** regionalPage

---

**Document Version:** 2026-01-26
**Tested On:** Development environment
**Author:** Claude Code
