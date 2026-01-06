# Login & Registration System

Complete documentation for the member authentication, registration, and approval system with email notifications.

## Overview

Comprehensive member authentication and registration system featuring:
- Smart login with redirect to previous page
- Enhanced error messages for login failures
- Member registration with approval workflow
- Email notification service (5 email templates)
- Missing club request feature
- Unified approval system

**Last Updated:** 2025-11-03

---

## Login System

### Location

`/login-register` page with tabbed interface

**View Files:**
- `Views/MemberLoginPage.cshtml` - Main page with tabs
- `Views/Partials/Login.cshtml` - Login form partial
- `Views/Partials/Register.cshtml` - Registration form partial

### Features

#### 1. Smart Redirect (Fixed 2025-11-02)

**Functionality:**
- Redirects to previous page after successful login
- Falls back to home page if previous page was login page
- Supports `returnUrl` query parameter
- Implementation: Uses HTTP Referer header

**Code Location:** `Views/Partials/Login.cshtml`

#### 2. Enhanced Error Messages (Added 2025-11-02)

JavaScript-enhanced error display with Bootstrap alerts. Specific messages for:

- **Invalid credentials:** "Inloggningen misslyckades. Kontrollera att e-postadress och lösenord är korrekta."
- **Email not found:** "E-postadressen hittades inte. Kontrollera att du har registrerat dig."
- **Pending approval:** "Ditt konto väntar på godkännande från en klubbadministratör. Du kommer att meddelas via e-post när ditt konto har godkänts."
- **Locked account:** "Ditt konto har låsts. Kontakta administratören för hjälp."

Gracefully handles Umbraco's built-in validation messages.

#### 3. Authentication

- Uses Umbraco's built-in `UmbLoginController`
- Cookie-based session management
- Remember Me checkbox
- Support for 2FA (if enabled)
- External login providers (optional)

---

## Registration System

### Controller

`MemberController.RegisterMember()` (Lines 287-387)

**Endpoint:** `POST /umbraco/surface/Member/RegisterMember`

### Features

#### 1. Explanatory Text (Added 2025-11-02)

Blue info alert at top explaining approval process:

> "Efter registrering måste en administratör för den valda klubben godkänna ditt medlemskap innan du kan logga in. Du kommer att meddelas via e-post när ditt konto har godkänts."

#### 2. Club Selection

- Dropdown populated from active clubs (Document Type nodes)
- Uses `ClubAdminController.GetClubsForRegistration()` API
- **Bug Fix (2025-11-02):** Now uses `IContentService` instead of obsolete `IMemberService` for club lookup

**Fixed Code (MemberController.cs lines 337-357):**
```csharp
// Use IContentService to find club Document Type nodes
var clubsRoot = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
if (clubsRoot != null)
{
    var clubNode = _contentService.GetPagedChildren(clubsRoot.Id, 0, int.MaxValue, out _)
        .FirstOrDefault(c => c.Id == primaryClubId && c.ContentType.Alias == "club");

    if (clubNode != null)
    {
        clubName = clubNode.Name;
    }
}
```

#### 3. Missing Club Request (Added 2025-11-02)

**Feature:** "Saknas din klubb i listan?" link below dropdown

Opens modal: `Views/Partials/RequestMissingClubModal.cshtml`

**Form Fields:**
- Club Name* (required)
- Location
- Contact Person
- Requestor Email* (required, validated)
- Notes (textarea)
- SPSF contact requirement explanation included

**Sends email to site administrator**

**Endpoint:** `MemberController.RequestMissingClub()`

#### 4. Registration Flow

1. **Validation:**
   - First name, last name, email, password required
   - Password minimum 6 characters
   - Password confirmation required
   - Email uniqueness check

2. **Member Creation:**
   - Creates member with `IsApproved = false`
   - Assigns to "PendingApproval" member group
   - Member cannot login until approved

