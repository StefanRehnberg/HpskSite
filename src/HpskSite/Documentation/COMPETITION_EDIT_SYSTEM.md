# Competition Data Editing - Implementation Summary

## Overview
A type-agnostic, modal-based editing system for competition data that works across all competition types (Precision, RapidFire, etc.) with UX optimized for desktop and mobile devices.

## Architecture

### Files Created/Modified

#### 1. Base Controller
**File:** `HpskSite/Controllers/CompetitionEditController.cs`
- Routes edit requests based on competition type
- Validates competition exists and type is supported
- Delegates to type-specific services for saving
- Returns structured JSON responses

**Endpoint:** `POST /umbraco/surface/CompetitionEdit/SaveCompetition`

**Request Format:**
```json
{
  "competitionId": 2171,
  "competitionType": "Precision",
  "fields": {
    "competitionName": "Updated Name",
    "maxParticipants": 100,
    "isActive": true
  }
}
```

#### 2. Service Interface
**File:** `HpskSite/CompetitionTypes/Common/Interfaces/ICompetitionEditService.cs`
- Defines contract for type-specific edit services
- Includes validation and field definition methods
- Provides structured result classes

**Key Methods:**
- `SaveCompetitionAsync()` - Save to Umbraco
- `ValidateFields()` - Type-specific validation
- `GetEditableFields()` - Returns field definitions for UI

#### 3. Precision Implementation
**File:** `HpskSite/CompetitionTypes/Precision/Services/PrecisionCompetitionEditService.cs`
- Implements `ICompetitionEditService` for Precision competitions
- Validates all Precision-specific fields
- Handles Umbraco content updates
- Includes field definitions for form rendering

**Supported Fields:**
- Basic: competitionName, description, venue
- Dates: competitionDate, competitionEndDate
- Registration: registrationOpenDate, registrationCloseDate, maxParticipants, registrationFee
- Contact: competitionDirector, contactEmail, contactPhone
- Config: numberOfSeriesOrStations, showLiveResults, isActive

#### 4. UI Modal
**File:** `HpskSite/Views/Partials/CompetitionEditModal.cshtml`
- Bootstrap modal with form fields organized by sections
- Real-time form validation
- Error display with field-specific messages
- Loading state during save
- Auto-reload on successful save

**Integration:** Added to `CompetitionManagement.cshtml` as a partial view

**Modal Trigger:** "Redigera" button in competition details card header

## Data Flow

```
1. User clicks "Redigera" button in competition details card
                           ↓
2. Modal opens and loads edit form with current values
                           ↓
3. User modifies fields and clicks "Spara ändringar"
                           ↓
4. Client-side validation runs
                           ↓
5. POST /umbraco/surface/CompetitionEdit/SaveCompetition
                           ↓
6. CompetitionEditController routes to PrecisionCompetitionEditService
                           ↓
7. Service validates all fields
                           ↓
8. Service updates Umbraco content via ContentService
                           ↓
9. Returns success/error response
                           ↓
10. Modal shows errors or reloads page on success
```

## Adding Edit Support for a New Competition Type

1. Create `[Type]CompetitionEditService.cs` in `CompetitionTypes/[Type]/Services/`
2. Implement `ICompetitionEditService` interface:
   ```csharp
   public class RapidFireCompetitionEditService : ICompetitionEditService
   {
       public async Task<CompetitionEditResult> SaveCompetitionAsync(int competitionId, Dictionary<string, object> fields)
       { ... }
       
       public ValidationResult ValidateFields(Dictionary<string, object> fields)
       { ... }
       
       public List<EditableFieldDefinition> GetEditableFields()
       { ... }
   }
   ```
3. Add case to `RouteToTypeSpecificSave()` in `CompetitionEditController`
4. Inject the service and call it

## Validation

The system performs two-level validation:

**Client-Side:**
- HTML5 form validation (required, email, min/max)
- Runs before API call

**Server-Side:**
- Type-specific business logic validation
- Field format validation (email, phone, dates)
- Range validation (positive integers, decimal values)
- Returns detailed error messages per field

## Error Handling

Errors are displayed in the modal with:
- Clear error message header
- Bullet list of validation errors
- Field-specific error descriptions
- User can fix and retry

## UX Considerations

✅ **Mobile-Optimized**
- Full-screen modal on small screens
- Touch-friendly form controls
- Proper keyboard handling
- Responsive layout with proper spacing

✅ **Clear User Intent**
- Dedicated edit modal vs. inline editing
- Obvious save/cancel actions
- Loading states during save
- Success/error feedback

✅ **Extensible**
- Easy to add new competition types
- Fields organized by sections
- Reusable validation framework
- Clear separation of concerns

✅ **Accessible**
- ARIA labels and descriptions
- Keyboard navigation
- Error messages linked to fields
- Semantic HTML structure

## Competition Admin System (NEW - Session 2)

A complete management interface for admins to create, copy, and delete competitions from a centralized list.

### Files Created/Modified

#### 1. Admin Controller
**File:** `HpskSite/Controllers/CompetitionAdminController.cs`

**Endpoints:**
- `GET /umbraco/surface/CompetitionAdmin/GetCompetitionsList` - Returns all competitions with metadata
- `POST /umbraco/surface/CompetitionAdmin/CreateCompetition` - Creates new competition
- `POST /umbraco/surface/CompetitionAdmin/CopyCompetition` - Clones existing competition with +1 year dates
- `POST /umbraco/surface/CompetitionAdmin/DeleteCompetition` - Removes competition (blocks if registrations exist)

**Response Format:**
```json
{
  "success": true,
  "data": [
    {
      "id": 2171,
      "name": "Spring Championship",
      "type": "Precision",
      "startDate": "2024-05-15T00:00:00",
      "status": "Scheduled",
      "registrationCount": 12
    }
  ]
}
```

