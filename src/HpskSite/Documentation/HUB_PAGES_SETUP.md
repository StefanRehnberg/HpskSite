# Competition Hub Pages Setup

## Overview
This creates organized hub pages under each competition to better organize start lists and registrations in the Umbraco backoffice.

## Required Document Types

### 1. Competition Start Lists Hub

**Settings → Document Types → Create Document Type**

- **Name:** Competition Start Lists Hub
- **Alias:** `competitionStartListsHub`
- **Icon:** icon-folder (or icon-list)
- **Allow as Root:** No
- **Allow at Root:** No

**Properties Tab: "Hub Settings"**
| Property Name | Alias | Data Type | Description |
|---------------|-------|-----------|-------------|
| Description | `description` | Textarea | Hub description |

**Structure Tab:**
- **Allowed Child Content Types:** Precision Start List
- **Allowed Parent Content Types:** Competition

**Template:**
- **Create Template:** Yes → CompetitionStartListsHub.cshtml
- **Master Template:** master

### 2. Competition Registrations Hub

**Settings → Document Types → Create Document Type**

- **Name:** Competition Registrations Hub
- **Alias:** `competitionRegistrationsHub`
- **Icon:** icon-folder (or icon-users)
- **Allow as Root:** No
- **Allow at Root:** No

**Properties Tab: "Hub Settings"**
| Property Name | Alias | Data Type | Description |
|---------------|-------|-----------|-------------|
| Description | `description` | Textarea | Hub description |
| Registration Deadline | `registrationDeadline` | Date Picker | When registrations close |
| Max Participants | `maxParticipants` | Numeric | Maximum number of participants |

**Structure Tab:**
- **Allowed Child Content Types:** Competition Registration
- **Allowed Parent Content Types:** Competition

**Template:**
- **Create Template:** Yes → CompetitionRegistrationsHub.cshtml
- **Master Template:** master

### 3. Competition Registration (Individual)

**Settings → Document Types → Create Document Type**

- **Name:** Competition Registration
- **Alias:** `competitionRegistration`
- **Icon:** icon-user
- **Allow as Root:** No
- **Allow at Root:** No

**Properties Tab: "Registration Details"**
| Property Name | Alias | Data Type | Values/Description |
|---------------|-------|-----------|-------------------|
| Member ID | `memberId` | Numeric | Links to member |
| Member Name | `memberName` | Textbox | For display purposes |
| Member Email | `memberEmail` | Email Address | Contact info |
| Registration Date | `registrationDate` | Date Picker | When registered |
| Weapon Classes | `weaponClasses` | Checkboxlist | A1,A2,A3,B1,B2,B3,C1,C2,C3,CVÄ,CD3,CJun |
| Start Preference | `startPreference` | Dropdown | Early,Late,Ingen preferens |
| Notes | `notes` | Textarea | Additional notes |
| Is Active | `isActive` | True/False | Active registration |

**Structure Tab:**
- **Allowed Child Content Types:** None
- **Allowed Parent Content Types:** Competition Registrations Hub

**Template:**
- **Create Template:** Yes → CompetitionRegistration.cshtml
- **Master Template:** master

## Benefits of This Structure

### ✅ **Organized Backoffice**
```
📁 Spring Championship 2024
├── 📄 Startlistor
│   ├── 📄 Startlista - Mixed Teams (2024-01-15 09:00)
│   └── 📄 Startlista - Separated Classes (2024-01-15 14:00)
├── 📄 Anmälningar
│   ├── 📄 Anmälan - Erik Andersson
│   └── 📄 Anmälan - Maria Johansson
└── 📄 Results (existing)
```

### ✅ **Improved UX**
- **Clear Navigation**: Admins know where to find things
- **Permissions**: Can set different access levels for hubs
- **Bulk Operations**: Easier to manage all registrations/start lists
- **Reporting**: Hub pages can show summaries and statistics

### ✅ **Future Features**
- Registration hub can show participant counts, deadlines
- Start lists hub can show generation history
- Easy to add approval workflows
- Can create overview dashboards on hub pages

## Implementation Status

- ✅ **StartListController updated** - Auto-creates hub structure
- ✅ **Graceful fallback** - Uses contentPage if hub types don't exist
- ⏳ **Document types** - Need to be created in Umbraco backoffice
- ⏳ **Templates** - Need to be created for hub pages
- ⏳ **Registration system** - Update to use hub structure

## Next Steps

1. **Create the document types** in Umbraco backoffice using specs above
2. **Test start list generation** - should auto-create "Startlistor" hub
3. **Create templates** for hub pages with nice overview interfaces
4. **Update registration system** to use hub structure
5. **Add bulk management features** to hub templates

This creates a much more professional and organized content structure! 🏗️