3. **Email Notifications:**
   - Confirmation email sent to user
   - Notification email sent to admin
   - Graceful failure handling (registration succeeds even if email fails)

---

## Email Service ✅ (2025-11-02)

### Service Registration

**Service:** `Services/EmailService.cs`
**Composer:** `Services/EmailServiceComposer.cs` (registered as singleton)

### Configuration

`appsettings.json` → Email section

```json
{
  "Email": {
    "SmtpHost": "your-smtp-server.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "Username": "your-email@domain.com",
    "Password": "your-password",
    "FromAddress": "noreply@hpsk.se",
    "FromName": "HPSK Site",
    "AdminEmail": "admin@hpsk.se",
    "SiteUrl": "https://yourdomain.com"
  }
}
```

### Email Templates (All in Swedish)

#### 1. SendRegistrationConfirmationToUserAsync()

**Sent:** When user completes registration
**To:** New member
**Subject:** "Välkommen till HPSK - Registrering mottagen"
**Content:** Welcome message, approval process explanation, next steps

#### 2. SendRegistrationNotificationToAdminAsync()

**Sent:** When user completes registration
**To:** Site administrator (`AdminEmail`)
**Subject:** "Ny medlemsregistrering: [Member Name]"
**Content:** Member details, club selection, link to admin panel

#### 3. SendApprovalNotificationAsync()

**Sent:** When admin changes `IsApproved` from false to true
**To:** Approved member
**Subject:** "Ditt HPSK-konto har godkänts!"
**Content:** Congratulations, login instructions, feature list, login link

**Implementation:** `MemberAdminController.SaveMember()` (Lines 241-271)

#### 4. SendRejectionNotificationAsync()

**Sent:** When admin rejects registration (optional - not yet implemented in UI)
**To:** Rejected member
**Subject:** "Angående din HPSK-registrering"
**Content:** Rejection notice, optional reason, contact info

#### 5. SendMissingClubRequestAsync()

**Sent:** When user requests missing club via modal
**To:** Site administrator
**Subject:** "Förfrågan om att lägga till klubb: [Club Name]"
**Content:** Club details, requestor info, SPSF contact reminder

### Error Handling

- Graceful degradation: Email failures won't block registration or approval
- Logging: Errors logged via `ILogger<EmailService>`
- Validation: SMTP config checked before attempting send

---

## Approval Workflow

### Admin Interface

**Location:** Admin Page → Users tab → "Väntande godkännanden" section
**View:** `Views/Partials/UserManagement.cshtml`

### API Endpoints

#### Get Pending Approvals
`MemberAdminController.GetPendingApprovals()`
- Returns members with `IsApproved = false`

#### Approve Member (Unified System - 2025-11-03)
`MemberAdminController.SaveMember()`
- Sets `IsApproved = true`
- Assigns to "Users" group
- Removes from "PendingApproval" group
- **Automatically sends approval email**

### Email Integration

When admin approves member (changes `IsApproved` from false to true), approval email is automatically sent to member with login instructions.

### Unified Approval System (2025-11-03)

**Obsolete Endpoints Removed:**
- `ApproveMember()` - Replaced by SaveMember with approval detection
- `RejectMember()` - Future implementation

**New Pattern:**
SaveMember detects approval state change and triggers email automatically:

```csharp
// In MemberAdminController.SaveMember() (lines 241-271)
bool wasApproved = existingMember.IsApproved;
bool isNowApproved = model.IsApproved;

if (!wasApproved && isNowApproved)
{
    // Send approval email
    await _emailService.SendApprovalNotificationAsync(
        existingMember.Email,
        $"{existingMember.GetValue<string>("firstName")} {existingMember.GetValue<string>("lastName")}"
    );
}
```

---

## Authorization Pattern (3-Tier System)

Most endpoints follow a three-tier authorization pattern:

