# UI Implementation Patterns

**Last Updated:** 2025-11-24

## Navigation & Header

### Logo
- **Path:** `~/images/HpskLogo.jpg`
- **Style:** White header background with clickable logo linking to home

### User Menu
- **Display:** Avatar with member initials
- **Dropdown Items:**
  - My Profile
  - Administration (if admin)
  - Logout

### Admin Detection
```csharp
// Check if admin page exists in content tree
var adminPageExists = _contentService.GetByContentType("adminPage").Any();
```

### Site Title/Subtitle
- **Editable:** Via Home page properties in Umbraco backoffice
- **Properties:**
  - `siteTitle` - Main site title
  - `siteSubtitle` - Tagline/subtitle

### Bug Report Button
- **Label:** "Rapportera Fel"
- **Function:** Opens modal for bug reporting
- **Features:**
  - Text description
  - Image upload support
  - Sends to admin email

## Key Pages

- `/admin` - Admin dashboard with tabs (Competitions, Clubs, Users, Training)
- `/clubs` - Club directory for club admins
- `/training-stairs` - Training system interface (Skyttetrappan)
- `/login-register` - Login and registration page with tabs
- `/user-profile` - User profile with dashboard, training results

## Date & Time Pickers ✅

**Standardized:** 2025-11-21

### Critical Rule
**ALWAYS use Flatpickr for date/time inputs** - NEVER use HTML5 native inputs (`<input type="date">`, `<input type="datetime-local">`, `<input type="time">`)

### Why Flatpickr?
- Consistent Swedish localization (sv-SE) across all browsers
- Better UX with calendar popup
- Standardized date format (YYYY-MM-DD / HH:mm)
- Works identically on all platforms (Windows, Mac, mobile)

### CDN Setup

Add once per page/partial:

```html
<!-- Flatpickr Date/Time Picker -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/flatpickr/dist/flatpickr.min.css">
<script src="https://cdn.jsdelivr.net/npm/flatpickr"></script>
<script src="https://cdn.jsdelivr.net/npm/flatpickr/dist/l10n/sv.js"></script>
```

### Implementation Patterns

#### 1. Date Picker (e.g., "Medlem sedan")

```html
<!-- HTML -->
<input type="text" class="form-control" id="memberSince" name="memberSince">

<!-- JavaScript -->
<script>
flatpickr('#memberSince', {
    locale: 'sv',
    dateFormat: 'Y-m-d'
});
</script>
```

#### 2. DateTime Picker (e.g., "Event Date")

```html
<!-- HTML -->
<input type="text" class="form-control" id="eventDate" name="eventDate">

<!-- JavaScript -->
<script>
flatpickr('#eventDate', {
    locale: 'sv',
    enableTime: true,
    time_24hr: true,
    dateFormat: 'Y-m-d H:i'
});
</script>
```

#### 3. Time-Only Picker (e.g., "Start Time")

```html
<!-- HTML -->
<input type="text" class="form-control" id="startTime" name="startTime">

<!-- JavaScript -->
<script>
flatpickr('#startTime', {
    locale: 'sv',
    enableTime: true,
    noCalendar: true,
    dateFormat: 'H:i',
    time_24hr: true
});
</script>
```

### Common Options

```javascript
flatpickr('#myInput', {
    locale: 'sv',                    // Swedish localization
    dateFormat: 'Y-m-d',             // Format: YYYY-MM-DD
    maxDate: 'today',                // Prevent future dates
    minDate: 'today',                // Prevent past dates
    defaultDate: 'today',            // Set initial value to today
    defaultHour: 9,                  // Default time: 09:00
    defaultMinute: 0,
    time_24hr: true,                 // 24-hour format
    enableTime: true,                // Enable time selection
    noCalendar: true                 // Time-only mode (hide calendar)
});
```

### Standardized Files (2025-11-21)

- ✅ `ClubAdminPanel.cshtml` - Event date (datetime), Member since (date)
- ✅ `UserManagement.cshtml` - Member since (date)
- ✅ `TrainingScoreEntry.cshtml` - Training date (date with maxDate: today)
- ✅ `CompetitionStartListManagement.cshtml` - First start time (time-only)

## Date Display Formatting

### Server-Side (C#)

```csharp
@using System.Globalization;

// Full date: "måndag, 5 oktober 2025"
@someDate.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("sv-SE"))

// Short date: "5 okt 2025"
@someDate.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("sv-SE"))

// Date with time: "2025-10-05 14:30"
@someDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("sv-SE"))
```

### Client-Side (JavaScript)

```javascript
// Swedish date string
const dateStr = date.toLocaleDateString('sv-SE', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit'
});

// Swedish datetime string
const dateTimeStr = date.toLocaleString('sv-SE', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
});
```

## Model Usage

### ✅ CORRECT - Use auto-generated models

```csharp
@inherits UmbracoViewPage<ContentModels.AdminPage>
```

### ❌ WRONG - Don't create custom page models

```csharp
public class AdminPage : BasePage { } // Only for complex business logic
```

## Bootstrap & CSS Framework

### Version
- **Bootstrap 5.3.0** - Used throughout the application
- **Bootstrap Icons 1.11.0** - Icon library

### CDN Links

```html
<!-- Bootstrap CSS -->
<link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css" rel="stylesheet">

<!-- Bootstrap Icons -->
<link href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.0/font/bootstrap-icons.css" rel="stylesheet">

<!-- Bootstrap JS Bundle (includes Popper) -->
<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js"></script>
```

### Common Patterns

#### Modal Structure