**Features:**
- Admin-only access via `IsCurrentUserAdminAsync()`
- Blocks deletion of competitions with registrations
- Auto-copies all properties when cloning
- Date auto-increment by 1 year for all date fields
- Status detection: Draft/Scheduled/Active/Completed

#### 2. UI Components

**Main List:** `HpskSite/Views/Partials/AdminCompetitionsList.cshtml`
- Table showing: Name, Type, Start Date, Status, Registrations, Actions
- "New Competition" button to create
- Action buttons: View Details (link), Copy, Delete
- Auto-loads on page load via AJAX
- Handles loading states and error display
- Empty state messaging

**Create/Copy Modal:** `HpskSite/Views/Partials/CompetitionCreateModal.cshtml`
- Reusable modal for both create and copy operations
- Fields: Name, Type (dropdown), Start Date, Max Participants
- Mode detection: Shows "Copy Mode" alert banner when cloning
- Client-side validation before submit
- Auto-reset on close or fresh open
- Handles both endpoints seamlessly

**Delete Modal:** `HpskSite/Views/Partials/CompetitionDeleteConfirmModal.cshtml`
- Confirmation dialog with warning banner
- Displays competition name being deleted
- Shows helpful error if deletion blocked (e.g., has registrations)
- Prevents accidental deletion

#### 3. Admin Page Integration
**File:** `HpskSite/Views/AdminPage.cshtml` (modified)

Changes:
- Added "Competitions" as the **first/default tab** (makes it the primary admin function)
- Includes `AdminCompetitionsList` partial in the Competitions tab
- Added modal includes at bottom: `CompetitionCreateModal` and `CompetitionDeleteConfirmModal`
- Both modals are globally available across all admin tabs

### Data Flow - Admin Operations

**Create Competition:**
```
1. User clicks "New Competition" button
   ↓
2. Modal opens with blank form
   ↓
3. User fills: Name, Type, Start Date (optional), Max Participants (optional)
   ↓
4. Click "Create" → POST CreateCompetition
   ↓
5. Controller creates new IContent under competitionsContainer
   ↓
6. Sets properties and publishes
   ↓
7. Modal closes, list reloads with new competition
```

**Copy Competition:**
```
1. User clicks "Copy" button next to competition
   ↓
2. Modal opens in copy mode (shows alert banner)
   ↓
3. User can modify form or just click "Copy"
   ↓
4. Click "Copy" → POST CopyCompetition with sourceCompetitionId
   ↓
5. Controller clones all properties, increments all dates by 1 year
   ↓
6. Updates name with year suffix
   ↓
7. Creates and publishes new competition
   ↓
8. Modal closes, list reloads with copied competition
```

**Delete Competition:**
```
1. User clicks "Delete" button
   ↓
2. Confirmation modal shows competition name
   ↓
3. User clicks "Delete" to confirm
   ↓
4. POST DeleteCompetition
   ↓
5. Controller checks for registrations
   ↓
6. If registrations exist → Shows error, blocks deletion
   ↓
7. If safe → Unpublishes and deletes content
   ↓
8. Modal closes, list reloads
```

### Security

✅ **Admin-Only Access** - All endpoints require "Administrators" role
✅ **Anti-Forgery Tokens** - Required for all POST requests
✅ **Member Service Validation** - Checks member roles via service

### Error Handling

- **Missing Container** - Graceful error if competitionsContainer not found
- **Invalid Content Type** - Validates competition is actual "competition" type
- **Registrations Exist** - Prevents orphaned data, shows count and helpful message
- **Network Errors** - User-friendly error messages with retry option
- **Validation Errors** - Returns field-level validation feedback

### Status Auto-Detection

Competitions automatically marked as:
- **Draft** - No start date set
- **Scheduled** - Start date is in the future
- **Active** - Between start and end date
- **Completed** - End date has passed

### Integration with Edit System

The admin list and edit modal work together:
1. Admin sees competition in list with status/registration count
2. Clicks "View Details" link → navigates to `/competitionmanagement?competitionId=X`
3. Existing edit modal opens for detailed editing
4. After save, can return to admin list to see copy/delete options

## Future Enhancements

1. **Dynamic Field Loading** - Load field definitions from `GetEditableFields()` instead of hardcoding
2. **Field-Level Permissions** - Some users can edit certain fields but not others
3. **Edit History** - Track who changed what and when
4. **Concurrent Edit Detection** - Warn if another user is editing the same competition
5. **Type-Specific Validation Rules** - Show validation rules dynamically based on field definitions
6. **Draft Saving** - Auto-save drafts while editing
7. **Bulk Operations** - Select multiple competitions, delete/copy multiple at once
8. **Advanced Filtering** - Filter by status, type, date range
9. **Search** - Search competitions by name
10. **Export** - Export competitions list to CSV

## Testing

### Manual Testing Checklist

- [ ] Load form and verify current values appear
- [ ] Modify a field and save successfully
- [ ] Verify data persists after page reload
- [ ] Try invalid email and verify error shows
- [ ] Try negative number for required numeric field
- [ ] Test on mobile device - modal displays correctly
- [ ] Cancel button closes modal without saving
- [ ] Network error - retry functionality

### Unit Tests Needed

- PrecisionCompetitionEditService.ValidateFields()
- Each validation helper method
- Field type conversion logic
- Umbraco content update logic

## Notes

- All dates/times are handled as ISO 8601 format in transit
- Boolean fields use string values in HTML forms ("true"/"false") and are converted server-side
- The system reads current values from the Razor view to populate the form
- Future iterations should fetch field definitions from the service for dynamic forms