```csharp
// Check Site Admin first (has access to everything)
bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

// Check Competition Manager (for competition-specific endpoints)
bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

// Check Club Admin (for club-scoped access)
bool isClubAdmin = false;
var competitionClubId = competition.Value<int>("clubId");
if (competitionClubId > 0)
{
    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
}

// Grant access if ANY role applies
if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin)
{
    return Json(new { success = false, message = "Access denied" });
}
```

---

## Security Features

### Password Requirements
- Minimum 6 characters (configurable in `appsettings.json`)
- No special character requirements by default
- Can be enhanced via Umbraco security settings

### Authorization
- Anti-forgery tokens on all POST requests
- Registration rate limiting (Umbraco default)
- Email validation
- Member cannot login until approved by admin

### Data Protection
- Passwords hashed via Umbraco Identity
- Email used as username (unique constraint)
- Club references by ID (not name) for data integrity

---

## Bug Fixes (2025-11-02)

### 1. Club Lookup Bug (MemberController.RegisterMember)

**Issue:** Used `IMemberService` to find clubs (obsolete pattern from when clubs were members)

**Fix:** Now uses `IContentService` to find club Document Type nodes (Lines 337-357)

**Impact:** Club assignment during registration now works correctly

### 2. Login URL Fix (TrainingStairs.cshtml)

**Issue:** Login button linked to `/login` (non-existent page)

**Fix:** Changed to `/login-register` (Line 40)

**Impact:** Login button on training page now works

### 3. Razor Parsing Error (RequestMissingClubModal.cshtml)

**Issue:** JavaScript regex pattern `/@/` parsed as Razor code

**Fix:** Escaped `@` symbols with `@@` (Line 78)

**Impact:** Modal renders without errors

---

## Files Modified/Created (2025-11-02)

### Created
- `Services/EmailService.cs` - Email notification service
- `Services/EmailServiceComposer.cs` - DI registration
- `Models/MissingClubRequest.cs` - Missing club request model
- `Views/Partials/RequestMissingClubModal.cshtml` - Missing club modal

### Modified
- `Views/Partials/Login.cshtml` - Redirect logic + error handling
- `Views/Partials/Register.cshtml` - Explanatory text + missing club link
- `Views/TrainingStairs.cshtml` - Fixed login URL
- `Controllers/MemberController.cs` - Email service injection, club lookup fix, missing club endpoint
- `Controllers/MemberAdminController.cs` - Email service injection, approval email
- `appsettings.json` - Added Email configuration section

---

## Testing Checklist

- [x] Login redirect to previous page works
- [x] Login redirect to home as fallback works
- [x] Failed login shows enhanced error messages
- [x] Pending approval login shows special message
- [x] Registration shows approval explanation alert
- [x] Missing club link opens modal
- [x] Missing club request sends email (if SMTP configured)
- [x] Registration sends confirmation email to user (if SMTP configured)
- [x] Registration sends notification email to admin (if SMTP configured)
- [x] Approval sends email to member (if SMTP configured)
- [x] Club assignment during registration works
- [x] Training page login button works

---

## Common Pitfalls

1. ❌ **Don't use IMemberService for club lookups** - Clubs are Document Type nodes, not members
2. ❌ **Don't hardcode login URLs** - Always use `/login-register` (not `/login` or `/member-login`)
3. ❌ **Don't forget to escape `@` in JavaScript** - Use `@@` in Razor views for email regex patterns
4. ✅ **Always inject EmailService** in controllers that need notifications
5. ✅ **Configure SMTP settings** before testing email features
6. ✅ **Test with actual member accounts** to verify approval workflow

---

## Future Enhancements

- [ ] Password reset functionality
- [ ] Email verification during registration
- [ ] Social login providers (Google, Facebook)
- [ ] Two-factor authentication (2FA)
- [ ] Member self-service profile updates
- [ ] Rejection reason UI for admins

---

**Implementation Status:** ✅ Complete
**Last Tested:** 2025-11-03
**Build Status:** ✅ 0 errors
