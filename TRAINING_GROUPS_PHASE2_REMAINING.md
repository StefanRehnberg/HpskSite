# Training Groups Phase 2 -- Remaining Features

## Status: Completed So Far

### Already Implemented
1. **GetMemberProgress auth expansion** (`TrainingController.cs`) -- Trainers and club admins can now view member progress, not just site admins
2. **Trainer step-by-step approval** (`TrainingStairs.cshtml`) -- "Visa framsteg" button per member in training group view; `viewMemberProgressWithApproval()` with accordion of all levels/steps with approve buttons; refresh after approval
3. **ClubAdminPanel training groups tab** (`ClubAdminPanel.cshtml`) -- Full CRUD: create/edit/deactivate groups, add/remove members, toggle roles, member search scoped to club
4. **UserProfile training group badge** (`UserProfile.cshtml`) -- Info card on dashboard showing group name(s), trainer name(s), link to Skyttetrappan
5. **Add-member modal fix** (`TrainingStairs.cshtml`) -- Admin tab add-member modal now uses select-then-confirm flow with explicit "Lägg till" button

---

## Remaining Features to Implement

### 1. Email: Member Added to Training Group

**File:** `src/HpskSite/Services/EmailService.cs`

Add new method `SendTrainingGroupMemberAddedAsync`:
- Parameters: `memberEmail`, `memberName`, `groupName`, `trainerNames`, `startDate`, `clubName`
- Swedish HTML email template following existing patterns (inline styles, responsive)
- Content: Welcome message, group name, trainer name(s), start date, link to Skyttetrappan

**File:** `src/HpskSite/Controllers/TrainingGroupController.cs`

In `AddTrainingGroupMember` endpoint (line ~224):
- Inject `EmailService` in constructor
- After successful `_trainingGroupService.AddTrainingGroupMember()`, look up the member's email
- Fetch trainer names from the group
- Call `SendTrainingGroupMemberAddedAsync`
- Email failure should not block the add operation (wrap in try/catch, log warning)

### 2. Email: Step Approved Notification

**File:** `src/HpskSite/Services/EmailService.cs`

Add new method `SendTrainingStepApprovedAsync`:
- Parameters: `memberEmail`, `memberName`, `levelName`, `levelBadge`, `stepNumber`, `stepDescription`, `approverName`
- Content: Congratulations, which step was approved, who approved it, current progress summary

**File:** `src/HpskSite/Controllers/TrainingController.cs`

In `CompleteStep` endpoint (line ~246):
- `EmailService` is NOT currently injected -- add it to constructor
- After successful step completion (after `_memberService.Save(member)`), look up member email
- Get level/step details from `TrainingDefinitions`
- Call `SendTrainingStepApprovedAsync`
- Email failure should not block the completion

### 3. Group Messaging (Trainer to Members)

**File:** `src/HpskSite/Services/EmailService.cs`

Add new method `SendTrainingGroupMessageAsync`:
- Parameters: `recipientEmail`, `recipientName`, `senderName`, `groupName`, `subject`, `messageBody`
- Content: Message from trainer with group context, plain text body in HTML wrapper

**File:** `src/HpskSite/Controllers/TrainingGroupController.cs`

Add new endpoint `SendGroupMessage`:
- `[HttpPost] [ValidateAntiForgeryToken]`
- Parameters: `int trainingGroupId`, `string subject`, `string message`
- Auth: `CanManageTrainingGroup(trainingGroupId)` (trainers + admins)
- Fetch all active members in the group
- Send email to each member (excluding the sender)
- Return success with count of emails sent

**File:** `src/HpskSite/Views/TrainingStairs.cshtml`

In the "Min Träningsgrupp" tab:
- Add "Skicka meddelande" button (visible to trainers/admins) in the card header area
- New modal with subject field and textarea for message body
- JS function `showSendGroupMessageModal()` and `sendGroupMessage()`
- On success, show confirmation with number of recipients

### 4. Reset Progress UI

**File:** `src/HpskSite/Views/TrainingStairs.cshtml`

The `ResetProgress` endpoint already exists in `TrainingController.cs` (line ~440, admin-only).

In the admin panel's member table (the leaderboard/participants section where admins see all members):
- Add a "Reset" button (small, outline-danger) next to each member row that has started training
- Confirmation modal: "Vill du nollställa träningsframsteg för [name]? Detta kan inte ångras."
- JS function `resetMemberProgress(memberId, memberName)` that calls `/umbraco/surface/Training/ResetProgress`
- On success, refresh the admin data

Also in the `viewMemberProgressWithApproval` modal (when opened by admin):
- Add a reset button at the bottom of the modal

---

## File Change Summary

| File | Changes |
|------|---------|
| `src/HpskSite/Services/EmailService.cs` | Add 3 methods: `SendTrainingGroupMemberAddedAsync`, `SendTrainingStepApprovedAsync`, `SendTrainingGroupMessageAsync` |
| `src/HpskSite/Controllers/TrainingGroupController.cs` | Inject `EmailService`; call email on `AddTrainingGroupMember`; add `SendGroupMessage` endpoint |
| `src/HpskSite/Controllers/TrainingController.cs` | Inject `EmailService`; call email on `CompleteStep` |
| `src/HpskSite/Views/TrainingStairs.cshtml` | Add group message button+modal in training group tab; add reset progress button in admin panel; add reset button in progress modal |

---

## Implementation Notes

- **EmailService** is registered as singleton via `EmailServiceComposer.cs` -- no registration changes needed
- All email templates use inline HTML with Swedish text and HTML entities (å=`&#229;`, ä=`&#228;`, ö=`&#246;`)
- Email failures must never block the primary operation -- always wrap in try/catch with logging
- The existing `SendEmailAsync` private method handles SMTP; new methods just build HTML and call it
- TrainingGroupController already has `_memberService` and `_memberManager` injected
- TrainingController already has `_memberService` and `_memberManager` but NOT `EmailService` -- needs constructor change
