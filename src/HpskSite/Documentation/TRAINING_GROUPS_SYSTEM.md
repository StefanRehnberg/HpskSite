# Training Groups System (2026-02)

This document describes the Training Groups system, which allows clubs to organize members into training groups with assigned trainers who can approve training steps on Skyttetrappan.

---

## Overview

Training groups are club-scoped collections of members and trainers. They provide a structured way for trainers to track and approve members' progress through the Skyttetrappan training levels.

**Key design principle:** Training progress is stored on member properties (not on the training group). Closing a training group preserves all member progress.

---

## Database Schema

Two tables in the database (see `Scripts/CreateTrainingGroupTables.sql`):

### TrainingGroups
| Column | Type | Description |
|--------|------|-------------|
| Id | int (PK, identity) | Auto-generated ID |
| Name | nvarchar(200) | Group name |
| ClubId | int | Club this group belongs to |
| Description | nvarchar(max) | Optional description |
| StartDate | datetime | When the group started |
| IsActive | bit | Whether the group is active |
| CreatedDate | datetime | When the group was created |
| CreatedByMemberId | int | Member who created the group |

### TrainingGroupMembers
| Column | Type | Description |
|--------|------|-------------|
| Id | int (PK, identity) | Auto-generated ID |
| TrainingGroupId | int (FK) | Reference to TrainingGroups.Id |
| MemberId | int | Umbraco member ID |
| Role | nvarchar(50) | `"Member"` or `"Trainer"` |
| JoinedDate | datetime | When the member was added |
| AddedByMemberId | int, nullable | Who added this member |
| IsActive | bit | Soft delete flag |

---

## Authorization & Roles

### Permission Hierarchy for Training Group Management

Who can manage (create, edit, add members to) a training group:

1. **Site Admin** - Can manage any training group
2. **Club Admin** - Can manage training groups for their club
3. **Skjutledare** - Can manage training groups for their club
4. **Trainer** - Can manage the training group they are a Trainer in

This is implemented in `TrainingGroupService.CanManageTrainingGroup()`.

### Permission Hierarchy for Training Step Approval

Who can approve a training step for a member (CompleteStep in TrainingController):

1. **Site Admin** - Can approve any member
2. **Trainer** - Can approve members in their active training group
3. **Skjutledare** - Can approve members at their club (even without an active training group)
4. **Club Admin** - Can approve members at their club

### Skjutledare (Range Master) Role

The Skjutledare role was added in 2026-02. It is a club-level trust role stored as Umbraco member group `Skjutledare_{ClubId}`.

**Capabilities:**
- Approve training steps for any member of their club
- Create and manage training groups for their club
- View and manage competitions for their club
- View competition registrations for their club

**Limitations (compared to Club Admin):**
- Cannot approve member applications
- Cannot edit club settings or information
- Cannot assign other admins or Skjutledare

**Management:** Skjutledare are assigned/removed by Club Admins via the Members tab on the club admin page.

**AdminAuthorizationService methods:**
- `IsSkjutledareForClub(clubId)` - Check current user
- `IsMemberSkjutledareForClub(memberId, clubId)` - Check specific member
- `IsSkjutledareForMember(memberId)` - Check if Skjutledare for member's club(s)
- `GetSkjutledareClubIds()` - Get all clubs where user is Skjutledare
- `EnsureSkjutledareGroup(clubId)` - Create member group if missing

---

## Backend Architecture

### Service: TrainingGroupService.cs

**Constructor dependencies:** `IUmbracoDatabaseFactory`, `IMemberService`, `ClubService`, `AdminAuthorizationService`, `IMemberManager`

**Key methods:**

| Method | Description |
|--------|-------------|
| `GetTrainingGroupsForClub(clubId, includeInactive)` | Get all groups for a club with member/trainer counts |
| `GetTrainingGroupsForMember(memberId)` | Get groups where member is active, with trainer names |
| `GetTrainingGroup(trainingGroupId)` | Get single group with all active members |
| `CreateTrainingGroup(name, clubId, description, startDate, createdByMemberId)` | Create new group |
| `UpdateTrainingGroup(id, name, description, startDate, isActive)` | Update group (optionally toggle active state) |
| `DeactivateTrainingGroup(trainingGroupId)` | Set IsActive = false |
| `AddTrainingGroupMember(groupId, memberId, role, addedByMemberId)` | Add or reactivate member |
| `RemoveTrainingGroupMember(groupId, memberId)` | Soft delete (IsActive = false) |
| `SetTrainingGroupMemberRole(groupId, memberId, role)` | Change between "Member" and "Trainer" |
| `IsTrainerForMember(trainerMemberId, targetMemberId)` | Check if trainer relationship exists in active group |
| `CanManageTrainingGroup(trainingGroupId)` | Four-tier authorization check |
| `GetTrainingGroupClubId(trainingGroupId)` | Get club ID for a group |
| `GetAllTrainingGroups(regionFilter, includeInactive)` | Get all groups (admin view) |