```html
<div class="modal fade" id="myModal" tabindex="-1">
    <div class="modal-dialog">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title">Modal Title</h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
            </div>
            <div class="modal-body">
                <!-- Content -->
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Stäng</button>
                <button type="button" class="btn btn-primary" onclick="saveData()">Spara</button>
            </div>
        </div>
    </div>
</div>
```

#### Alert Messages

```html
<div class="alert alert-success alert-dismissible fade show" role="alert">
    <i class="bi bi-check-circle me-2"></i>Operation lyckades!
    <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
</div>
```

#### Card Component

```html
<div class="card">
    <div class="card-header">
        <h5><i class="bi bi-info-circle me-2"></i>Card Title</h5>
    </div>
    <div class="card-body">
        <p class="card-text">Card content goes here.</p>
    </div>
</div>
```

## Chart.js Visualizations

### Version
- **Chart.js 4.x** - Used for dashboard visualizations

### CDN Link

```html
<script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
```

### Common Implementation

```javascript
const ctx = document.getElementById('myChart').getContext('2d');
const myChart = new Chart(ctx, {
    type: 'line', // or 'bar', 'pie', etc.
    data: {
        labels: ['Jan', 'Feb', 'Mar'],
        datasets: [{
            label: 'My Dataset',
            data: [12, 19, 3],
            borderColor: 'rgb(75, 192, 192)',
            tension: 0.1
        }]
    },
    options: {
        responsive: true,
        maintainAspectRatio: false
    }
});
```

### Used In
- User profile dashboard (Training Scoring System)
- Progress Over Time charts
- Weapon Class Performance charts

## Rich Text Editor

### CKEditor 5 (Current)
- **Migrated From:** TinyMCE (required API key since 2024)
- **Reason:** Open-source, no API key required
- **Version:** CKEditor 5 (latest)

### CDN Setup

```html
<script src="https://cdn.ckeditor.com/ckeditor5/40.0.0/classic/ckeditor.js"></script>
```

### Basic Implementation

```javascript
ClassicEditor
    .create(document.querySelector('#editor'), {
        toolbar: ['heading', '|', 'bold', 'italic', 'link', 'bulletedList', 'numberedList', 'blockQuote']
    })
    .catch(error => {
        console.error(error);
    });
```

### Used In
- Series descriptions (Admin → Series tab)
- Club news/events
- Competition descriptions

## Form Validation

### Client-Side (JavaScript)

```javascript
function validateForm() {
    const form = document.getElementById('myForm');

    if (!form.checkValidity()) {
        form.classList.add('was-validated');
        return false;
    }

    return true;
}
```

### Server-Side (C#)

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SaveData(MyModel model)
{
    if (!ModelState.IsValid)
    {
        return Json(new { success = false, message = "Validering misslyckades" });
    }

    // Proceed with save
}
```

### Required CSRF Token

```html
@Html.AntiForgeryToken()

<!-- Or in JavaScript fetch -->
<script>
const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

fetch('/endpoint', {
    method: 'POST',
    headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': token
    },
    body: JSON.stringify(data)
});
</script>
```

## Loading States

### Spinner

```html
<div id="loadingSpinner" class="text-center py-4">
    <div class="spinner-border" role="status">
        <span class="visually-hidden">Laddar...</span>
    </div>
    <p class="mt-2">Laddar data...</p>
</div>
```

### JavaScript Pattern

```javascript
function showLoading() {
    document.getElementById('loadingSpinner').style.display = 'block';
    document.getElementById('content').style.display = 'none';
}

function hideLoading() {
    document.getElementById('loadingSpinner').style.display = 'none';
    document.getElementById('content').style.display = 'block';
}
```

## Common UI Utilities

### Show Alert Function

```javascript
function showAlert(type, message, duration = 5000) {
    // Remove existing alerts
    document.querySelectorAll('.alert-toast').forEach(alert => alert.remove());

    // Create new alert
    const alertDiv = document.createElement('div');
    alertDiv.className = `alert alert-${type} alert-dismissible fade show alert-toast`;
    alertDiv.style.cssText = 'position: fixed; top: 20px; right: 20px; z-index: 9999; min-width: 300px;';
    alertDiv.innerHTML = `
        ${message}
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    `;

    document.body.appendChild(alertDiv);

    // Auto-dismiss
    setTimeout(() => {
        alertDiv.remove();
    }, duration);
}
```

### Confirm Dialog

```javascript
function confirmAction(message, callback) {
    if (confirm(message)) {
        callback();
    }
}

// Usage
confirmAction('Är du säker?', () => {
    // Perform action
});
```

## Responsive Design

### Breakpoints (Bootstrap 5)

- **xs:** < 576px (mobile)
- **sm:** ≥ 576px (mobile landscape)
- **md:** ≥ 768px (tablet)
- **lg:** ≥ 992px (desktop)
- **xl:** ≥ 1200px (large desktop)
- **xxl:** ≥ 1400px (extra large)

### Common Responsive Classes

```html
<!-- Hide on mobile, show on desktop -->
<div class="d-none d-md-block">Desktop only</div>

<!-- Show on mobile, hide on desktop -->
<div class="d-block d-md-none">Mobile only</div>

<!-- Responsive columns -->
<div class="row">
    <div class="col-12 col-md-6 col-lg-4">Column</div>
</div>
```

## Related Documentation

- [LOGIN_REGISTRATION_SYSTEM.md](LOGIN_REGISTRATION_SYSTEM.md) - Login UI patterns
- [TRAINING_SCORING_SYSTEM.md](TRAINING_SCORING_SYSTEM.md) - Dashboard UI
- [CLUB_SYSTEM_MIGRATIONS.md](CLUB_SYSTEM_MIGRATIONS.md) - Club admin UI