### Controller: TrainingGroupController.cs

**Route:** `/umbraco/surface/TrainingGroup/`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GetTrainingGroups` | GET | Get groups the current user can see |
| `GetMyTrainingGroups` | GET | Get groups for the logged-in member |
| `GetTrainingGroup?id=X` | GET | Get group details with members |
| `CreateTrainingGroup` | POST | Create a new group |
| `UpdateTrainingGroup` | POST | Update group details (including isActive) |
| `DeleteTrainingGroup` | POST | Deactivate a group |
| `AddMember` | POST | Add member to group |
| `RemoveMember` | POST | Remove member from group |
| `SetMemberRole` | POST | Change member's role |
| `SearchMembers` | GET | Search members for adding to group |
| `SendGroupEmail` | POST | Send email to group members |

### Training Step Approval (TrainingController.cs)

The `CompleteStep` and `GetMemberProgress` endpoints in TrainingController use the four-tier auth pattern described above. When a `memberId` parameter is provided (viewing/approving another member's progress), the system checks Site Admin > Trainer > Skjutledare > Club Admin.

The `GetTrainingAdminStatus` endpoint returns the current user's role information to the frontend, including:
- `isAdmin` (site admin)
- `managedClubIds` (club admin clubs)
- `isSkjutledare`
- `skjutledareClubIds`

---

## UI Locations

### 1. Skyttetrappan (/skyttetrappan/) - TrainingStairs.cshtml

**"Min Traningsgrupp" tab** (visible to members in a training group):
- Shows the member's training group with member list and progress
- Trainers see "Godkann" (approve) and "Visa framsteg" (view progress) buttons per member
- "Visa framsteg" opens a detailed view of all levels/steps with approve buttons for incomplete steps
- Group email feature for trainers to message all group members

**"Administration" tab** (visible to admins, club admins, skjutledare):
- Create new training groups with club selection
- Expand groups to see members and manage them
- Add/remove members, toggle trainer role
- Edit group details (name, description, start date, active state)

### 2. Club Admin Panel - ClubAdminPanel.cshtml

**"Traningsgrupper" tab** (on the club admin page):
- Same functionality as the Administration tab on Skyttetrappan, but scoped to the current club
- Create/edit/deactivate training groups
- Manage members and trainers
- Inactive groups shown with "Inaktiv" badge and muted styling

**"Medlemmar" tab** (Members tab):
- Skjutledare management section: view current Skjutledare, assign new ones, remove existing

---

## Group Lifecycle

1. **Create** - Admin/Skjutledare creates group with name, club, description, start date
2. **Add members** - Search and add club members; optionally send welcome email
3. **Assign trainers** - Toggle member role to "Trainer"
4. **Active use** - Trainers approve steps, view member progress, send group emails
5. **Deactivate** - Set IsActive = false via edit modal; group no longer appears in member views
   - `IsTrainerForMember` returns false for deactivated groups
   - Skjutledare and Club Admins can still approve steps (not dependent on active group)
   - All member training progress is preserved (stored on member properties)

---

## Registration

`TrainingGroupService` is registered as a transient service in `AdminServicesComposer.cs`:

```csharp
builder.Services.AddTransient<TrainingGroupService>();
```

---

## Files

| File | Purpose |
|------|---------|
| `Models/TrainingGroup.cs` | Training group model |
| `Models/TrainingGroupMember.cs` | Training group member model |
| `Services/TrainingGroupService.cs` | Business logic and data access |
| `Controllers/TrainingGroupController.cs` | API endpoints |
| `Controllers/TrainingController.cs` | Step approval with trainer/skjutledare auth |
| `Controllers/ClubAdminController.cs` | Skjutledare assignment endpoints |
| `Services/AdminAuthorizationService.cs` | Skjutledare authorization methods |
| `Views/TrainingStairs.cshtml` | Training group UI (member + admin views) |
| `Views/Partials/ClubAdminPanel.cshtml` | Club admin training group + Skjutledare management |
| `Scripts/CreateTrainingGroupTables.sql` | Database table creation script |
| `Scripts/CreateTrainingGroupTables_Rollback.sql` | Database rollback script |

---

**Created:** 2026-02
**Status:** Complete
