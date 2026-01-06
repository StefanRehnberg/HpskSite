# HPSK Video Tutorial System - Implementation TODO

## **REVISED UX DESIGN** (2025-12-09) ⭐

### **Two-Stage Help System for Better Discoverability**

**Problem with Original Plan:** Dismissible banner disappears permanently → Users lose access to help

**New Solution:** Two-stage approach combining initial awareness with persistent access

#### **Stage 1: First-Time User (Eye-Catching Banner)**
```
┌───────────────────────────────────────────────────────────┐
│ 🎥 Första gången här? Se vår handledning!                │
│     [Visa video (4 min)] [Stäng]                          │
└───────────────────────────────────────────────────────────┘
```
- Prominent banner at top of page
- Shows only on first visit to each page
- "Visa video" button opens tutorial + dismisses banner
- "Stäng" button dismisses permanently
- Saves dismissal state in localStorage

#### **Stage 2: After Dismissal (Persistent Discrete Icon)**
```
DESKTOP VIEW:
┌───────────────────────────────────────────────────────────┐
│  Header / Navigation                                      │
├───────────────────────────────────────────────────────────┤
│                                                      [▶️]  │ ← Fixed icon
│  Tävlingar                                                │    (top-right)
│  ────────────────────────────────────────────────         │
│                                                      [▶️]  │ ← Pulses for
│  Content here...                                          │    6 seconds,
│  - Competition cards                                      │    then static
│  - Filters                                           [▶️]  │
│  - Etc.                                                   │ ← Tooltip:
│                                                      [▶️]  │    "Klicka för
└───────────────────────────────────────────────────────────┘     instruktioner"

MOBILE VIEW (< 768px):
┌─────────────────────────┐
│  Header                 │
├─────────────────────────┤
│  Tävlingar              │  ← Icon hidden on mobile
│  ─────────────────────  │     (saves screen space)
│  Content...             │
└─────────────────────────┘
```

**Icon Specifications:**
- **Icon:** `bi-play-circle-fill` (unmistakable video indicator)
- **Position:** Fixed top-right corner (`position: fixed; top: 100px; right: 20px;`)
- **Size:** 2.5rem (40px) with 56x56px white circular background
- **Opacity:** 60% (discrete) → 100% on hover
- **Hover Effect:** Scale 1.2x + shadow increase
- **Animation:** 6-second pulse (3x) on first show after banner dismissal, then static forever
- **Tooltip:** "Klicka för instruktioner" (Bootstrap tooltip, left placement)
- **Mobile:** Hidden on screens < 768px
- **Click Action:** Opens tutorial modal

**Benefits:**
✅ Always accessible - Users can find help anytime
✅ Discrete - Low opacity doesn't clutter interface
✅ Responsive - Hidden on mobile to save space
✅ Intuitive - Play icon universally understood as "video"
✅ Progressive disclosure - Banner first, icon after

---

## **Phase 1: Video Production** (Do this first! ✅)

### **Recommended Tutorial List** (Prioritized by user type)

**Regular Members (Essential - 6 videos):**

1. ✅ **"Kom igång med HPSK-sidan"** (~5 min)
   - Site overview, login process, main navigation
   - Where to find profile, competitions, training

2. ✅ **"Anmäl dig till tävling & betala med Swish"** (~4 min)
   - Browse competitions with filters
   - Click registration, fill form
   - Scan Swish QR code, complete payment

3. ✅ **"Din profil & instrumentpanel"** (~6 min)
   - Navigate 3 tabs (Dashboard, Profil, Träningsresultat)
   - Understand charts and statistics
   - Edit personal information

4. ✅ **"Registrera träningsresultat"** (~5 min)
   - Open training score entry modal
   - Enter shot-by-shot data
   - View results in table

5. ✅ **"Skyttetrappan - Så fungerar det"** (~7 min)
   - Explain 9 levels (Bronze → Gold → Record)
   - How progression works
   - Competition requirements
   - View leaderboard

6. ✅ **"Gå med i klubb"** (~3 min)
   - Primary vs additional membership
   - Club-specific features access

**Club Admins (4 videos):**

7. ✅ **"Klubbadministration - Godkänna medlemmar"** (~5 min)
   - Approve/reject workflow
   - Pending approvals management
   - Rejection reasons

8. ✅ **"Redigera klubbinformation"** (~4 min)
   - Edit club details, contact info
   - Update logo and banner images
   - Manage club visibility

9. ✅ **"Hantera klubbevenemang"** (~5 min)
   - Create new club events
   - Edit existing events
   - Delete events
   - Event types and settings

10. ✅ **"Skyttetrappan för instruktörer"** (~6 min)
    - Mark steps as complete for members
    - Track member progress
    - Add completion notes
    - View progress history

**Site Admins (3 videos):**

11. ✅ **"Skapa tävling"** (~8 min)
    - Competition creation wizard
    - All settings and options
    - Shooting classes, dates, fees
    - Swish configuration

12. ✅ **"Hantera anmälningar & betalningar"** (~6 min)
    - View registrations
    - Update registration status
    - Verify Swish payments
    - Export registration data

13. ✅ **"Användarhantering"** (~5 min)
    - Approve pending members
    - Assign member groups/roles
    - Delete members
    - Search and filter functionality

---

## **Phase 2: Umbraco Configuration** (After videos are created)

### **Add to hpskMember Member Type**

Navigate to: **Backoffice → Settings → Member Types → hpskMember**

**New Properties to Add:**

#### 1. **watchedTutorials** (Textarea)
- **Alias:** `watchedTutorials`
- **Type:** Textarea
- **Description:** "JSON array of watched tutorial IDs and timestamps"
- **Format Example:**
  ```json
  [
    {
      "id": "tutorial-01",
      "watched": true,
      "timestamp": "2025-11-23T10:30:00Z",
      "viewCount": 2
    },
    {
      "id": "tutorial-02",
      "watched": true,
      "timestamp": "2025-11-22T14:15:00Z",
      "viewCount": 1
    }
  ]
  ```

#### 2. **firstLoginWelcomeShown** (True/False)
- **Alias:** `firstLoginWelcomeShown`
- **Type:** True/False
- **Description:** "Has the welcome tutorial modal been shown after first login?"
- **Default:** False

#### 3. **dismissedTutorialBanners** (Textarea)
- **Alias:** `dismissedTutorialBanners`
- **Type:** Textarea
- **Description:** "JSON array of page-specific help banners user has permanently dismissed"
- **Format Example:**
  ```json
  ["competition-list", "user-profile", "training-stairs", "club-admin"]
  ```

---

## **Phase 3: Technical Implementation**

### **Create New Files**

#### 1. **TutorialPage.cshtml** (`Views/TutorialPage.cshtml`)
**Purpose:** Tutorial hub page listing all videos

**Features:**
- Card layout with YouTube thumbnails
- Organized by user role (Member / Club Admin / Site Admin)
- Search/filter functionality
- Duration display
- "Watched" indicators
- Direct play or modal view

**Template:**
```html
@inherits UmbracoViewPage
@using Umbraco.Cms.Web.Common.PublishedModels;

<div class="container mt-4">
    <h1>Hjälp & Tutorials</h1>
    <p class="lead">Lär dig hur du använder HPSK-sidan med våra videotutorials</p>

    <!-- Filter pills by role -->
    <div class="btn-group mb-4" role="group">
        <button class="btn btn-outline-primary active" data-filter="all">Alla</button>
        <button class="btn btn-outline-primary" data-filter="member">För medlemmar</button>
        <button class="btn btn-outline-primary" data-filter="club-admin">För klubbadministratörer</button>
        <button class="btn btn-outline-primary" data-filter="site-admin">För siteadministratörer</button>
    </div>

    <!-- Tutorial cards grid -->
    <div class="row" id="tutorialGrid">
        <!-- Populated dynamically from Umbraco content nodes -->
    </div>
</div>
```

---

#### 2. **TutorialModal.cshtml** (`Views/Partials/TutorialModal.cshtml`)
**Purpose:** Reusable modal component for embedded YouTube videos

**Features:**
- Embedded YouTube player (responsive 16:9)
- "Mark as watched" button
- Related tutorials section
- Close/dismiss functionality

**Template:**
```html
<!-- Tutorial Modal -->
<div class="modal fade" id="tutorialModal" tabindex="-1" aria-labelledby="tutorialModalLabel" aria-hidden="true">
    <div class="modal-dialog modal-lg modal-dialog-centered">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title" id="tutorialModalLabel">Tutorial Title</h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
            </div>
            <div class="modal-body">
                <!-- YouTube embed (16:9 aspect ratio) -->
                <div class="ratio ratio-16x9 mb-3">
                    <iframe id="tutorialVideo" src="" frameborder="0" allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture" allowfullscreen></iframe>
                </div>

                <!-- Description -->
                <p id="tutorialDescription"></p>

                <!-- Mark as watched button -->
                <button class="btn btn-success btn-sm" id="markWatchedBtn">
                    <i class="bi bi-check-circle"></i> Markera som sedd
                </button>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Stäng</button>
            </div>
        </div>
    </div>
</div>
```

---

#### 3. **TutorialController.cs** (`Controllers/TutorialController.cs`)
**Purpose:** API endpoints for tutorial management

**Key Methods:**
- `GetTutorials()` - List all tutorials filtered by user role
- `MarkAsWatched(tutorialId)` - Update member property
- `GetWatchedTutorials()` - Get member's watched list
- `GetRecommendedTutorials()` - Smart recommendations based on role and current page

**Code Example:**
```csharp
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Controllers;
using System.Text.Json;

namespace HpskSite.Controllers
{
    public class TutorialController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IContentService _contentService;

        public TutorialController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            IContentService contentService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _contentService = contentService;
        }

        [HttpPost]
        public async Task<IActionResult> MarkAsWatched(string tutorialId)
        {
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Not authenticated" });

            var umbracoMember = _memberService.GetById(member.Key);
            if (umbracoMember == null) return Json(new { success = false, message = "Member not found" });

            // Get current watched list
            var watchedJson = umbracoMember.GetValue<string>("watchedTutorials") ?? "[]";
            var watched = JsonSerializer.Deserialize<List<TutorialWatch>>(watchedJson) ?? new List<TutorialWatch>();

            // Add or update
            var existing = watched.FirstOrDefault(w => w.Id == tutorialId);
            if (existing != null)
            {
                existing.ViewCount++;
                existing.Timestamp = DateTime.UtcNow;
            }
            else
            {
                watched.Add(new TutorialWatch
                {
                    Id = tutorialId,
                    Watched = true,
                    Timestamp = DateTime.UtcNow,
                    ViewCount = 1
                });
            }

            // Save back to member
            umbracoMember.SetValue("watchedTutorials", JsonSerializer.Serialize(watched));
            _memberService.Save(umbracoMember);

            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetWatchedTutorials()
        {
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, watched = new List<string>() });

            var umbracoMember = _memberService.GetById(member.Key);
            var watchedJson = umbracoMember?.GetValue<string>("watchedTutorials") ?? "[]";
            var watched = JsonSerializer.Deserialize<List<TutorialWatch>>(watchedJson) ?? new List<TutorialWatch>();

            return Json(new { success = true, watched });
        }

        [HttpPost]
        public async Task<IActionResult> MarkWelcomeSeen()
        {
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });

            var umbracoMember = _memberService.GetById(member.Key);
            umbracoMember.SetValue("firstLoginWelcomeShown", true);
            _memberService.Save(umbracoMember);

            return Json(new { success = true });
        }
    }

    // Helper class for JSON serialization
    public class TutorialWatch
    {
        public string Id { get; set; }
        public bool Watched { get; set; }
        public DateTime Timestamp { get; set; }
        public int ViewCount { get; set; }
    }
}
```

---

#### 4. **tutorialHelper.js** (`wwwroot/js/tutorialHelper.js`)
**Purpose:** Client-side tutorial modal management and tracking

**Features:**
- Open tutorial modal with YouTube video
- Track video playback
- Mark as watched via AJAX
- Dismiss banners
- LocalStorage fallback for non-authenticated users

**Code Example:**
```javascript
// Tutorial Helper JavaScript
const TutorialHelper = {
    // Tutorial metadata (to be populated from Umbraco)
    tutorials: {},

    // Page-to-Tutorial mapping configuration
    // Maps page IDs to their corresponding tutorial IDs
    pageTutorialMap: {
        'competitions': 'tutorial-02',      // Anmälan & Swish
        'user-profile': 'tutorial-03',      // Profil & Dashboard
        'training-stairs': 'tutorial-05',   // Skyttetrappan
        'club-admin': 'tutorial-07',        // Godkänna medlemmar
        'club-events': 'tutorial-09',       // Hantera evenemang
        'admin': 'tutorial-11'              // Skapa tävling
    },

    // Initialize tutorial system
    init: function() {
        // Load tutorials from server or inline data
        this.loadTutorials();

        // Initialize persistent icon tooltips and animations
        this.initPersistentIcons();

        // Check if first-time welcome should be shown
        this.checkFirstTimeWelcome();
    },

    // Load tutorial metadata
    loadTutorials: function() {
        // This would be populated from Umbraco content
        // For now, example structure:
        this.tutorials = {
            'tutorial-01': {
                id: 'tutorial-01',
                title: 'Kom igång med HPSK-sidan',
                youtubeId: 'YOUTUBE_VIDEO_ID_HERE',
                description: 'Lär dig navigera på HPSK-sidan',
                duration: '5 min',
                targetRole: 'all'
            },
            'tutorial-02': {
                id: 'tutorial-02',
                title: 'Anmäl dig till tävling & betala med Swish',
                youtubeId: 'YOUTUBE_VIDEO_ID_HERE',
                description: 'Så här registrerar du dig för tävlingar och betalar med Swish',
                duration: '4 min',
                targetRole: 'member'
            }
            // ... more tutorials
        };
    },

    // Open tutorial modal
    open: function(tutorialId) {
        const tutorial = this.tutorials[tutorialId];
        if (!tutorial) {
            console.error('Tutorial not found:', tutorialId);
            return;
        }

        // Set modal content
        document.getElementById('tutorialModalLabel').textContent = tutorial.title;
        document.getElementById('tutorialDescription').textContent = tutorial.description;
        document.getElementById('tutorialVideo').src = `https://www.youtube.com/embed/${tutorial.youtubeId}?autoplay=1`;

        // Store current tutorial ID
        this.currentTutorialId = tutorialId;

        // Show modal
        const modal = new bootstrap.Modal(document.getElementById('tutorialModal'));
        modal.show();

        // Setup mark as watched button
        document.getElementById('markWatchedBtn').onclick = () => this.markAsWatched(tutorialId);
    },

    // Mark tutorial as watched
    markAsWatched: function(tutorialId) {
        fetch('/umbraco/surface/Tutorial/MarkAsWatched', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({ tutorialId: tutorialId })
        })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                // Update UI to show as watched
                this.updateWatchedUI(tutorialId);

                // Show success feedback
                const btn = document.getElementById('markWatchedBtn');
                btn.innerHTML = '<i class="bi bi-check-circle-fill"></i> Markerad som sedd!';
                btn.classList.remove('btn-success');
                btn.classList.add('btn-secondary');
                btn.disabled = true;
            }
        })
        .catch(error => console.error('Error marking tutorial as watched:', error));
    },

    // Update UI to reflect watched status
    updateWatchedUI: function(tutorialId) {
        // Add checkmark to tutorial cards, etc.
        const elements = document.querySelectorAll(`[data-tutorial-id="${tutorialId}"]`);
        elements.forEach(el => {
            el.classList.add('watched');
            const badge = el.querySelector('.watched-badge');
            if (badge) badge.style.display = 'inline-block';
        });
    },

    // Show first-time welcome modal
    showWelcomeModal: function(tutorialId) {
        // Show a special welcome modal with tutorial
        this.open(tutorialId);
    },

    // Mark welcome as seen
    markWelcomeSeen: function() {
        fetch('/umbraco/surface/Tutorial/MarkWelcomeSeen', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            }
        })
        .then(response => response.json())
        .catch(error => console.error('Error marking welcome as seen:', error));
    },

    // Check if first-time welcome should be shown
    checkFirstTimeWelcome: function() {
        // This would check the member property on page load
        // Implementation depends on how you pass server data to client
    },

    // Dismiss help banner
    dismissBanner: function(bannerId) {
        // Hide banner
        const banner = document.getElementById(`banner-${bannerId}`);
        if (banner) banner.style.display = 'none';

        // Save to localStorage as backup
        const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');
        if (!dismissed.includes(bannerId)) {
            dismissed.push(bannerId);
            localStorage.setItem('dismissedBanners', JSON.stringify(dismissed));
        }

        // Save to server (for logged-in users)
        fetch('/umbraco/surface/Tutorial/DismissBanner', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({ bannerId: bannerId })
        });
    },

    // Open tutorial and dismiss banner (convenience method)
    openAndDismissBanner: function(tutorialId, bannerId) {
        this.open(tutorialId);
        this.dismissBanner(bannerId);
    },

    // Initialize persistent icon tooltips and animations
    initPersistentIcons: function() {
        // Initialize Bootstrap tooltips for all persistent icons
        const icons = document.querySelectorAll('.tutorial-persistent-icon');
        icons.forEach(icon => {
            new bootstrap.Tooltip(icon);
        });

        // Add pulse animation for first 6 seconds after banner dismissal
        // Check if banner for this page has been dismissed
        const pageId = this.getCurrentPageId();
        const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');

        if (dismissed.includes(pageId)) {
            // Banner dismissed - check if we've shown pulse for this icon yet
            const seenIconKey = `seenIcon_${pageId}`;
            const hasSeenIcon = localStorage.getItem(seenIconKey);

            if (!hasSeenIcon) {
                icons.forEach(icon => {
                    icon.classList.add('first-show');
                    setTimeout(() => {
                        icon.classList.remove('first-show');
                    }, 6000); // Remove after 3 pulses (2s each)
                });
                localStorage.setItem(seenIconKey, 'true');
            }
        }
    },

    // Helper to get current page ID
    getCurrentPageId: function() {
        // Extract page identifier from URL or element
        // Example: competitions, user-profile, training-stairs, etc.
        const path = window.location.pathname.toLowerCase();
        if (path.includes('competitions') || path.includes('tavlingar')) return 'competitions';
        if (path.includes('user-profile') || path.includes('profil')) return 'user-profile';
        if (path.includes('training-stairs') || path.includes('skyttetrappan')) return 'training-stairs';
        if (path.includes('club-admin') || path.includes('klubbadmin')) return 'club-admin';
        if (path.includes('club') && path.includes('events')) return 'club-events';
        if (path.includes('admin')) return 'admin';
        return 'default';
    },

    // Get tutorial ID for current page
    getTutorialForCurrentPage: function() {
        const pageId = this.getCurrentPageId();
        return this.pageTutorialMap[pageId] || null;
    },

    // Open tutorial for current page (convenience method)
    openCurrentPageTutorial: function() {
        const tutorialId = this.getTutorialForCurrentPage();
        if (tutorialId) {
            this.open(tutorialId);
        } else {
            console.warn('No tutorial configured for current page:', this.getCurrentPageId());
        }
    }
};

// Initialize on page load
document.addEventListener('DOMContentLoaded', function() {
    TutorialHelper.init();
});
```

---

#### 5. **tutorial.css** (`wwwroot/css/tutorial.css`)
**Purpose:** Styling for tutorial system components

**Code Example:**
```css
/* ============================================
   PERSISTENT TUTORIAL ICON - Fixed Top-Right
   ============================================ */

.tutorial-persistent-icon {
    /* Fixed Positioning */
    position: fixed;
    top: 100px;
    right: 20px;
    z-index: 1000;

    /* Visual Styling */
    display: flex;
    align-items: center;
    justify-content: center;
    width: 56px;
    height: 56px;

    /* Icon Styling */
    font-size: 2.5rem;
    color: #0d6efd;
    opacity: 0.6;

    /* Background Circle */
    background: white;
    border-radius: 50%;
    box-shadow: 0 2px 8px rgba(0,0,0,0.15);

    /* Interaction */
    cursor: pointer;
    transition: all 0.2s ease;
}

.tutorial-persistent-icon:hover {
    opacity: 1;
    transform: scale(1.2);
    box-shadow: 0 4px 12px rgba(13, 110, 253, 0.3);
}

/* Hide on mobile/small screens */
@media (max-width: 767px) {
    .tutorial-persistent-icon {
        display: none;
    }
}

/* Pulse animation for first 6 seconds (after banner dismissal) */
@keyframes pulse-tutorial {
    0%, 100% {
        opacity: 0.6;
        transform: scale(1);
    }
    50% {
        opacity: 1;
        transform: scale(1.1);
    }
}

.tutorial-persistent-icon.first-show {
    animation: pulse-tutorial 2s ease-in-out 3; /* 3 pulses = 6 seconds */
}

/* Tutorial Help Icon (legacy/inline use) */
.tutorial-help-icon {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.875rem;
    cursor: pointer;
    transition: all 0.2s ease;
}

.tutorial-help-icon:hover {
    transform: scale(1.05);
}

.tutorial-help-icon i {
    font-size: 1.1rem;
}

/* Tutorial Banner */
.tutorial-banner {
    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    color: white;
    padding: 1rem 1.5rem;
    border-radius: 8px;
    margin-bottom: 1.5rem;
    display: flex;
    justify-content: space-between;
    align-items: center;
    box-shadow: 0 4px 6px rgba(0,0,0,0.1);
}

.tutorial-banner .btn-close {
    filter: invert(1);
}

.tutorial-banner-content {
    display: flex;
    align-items: center;
    gap: 1rem;
}

.tutorial-banner-icon {
    font-size: 2rem;
}

/* Tutorial Card */
.tutorial-card {
    position: relative;
    transition: transform 0.2s ease, box-shadow 0.2s ease;
    cursor: pointer;
}

.tutorial-card:hover {
    transform: translateY(-4px);
    box-shadow: 0 8px 16px rgba(0,0,0,0.15);
}

.tutorial-card .watched-badge {
    position: absolute;
    top: 10px;
    right: 10px;
    background: #28a745;
    color: white;
    padding: 0.25rem 0.5rem;
    border-radius: 12px;
    font-size: 0.75rem;
    display: none;
}

.tutorial-card.watched .watched-badge {
    display: inline-block;
}

.tutorial-thumbnail {
    width: 100%;
    height: 180px;
    object-fit: cover;
    border-radius: 8px 8px 0 0;
}

.tutorial-duration {
    position: absolute;
    bottom: 10px;
    right: 10px;
    background: rgba(0,0,0,0.8);
    color: white;
    padding: 0.25rem 0.5rem;
    border-radius: 4px;
    font-size: 0.75rem;
}

/* Help Dropdown in Header */
.nav-item .help-icon {
    color: #667eea;
    margin-right: 0.25rem;
}

/* First-time Welcome Modal */
.welcome-modal .modal-header {
    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    color: white;
}

.welcome-modal .btn-close {
    filter: invert(1);
}

/* Responsive */
@media (max-width: 768px) {
    .tutorial-banner {
        flex-direction: column;
        text-align: center;
    }

    .tutorial-banner-content {
        margin-bottom: 0.5rem;
    }
}
```

---

### **Modify Existing Files**

#### 1. **_Layout.cshtml** - Add Help Dropdown in Header

**Location:** After existing navigation items, before user menu

```html
<!-- Help Dropdown -->
@if (Member.IsLoggedIn())
{
    <li class="nav-item dropdown">
        <a class="nav-link dropdown-toggle" href="#" id="helpDropdown" data-bs-toggle="dropdown" aria-expanded="false">
            <i class="bi bi-camera-video help-icon"></i> Hjälp
        </a>
        <ul class="dropdown-menu dropdown-menu-end" aria-labelledby="helpDropdown">
            <li><h6 class="dropdown-header">Hjälp för denna sida</h6></li>
            <li><a class="dropdown-item" href="#" onclick="TutorialHelper.open('contextual-tutorial-id')">
                <i class="bi bi-play-circle"></i> Se handledning
            </a></li>
            <li><hr class="dropdown-divider"></li>
            <li><h6 class="dropdown-header">Populära guider</h6></li>
            <li><a class="dropdown-item" href="#" onclick="TutorialHelper.open('tutorial-02')">
                Anmäla dig till tävling
            </a></li>
            <li><a class="dropdown-item" href="#" onclick="TutorialHelper.open('tutorial-04')">
                Registrera träningsresultat
            </a></li>
            <li><hr class="dropdown-divider"></li>
            <li><a class="dropdown-item" href="/tutorials">
                <i class="bi bi-collection-play"></i> Alla tutorials
            </a></li>
        </ul>
    </li>
}
```

**Also add in `<head>` section:**
```html
<!-- Tutorial CSS -->
<link rel="stylesheet" href="~/css/tutorial.css" />
```

**Also add before closing `</body>` tag:**
```html
<!-- Tutorial JavaScript -->
<script src="~/js/tutorialHelper.js"></script>

<!-- Include Tutorial Modal Partial -->
@await Html.PartialAsync("TutorialModal")
```

---

#### 2. **CompetitionsHub.cshtml** - Add Persistent Icon + Banner

**Add at top of page content** (after opening `<div class="container">` tag):

**OPTION 1: Hardcoded Tutorial ID** (Explicit, recommended for clarity)
```html
<div class="container py-4">
    <!-- Fixed Persistent Icon (top-right corner, hidden on mobile) -->
    <!-- Tutorial ID is explicitly specified: tutorial-02 -->
    <i class="bi bi-play-circle-fill tutorial-persistent-icon"
       onclick="TutorialHelper.open('tutorial-02')"
       data-bs-toggle="tooltip"
       data-bs-placement="left"
       data-bs-title="Klicka för instruktioner"
       id="tutorial-icon-competitions"></i>

    <!-- Page Title -->
    <h1 class="mb-4">Tävlingar</h1>

    <!-- First-Time Help Banner -->
    <div class="tutorial-banner" id="banner-competitions" style="display: none;">
        <div class="tutorial-banner-content">
            <i class="bi bi-camera-video tutorial-banner-icon"></i>
            <div>
                <strong>Första gången här?</strong>
                <p class="mb-0">Lär dig hur du anmäler dig till tävlingar och betalar med Swish!</p>
            </div>
        </div>
        <div>
            <!-- Explicit tutorial ID: tutorial-02 -->
            <button class="btn btn-light btn-sm me-2"
                    onclick="TutorialHelper.openAndDismissBanner('tutorial-02', 'competitions')">
                <i class="bi bi-play-circle"></i> Visa video (4 min)
            </button>
            <button type="button" class="btn-close"
                    onclick="TutorialHelper.dismissBanner('competitions')"></button>
        </div>
    </div>

    <script>
        // Show banner only if NOT dismissed
        document.addEventListener('DOMContentLoaded', function() {
            const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');
            if (!dismissed.includes('competitions')) {
                document.getElementById('banner-competitions').style.display = 'flex';
            }

            // Initialize tooltip for persistent icon
            const tooltip = new bootstrap.Tooltip(document.getElementById('tutorial-icon-competitions'));
        });
    </script>

    <!-- Rest of page content... -->
</div>
```

**OPTION 2: Auto-Detected Tutorial ID** (Dynamic, uses URL-based mapping)
```html
<div class="container py-4">
    <!-- Fixed Persistent Icon (top-right corner, hidden on mobile) -->
    <!-- Tutorial ID auto-detected based on URL via getCurrentPageId() -->
    <i class="bi bi-play-circle-fill tutorial-persistent-icon"
       onclick="TutorialHelper.openCurrentPageTutorial()"
       data-bs-toggle="tooltip"
       data-bs-placement="left"
       data-bs-title="Klicka för instruktioner"
       id="tutorial-icon-competitions"></i>

    <!-- Page Title -->
    <h1 class="mb-4">Tävlingar</h1>

    <!-- First-Time Help Banner -->
    <div class="tutorial-banner" id="banner-competitions" style="display: none;">
        <div class="tutorial-banner-content">
            <i class="bi bi-camera-video tutorial-banner-icon"></i>
            <div>
                <strong>Första gången här?</strong>
                <p class="mb-0">Lär dig hur du anmäler dig till tävlingar och betalar med Swish!</p>
            </div>
        </div>
        <div>
            <!-- Auto-detected tutorial ID + explicit page ID -->
            <button class="btn btn-light btn-sm me-2"
                    onclick="TutorialHelper.openAndDismissBanner(TutorialHelper.getTutorialForCurrentPage(), 'competitions')">
                <i class="bi bi-play-circle"></i> Visa video (4 min)
            </button>
            <button type="button" class="btn-close"
                    onclick="TutorialHelper.dismissBanner('competitions')"></button>
        </div>
    </div>

    <script>
        // Show banner only if NOT dismissed
        document.addEventListener('DOMContentLoaded', function() {
            const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');
            if (!dismissed.includes('competitions')) {
                document.getElementById('banner-competitions').style.display = 'flex';
            }

            // Initialize tooltip for persistent icon
            const tooltip = new bootstrap.Tooltip(document.getElementById('tutorial-icon-competitions'));
        });
    </script>

    <!-- Rest of page content... -->
</div>
```

**How It Works:**

1. **URL Detection:** `getCurrentPageId()` checks the URL path
   - Example: `/competitions` or `/tavlingar` → returns `'competitions'`

2. **Mapping Lookup:** `getTutorialForCurrentPage()` looks up in `pageTutorialMap`
   - `'competitions'` → `'tutorial-02'`

3. **Tutorial Opens:** `openCurrentPageTutorial()` opens the correct tutorial

**Configuration:**
```javascript
pageTutorialMap: {
    'competitions': 'tutorial-02',      // ← Maps this page...
    'user-profile': 'tutorial-03',      //   ...to this tutorial
    'training-stairs': 'tutorial-05',
    'club-admin': 'tutorial-07',
    'club-events': 'tutorial-09',
    'admin': 'tutorial-11'
}
```

**Recommendation:** Use **Option 1 (hardcoded)** for clarity and easier debugging. Use **Option 2 (auto-detected)** only if you have many pages and want centralized configuration.

---

#### 3. **UserProfile.cshtml** - Add Help Icons and Welcome Modal

**Add help icons to tab headers:**
```html
<!-- Dashboard Tab -->
<li class="nav-item" role="presentation">
    <button class="nav-link active" id="dashboard-tab" data-bs-toggle="tab" data-bs-target="#dashboard" type="button" role="tab">
        Instrumentpanel
        <i class="bi bi-camera-video text-primary ms-1" style="cursor:pointer;" onclick="event.stopPropagation(); TutorialHelper.open('tutorial-03');" title="Se handledning"></i>
    </button>
</li>

<!-- Profile Tab -->
<li class="nav-item" role="presentation">
    <button class="nav-link" id="profile-tab" data-bs-toggle="tab" data-bs-target="#profile" type="button" role="tab">
        Profil
        <i class="bi bi-camera-video text-primary ms-1" style="cursor:pointer;" onclick="event.stopPropagation(); TutorialHelper.open('tutorial-03');" title="Se handledning"></i>
    </button>
</li>

<!-- Training Results Tab -->
<li class="nav-item" role="presentation">
    <button class="nav-link" id="training-results-tab" data-bs-toggle="tab" data-bs-target="#trainingResults" type="button" role="tab">
        Träningsresultat
        <i class="bi bi-camera-video text-primary ms-1" style="cursor:pointer;" onclick="event.stopPropagation(); TutorialHelper.open('tutorial-04');" title="Se handledning"></i>
    </button>
</li>
```

**Add first-time welcome modal logic at bottom of page:**
```html
@if (Member.IsLoggedIn())
{
    var member = await memberManager.GetCurrentMemberAsync();
    if (member != null)
    {
        var umbracoMember = memberService.GetById(member.Key);
        var hasSeenWelcome = umbracoMember?.GetValue<bool>("firstLoginWelcomeShown") ?? false;

        if (!hasSeenWelcome)
        {
            <script>
                // Show welcome modal on first login
                document.addEventListener('DOMContentLoaded', function() {
                    setTimeout(function() {
                        if (confirm('Välkommen till HPSK! Vill du se en snabb genomgång av sidan?')) {
                            TutorialHelper.showWelcomeModal('tutorial-01');
                            TutorialHelper.markWelcomeSeen();
                        } else {
                            TutorialHelper.markWelcomeSeen();
                        }
                    }, 1000);
                });
            </script>
        }
    }
}
```

---

#### 4. **Competition.cshtml** - Add Help Icon Near Registration

**Add near registration button:**
```html
<div class="d-flex align-items-center gap-2">
    <button class="btn btn-primary" id="registerBtn">
        Anmäl dig
    </button>
    <button class="btn btn-outline-secondary btn-sm" onclick="TutorialHelper.open('tutorial-02')">
        <i class="bi bi-camera-video"></i> Se hur det går till
    </button>
</div>
```

---

#### 5. **TrainingStairs.cshtml** - Add Help Icons in Tabs

**Add to each tab header:**
```html
<!-- Trappan Tab -->
<li class="nav-item" role="presentation">
    <button class="nav-link active" id="levels-tab" data-bs-toggle="tab" data-bs-target="#levels">
        Trappan
        <i class="bi bi-camera-video text-primary ms-1" style="cursor:pointer;" onclick="event.stopPropagation(); TutorialHelper.open('tutorial-05');"></i>
    </button>
</li>

<!-- Similar for other tabs -->
```

---

#### 6. **ClubAdmin.cshtml** - Add Help Icons

**Add in admin panel header:**
```html
<div class="d-flex justify-content-between align-items-center mb-3">
    <h2>Klubbadministration</h2>
    <button class="btn btn-outline-primary btn-sm" onclick="TutorialHelper.open('tutorial-07')">
        <i class="bi bi-camera-video"></i> Hjälp med administration
    </button>
</div>
```

---

#### 7. **Club.cshtml** (Club Admin Panel) - Add Contextual Help

**Add to admin tabs:**
```html
<!-- Events Tab -->
<li class="nav-item" role="presentation">
    <button class="nav-link active" id="events-tab" data-bs-toggle="tab" data-bs-target="#events">
        Evenemang
        <i class="bi bi-camera-video text-primary ms-1" style="cursor:pointer;" onclick="event.stopPropagation(); TutorialHelper.open('tutorial-09');"></i>
    </button>
</li>
```

---

### **Create Tutorial Document Type in Umbraco**

Navigate to: **Backoffice → Settings → Document Types → Create**

**Document Type:** `tutorial`

**Properties:**

| Property Name | Alias | Type | Description |
|--------------|-------|------|-------------|
| Tutorial ID | tutorialId | Textstring | Unique identifier (e.g., "tutorial-01") |
| Tutorial Title | tutorialTitle | Textstring | Swedish title |
| Description | tutorialDescription | Textarea | Brief description |
| **YouTube Video ID** | **youtubeId** | **Textstring** | **YouTube video ID only (not full URL)** ⭐ |
| Duration | duration | Textstring | Display duration (e.g., "5 min") |
| Target Role | targetRole | Dropdown | "Member", "Club Admin", "Site Admin", "All" |
| Related Pages | relatedPages | Textarea | JSON array of page aliases where this tutorial appears |
| Thumbnail URL | thumbnailUrl | Textstring | YouTube thumbnail URL (auto-generated: `https://img.youtube.com/vi/{youtubeId}/hqdefault.jpg`) |
| Sort Order | sortOrder | Numeric | Display order |

**Allowed as child of:** `tutorialPage`

---

## ⭐ **WHERE YOUTUBE IDs ARE STORED**

### **Storage Location: Umbraco Content Nodes**

YouTube video IDs are stored in **Umbraco content nodes** using the `tutorial` Document Type:

**Content Tree Structure:**
```
Home
└── Tutorials (tutorialPage)
    ├── Tutorial 1 - Kom igång (tutorial)
    │   └── Property: youtubeId = "dQw4w9WgXcQ"  ← STORED HERE
    ├── Tutorial 2 - Anmälan & Swish (tutorial)
    │   └── Property: youtubeId = "abc123XYZ"    ← STORED HERE
    └── Tutorial 3 - Profil & Dashboard (tutorial)
        └── Property: youtubeId = "xyz789ABC"    ← STORED HERE
```

### **Creating Tutorial Content Nodes**

**In Umbraco Backoffice:**

1. Navigate to **Content → Tutorials**
2. Right-click → Create → **tutorial**
3. Fill in the properties:
   - **Tutorial ID:** `tutorial-02` (must match the ID in `pageTutorialMap`)
   - **Tutorial Title:** `Anmäl dig till tävling & betala med Swish`
   - **Description:** `Lär dig hur du anmäler dig till tävlingar och betalar med Swish`
   - **YouTube Video ID:** `dQw4w9WgXcQ` ⭐ **JUST THE ID, NOT THE FULL URL**
   - **Duration:** `4 min`
   - **Target Role:** `Member`
   - **Sort Order:** `2`
4. **Save & Publish**

### **Important: YouTube Video ID Format**

**✅ CORRECT - Just the ID:**
```
dQw4w9WgXcQ
```

**❌ WRONG - Full URL:**
```
https://www.youtube.com/watch?v=dQw4w9WgXcQ
https://youtu.be/dQw4w9WgXcQ
```

**How to Extract ID from YouTube URL:**
- Full URL: `https://www.youtube.com/watch?v=dQw4w9WgXcQ`
- ID only: `dQw4w9WgXcQ` ← Copy this part

### **How It Works at Runtime**

1. **TutorialHelper loads tutorials** from Umbraco content nodes
2. **Looks up tutorial by ID** (e.g., `'tutorial-02'`)
3. **Gets YouTube ID** from content node (e.g., `'dQw4w9WgXcQ'`)
4. **Constructs embed URL:** `https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1`
5. **Opens modal** with embedded video

**JavaScript Example (from tutorialHelper.js):**
```javascript
// Load tutorial data from Umbraco
loadTutorials: function() {
    // Fetch tutorials from Umbraco content API
    fetch('/umbraco/api/tutorial/GetAll')
        .then(response => response.json())
        .then(data => {
            // Store tutorials in memory
            this.tutorials = data.reduce((acc, tutorial) => {
                acc[tutorial.tutorialId] = {
                    id: tutorial.tutorialId,
                    title: tutorial.tutorialTitle,
                    youtubeId: tutorial.youtubeId,  // ← YouTube ID from Umbraco
                    description: tutorial.tutorialDescription,
                    duration: tutorial.duration
                };
                return acc;
            }, {});
        });
},

// Open tutorial modal
open: function(tutorialId) {
    const tutorial = this.tutorials[tutorialId];
    if (!tutorial) return;

    // Construct YouTube embed URL using the stored ID
    const embedUrl = `https://www.youtube.com/embed/${tutorial.youtubeId}?autoplay=1`;
    document.getElementById('tutorialVideo').src = embedUrl;

    // Show modal
    const modal = new bootstrap.Modal(document.getElementById('tutorialModal'));
    modal.show();
}
```

### **Summary**

| Question | Answer |
|----------|--------|
| **Where are YouTube IDs stored?** | Umbraco content nodes with `tutorial` Document Type |
| **Property name?** | `youtubeId` (Textstring) |
| **Format?** | ID only (e.g., `dQw4w9WgXcQ`), NOT full URL |
| **How to manage?** | Umbraco Backoffice → Content → Tutorials → Edit tutorial node |
| **How loaded?** | TutorialHelper fetches from Umbraco API on page load |

---

## 🔗 **SYSTEM ARCHITECTURE: How Everything Connects**

### **Question: "How is tutorialPage linked to the view where the icon will show?"**

**Answer:** `tutorialPage` is **NOT directly linked** to any view file. The linkage is **convention-based** through matching IDs.

### **The 3-Layer Architecture**

```
┌─────────────────────────────────────────────────────────────────┐
│ LAYER 1: UMBRACO CONTENT (Data Storage)                        │
├─────────────────────────────────────────────────────────────────┤
│ Home                                                            │
│ └── Tutorials (tutorialPage - CONTAINER ONLY, NOT LINKED)      │
│     ├── Tutorial 01 (tutorial Document Type)                   │
│     │   ├─ tutorialId: "tutorial-01"           ← ID STORED     │
│     │   ├─ youtubeId: "dQw4w9WgXcQ"                            │
│     │   └─ title, description, duration, etc.                  │
│     ├── Tutorial 02 (tutorial Document Type)                   │
│     │   ├─ tutorialId: "tutorial-02"           ← ID STORED     │
│     │   ├─ youtubeId: "abc123XYZ"                              │
│     │   └─ title, description, duration, etc.                  │
│     └── ... (13 tutorials total)                               │
└─────────────────────────────────────────────────────────────────┘
                           ↓
              (Fetched via API on page load)
                           ↓
┌─────────────────────────────────────────────────────────────────┐
│ LAYER 2: JAVASCRIPT (Runtime Bridge)                           │
├─────────────────────────────────────────────────────────────────┤
│ tutorialHelper.js:                                              │
│                                                                 │
│ 1. On page load:                                                │
│    fetch('/umbraco/api/tutorial/GetAll')                       │
│    → Loads ALL tutorials into memory                           │
│                                                                 │
│ 2. Stores in object:                                            │
│    this.tutorials = {                                           │
│      'tutorial-01': { youtubeId: 'dQw4w9WgXcQ', ... },         │
│      'tutorial-02': { youtubeId: 'abc123XYZ', ... },           │
│      ...                                                        │
│    }                                                            │
│                                                                 │
│ 3. When icon clicked:                                           │
│    TutorialHelper.open('tutorial-02')                          │
│    → Looks up tutorials['tutorial-02']                         │
│    → Gets youtubeId: 'abc123XYZ'                               │
│    → Constructs: https://youtube.com/embed/abc123XYZ           │
│    → Opens modal with embedded video                           │
└─────────────────────────────────────────────────────────────────┘
                           ↑
               (Called when user clicks icon)
                           ↑
┌─────────────────────────────────────────────────────────────────┐
│ LAYER 3: VIEW FILES (User Interface)                           │
├─────────────────────────────────────────────────────────────────┤
│ CompetitionsHub.cshtml:                                         │
│                                                                 │
│ <i class="bi bi-play-circle-fill tutorial-persistent-icon"     │
│    onclick="TutorialHelper.open('tutorial-02')"  ← ID USED     │
│    data-bs-toggle="tooltip"                      ↑             │
│    data-bs-title="Klicka för instruktioner"></i> MUST MATCH    │
│                                                                 │
│ UserProfile.cshtml:                                             │
│                                                                 │
│ <i class="bi bi-play-circle-fill tutorial-persistent-icon"     │
│    onclick="TutorialHelper.open('tutorial-03')"  ← ID USED     │
│    ...></i>                                                     │
└─────────────────────────────────────────────────────────────────┘
```

### **How the Linkage Works**

**The Linkage is Convention-Based (Like URL Routing):**

1. **No Automatic Connection:**
   - Umbraco content node has property: `tutorialId = "tutorial-02"`
   - View file has onclick: `TutorialHelper.open('tutorial-02')`
   - **Developer must ensure IDs match** (not automatic)

2. **Runtime Resolution:**
   ```
   User clicks icon → JavaScript receives ID "tutorial-02"
                    → Looks up in memory: tutorials["tutorial-02"]
                    → Finds: { youtubeId: "abc123XYZ", ... }
                    → Opens modal with YouTube embed URL
   ```

3. **No Database Relationship:**
   - There's NO foreign key constraint
   - There's NO Umbraco relationship field
   - It's purely **naming convention** (like routing "/competitions" to CompetitionsHub.cshtml)

### **What `tutorialPage` Actually Is**

**Purpose:** Container/hub for organizing tutorials in Umbraco content tree

**What it's NOT:**
- ❌ NOT a template/view file
- ❌ NOT linked to any specific page
- ❌ NOT automatically connected to view files
- ❌ NOT required for tutorials to work (could use any container)

**What it IS:**
- ✅ Organizes tutorial content nodes in content tree
- ✅ Provides a `/tutorials` page for browsing all videos
- ✅ Optional - tutorials could live anywhere in tree
- ✅ Just a convention for organization

**Analogy:** Like a folder on your computer - it organizes files, but doesn't control how programs use those files.

### **Developer Workflow: Adding a New Tutorial**

```
STEP 1: Create Umbraco Content Node
├─ Go to Backoffice → Content → Tutorials
├─ Right-click → Create → tutorial
└─ Fill in:
   ├─ tutorialId: "tutorial-15"      ← Choose unique ID
   ├─ youtubeId: "xYz789"
   ├─ title: "How to do XYZ"
   └─ Save & Publish

STEP 2: Add Icon to View File (with SAME ID)
├─ Open: Views/SomePage.cshtml
└─ Add:
   <i class="bi bi-play-circle-fill tutorial-persistent-icon"
      onclick="TutorialHelper.open('tutorial-15')"></i>
                                      ↑
                                Must match STEP 1 ↑

STEP 3: Test
└─ Load page → Click icon → Should open modal with video
   If error "Tutorial not found": IDs don't match!
```

### **Alternative: Auto-Detection (Optional)**

If you want to avoid hardcoding IDs in every view:

**In tutorialHelper.js:**
```javascript
pageTutorialMap: {
    'competitions': 'tutorial-02',  // Maps URL pattern to tutorial ID
    'user-profile': 'tutorial-03',
    'training-stairs': 'tutorial-05'
}

getCurrentPageId: function() {
    const path = window.location.pathname.toLowerCase();
    if (path.includes('competitions')) return 'competitions';
    if (path.includes('user-profile')) return 'user-profile';
    // ...
}
```

**In view file:**
```html
<!-- No hardcoded ID - uses URL detection -->
<i onclick="TutorialHelper.openCurrentPageTutorial()"></i>
```

**Trade-offs:**
- ✅ Centralized configuration (one place to update)
- ✅ Less repetition in view files
- ❌ More complex (URL detection logic)
- ❌ Can break if URLs change

**Recommendation:** Use **explicit IDs** (hardcoded) for clarity and reliability.

---

**Create Document Type:** `tutorialPage`

**Purpose:** Container for all tutorials (hub page)

**Properties:**
- Standard page properties (title, description)

**Allowed children:** `tutorial`

**Template:** `TutorialPage.cshtml`

---

### **Content Structure to Create**

```
Home
└── Tutorials (tutorialPage)
    ├── Tutorial 1 - Kom igång (tutorial)
    ├── Tutorial 2 - Anmälan & Swish (tutorial)
    ├── Tutorial 3 - Profil & Dashboard (tutorial)
    └── ... (all other tutorials)
```

---

## **Phase 4: Testing Checklist**

**Video Production:**
- [ ] All 13 tutorial videos recorded
- [ ] Videos uploaded to YouTube
- [ ] YouTube IDs documented
- [ ] Video visibility set to Public or Unlisted
- [ ] Thumbnails look good

**Umbraco Configuration:**
- [ ] Member properties added to hpskMember type
- [ ] `tutorial` document type created with all properties
- [ ] `tutorialPage` document type created
- [ ] Tutorial page content node created
- [ ] All tutorial content nodes created with YouTube IDs

**Code Implementation:**
- [ ] TutorialPage.cshtml created and displays tutorials correctly
- [ ] TutorialModal.cshtml created and styled
- [ ] TutorialController.cs created with all endpoints
- [ ] tutorialHelper.js created and initialized
- [ ] tutorial.css created and linked
- [ ] Help dropdown added to _Layout.cshtml header
- [ ] Help icons added to all target pages
- [ ] Tutorial modal included in layout

**Functionality Testing:**
- [ ] Help icons visible on all target pages
- [ ] Clicking help icon opens modal with correct video
- [ ] YouTube video plays in modal
- [ ] "Mark as watched" button works
- [ ] Watched status persists after page reload
- [ ] First-time welcome modal shows once for new users
- [ ] Welcome modal can be dismissed
- [ ] Dismissible banners stay dismissed
- [ ] Help dropdown shows contextual tutorials
- [ ] Tutorial page lists all videos correctly
- [ ] Tutorial filtering by role works
- [ ] Tutorial search works (if implemented)

**User Experience:**
- [ ] All text in Swedish
- [ ] Mobile responsiveness tested
- [ ] Modals work on mobile devices
- [ ] Icons visible but not intrusive
- [ ] Loading performance acceptable
- [ ] No console errors
- [ ] Analytics tracking works (if implemented)

**Security & Permissions:**
- [ ] Non-logged-in users see appropriate tutorials
- [ ] Club admin tutorials only visible to club admins
- [ ] Site admin tutorials only visible to site admins
- [ ] Member properties secured (can't be edited by users directly)

---

## **Quick Reference: Page-to-Tutorial Mapping**

### **Complete Mapping Table**

| Page/View File | Page ID | URL Pattern | Tutorial ID | Tutorial Title | Duration |
|----------------|---------|-------------|-------------|----------------|----------|
| CompetitionsHub.cshtml | `competitions` | `/competitions`, `/tavlingar` | `tutorial-02` | Anmäl dig till tävling & betala med Swish | 4 min |
| UserProfile.cshtml | `user-profile` | `/user-profile`, `/profil` | `tutorial-03` | Din profil & instrumentpanel | 6 min |
| TrainingStairs.cshtml | `training-stairs` | `/training-stairs`, `/skyttetrappan` | `tutorial-05` | Skyttetrappan - Så fungerar det | 7 min |
| ClubAdmin.cshtml | `club-admin` | `/club-admin`, `/klubbadmin` | `tutorial-07` | Klubbadministration - Godkänna medlemmar | 5 min |
| Club.cshtml (Events tab) | `club-events` | `/club/*events*` | `tutorial-09` | Hantera klubbevenemang | 5 min |
| AdminPage.cshtml | `admin` | `/admin` | `tutorial-11` | Skapa tävling | 8 min |

### **Implementation Methods**

**Method 1: Hardcoded (Explicit) - RECOMMENDED**
```html
<!-- In view file, explicitly specify tutorial ID -->
<i class="bi bi-play-circle-fill tutorial-persistent-icon"
   onclick="TutorialHelper.open('tutorial-02')"></i>
```
✅ **Pros:** Clear, easy to debug, no magic
❌ **Cons:** Need to update each view file individually

**Method 2: Auto-Detected (Dynamic)**
```html
<!-- In view file, use auto-detection -->
<i class="bi bi-play-circle-fill tutorial-persistent-icon"
   onclick="TutorialHelper.openCurrentPageTutorial()"></i>
```
✅ **Pros:** Centralized configuration in tutorialHelper.js
❌ **Cons:** URL detection can fail if routing changes

**JavaScript Configuration (tutorialHelper.js):**
```javascript
pageTutorialMap: {
    'competitions': 'tutorial-02',
    'user-profile': 'tutorial-03',
    'training-stairs': 'tutorial-05',
    'club-admin': 'tutorial-07',
    'club-events': 'tutorial-09',
    'admin': 'tutorial-11'
}
```

**URL Detection Logic (getCurrentPageId):**
```javascript
const path = window.location.pathname.toLowerCase();
if (path.includes('competitions') || path.includes('tavlingar')) return 'competitions';
if (path.includes('user-profile') || path.includes('profil')) return 'user-profile';
// ... etc
```

---

## **Handling Multiple Tutorials Per Page** ⚠️

### **Problem: Pages with Multiple Sections**

Some pages have **multiple partials/sections** that each need different tutorials:

**Example: UserProfile.cshtml**
- Dashboard tab → tutorial-03 (Profil & Dashboard)
- Training Results tab → tutorial-04 (Registrera träningsresultat)

**Current limitation:** Only ONE persistent icon per page → Can only open ONE tutorial

### **Solution: Context-Aware Icon** 🎯

The persistent icon **changes behavior** based on which tab/section is active:

```javascript
// Add to tutorialHelper.js

// Get active section/tab on current page
getActiveSection: function() {
    // Check which tab is active (for tabbed interfaces)
    const activeTab = document.querySelector('.nav-link.active');
    if (activeTab) {
        const tabId = activeTab.getAttribute('id');
        if (tabId === 'dashboard-tab') return 'dashboard';
        if (tabId === 'training-results-tab') return 'training-results';
        if (tabId === 'profile-tab') return 'profile';
    }
    return null;
},

// Extended page-to-tutorial mapping with sections
pageTutorialMap: {
    'competitions': 'tutorial-02',
    'user-profile': {
        default: 'tutorial-03',           // Default tab (Dashboard)
        'dashboard': 'tutorial-03',       // Dashboard tab
        'training-results': 'tutorial-04', // Training Results tab
        'profile': 'tutorial-03'          // Profile tab
    },
    'training-stairs': 'tutorial-05',
    'club-admin': 'tutorial-07'
},

// Updated tutorial lookup with section awareness
getTutorialForCurrentPage: function() {
    const pageId = this.getCurrentPageId();
    const mapping = this.pageTutorialMap[pageId];

    if (!mapping) return null;

    // Simple string mapping
    if (typeof mapping === 'string') {
        return mapping;
    }

    // Object mapping with sections
    if (typeof mapping === 'object') {
        const section = this.getActiveSection();
        return mapping[section] || mapping.default || null;
    }

    return null;
}
```

**Usage in UserProfile.cshtml:**
```html
<!-- Single persistent icon - behavior changes based on active tab -->
<i class="bi bi-play-circle-fill tutorial-persistent-icon"
   onclick="TutorialHelper.openCurrentPageTutorial()"
   data-bs-toggle="tooltip"
   data-bs-title="Klicka för instruktioner"></i>

<!-- Tabs -->
<ul class="nav nav-tabs">
    <li class="nav-item">
        <button class="nav-link active" id="dashboard-tab"
                data-bs-toggle="tab" data-bs-target="#dashboard">
            Dashboard
        </button>
    </li>
    <li class="nav-item">
        <button class="nav-link" id="training-results-tab"
                data-bs-toggle="tab" data-bs-target="#trainingResults">
            Träningsresultat
        </button>
    </li>
</ul>
```

✅ **Benefits:** One icon, smart behavior, clean UI, adapts automatically to active tab

### **Example: UserProfile.cshtml with Context-Aware Icon**

```cshtml
<div class="container py-4">
    <!-- Context-aware persistent icon -->
    <i class="bi bi-play-circle-fill tutorial-persistent-icon"
       onclick="TutorialHelper.openCurrentPageTutorial()"
       data-bs-toggle="tooltip"
       data-bs-placement="left"
       data-bs-title="Klicka för instruktioner"
       id="tutorial-icon-profile"></i>

    <h1 class="mb-4">Min Profil</h1>

    <!-- Navigation Tabs -->
    <ul class="nav nav-tabs mb-4">
        <li class="nav-item">
            <button class="nav-link active" id="dashboard-tab"
                    data-bs-toggle="tab" data-bs-target="#dashboard">
                Dashboard
            </button>
        </li>
        <li class="nav-item">
            <button class="nav-link" id="training-results-tab"
                    data-bs-toggle="tab" data-bs-target="#trainingResults">
                Träningsresultat
            </button>
        </li>
        <li class="nav-item">
            <button class="nav-link" id="profile-tab"
                    data-bs-toggle="tab" data-bs-target="#profile">
                Profil
            </button>
        </li>
    </ul>

    <!-- Tab Content -->
    <div class="tab-content">
        <div class="tab-pane fade show active" id="dashboard">
            <!-- Dashboard content - icon shows tutorial-03 -->
        </div>
        <div class="tab-pane fade" id="trainingResults">
            <!-- Training Results content - icon shows tutorial-04 -->
        </div>
        <div class="tab-pane fade" id="profile">
            <!-- Profile content - icon shows tutorial-03 -->
        </div>
    </div>

    <script>
        // Update icon tooltip when tab changes
        document.querySelectorAll('[data-bs-toggle="tab"]').forEach(tab => {
            tab.addEventListener('shown.bs.tab', function() {
                // Icon tooltip can optionally update based on active tab
                const tooltip = bootstrap.Tooltip.getInstance(document.getElementById('tutorial-icon-profile'));
                if (tooltip) {
                    tooltip.dispose();
                }
                new bootstrap.Tooltip(document.getElementById('tutorial-icon-profile'));
            });
        });
    </script>
</div>
```

---

## **Implementation Timeline Estimate**

**After videos are ready:**

1. **Umbraco Configuration** (30 minutes)
   - Add member properties
   - Create document types
   - Create content structure
   - Add tutorial content nodes

2. **Core Files Creation** (3-4 hours)
   - TutorialPage.cshtml
   - TutorialModal.cshtml
   - TutorialController.cs
   - tutorialHelper.js
   - tutorial.css

3. **Modify Existing Pages** (3-4 hours)
   - _Layout.cshtml (header dropdown)
   - CompetitionsHub.cshtml
   - UserProfile.cshtml
   - Competition.cshtml
   - TrainingStairs.cshtml
   - ClubAdmin.cshtml
   - Club.cshtml

4. **Testing & Refinement** (2-3 hours)
   - Functionality testing
   - Mobile responsiveness
   - Bug fixes
   - UX improvements

**Total Estimated Time:** 8-12 hours

---

## **Future Enhancements (Optional)**

- **Analytics Dashboard:** Track which tutorials are most watched
- **Tutorial Completion Tracking:** Track % of video watched
- **Auto-recommendations:** Suggest tutorials based on user behavior
- **In-app Tooltips:** Interactive tooltips with video links
- **Tutorial Ratings:** Allow users to rate helpfulness
- **Multi-language Support:** Add English subtitles or separate English videos
- **Automatic Updates:** Notification when new tutorials are added
- **Tutorial Playlists:** Curated learning paths for different user types
- **Downloadable Cheat Sheets:** PDF guides to supplement videos

---

## **Notes for Video Production**

**Recording Tips:**
- Use screen recording software (OBS Studio, Camtasia, etc.)
- Record in 1920x1080 resolution minimum
- Use Swedish language throughout
- Speak clearly and at moderate pace
- Show mouse cursor movements
- Highlight important areas with zoom or annotations
- Keep videos focused (one topic per video)
- Add intro/outro with HPSK branding
- Test audio quality before recording

**Video Structure:**
1. **Intro (10 seconds):** "Välkommen! I den här guiden visar jag..."
2. **Main Content (3-7 minutes):** Step-by-step demonstration
3. **Recap (20 seconds):** "Sammanfattning: Du lärde dig..."
4. **Outro (10 seconds):** "Tack för att du tittade! Fler guider finns på..."

**YouTube Optimization:**
- Use descriptive titles in Swedish
- Add detailed descriptions with timestamps
- Create custom thumbnails with text overlay
- Add Swedish subtitles (auto-generated or manual)
- Create playlist: "HPSK Site Tutorials"
- Set visibility to "Public" or "Unlisted" (unlisted = only people with link can see)

---

 Tutorial ID Reference Guide

  | View File                                       | Tutorial ID | Tutorial Topic                                |
  |-------------------------------------------------|-------------|-----------------------------------------------|
  | Views/HomePage.cshtml                           | tutorial-01 | Kom igång med HPSK-sidan (Welcome/Navigation) |
  | Views/CompetitionsHub.cshtml                    | tutorial-02 | Anmälan & Swish (Competition Registration)    |
  | Views/UserProfile.cshtml                        | tutorial-03 | Profil & Dashboard                            |
  | Views/UserProfile.cshtml (Training Results tab) | tutorial-04 | Registrera träningsresultat                   |
  | Views/TrainingStairs.cshtml                     | tutorial-05 | Skyttetrappan (Training System)               |
  | Views/ClubAdmin.cshtml                          | tutorial-07 | Godkänna medlemmar (Club Admin)               |
  | Views/Club.cshtml (Events section)              | tutorial-09 | Hantera evenemang (Event Management)          |
  | Views/AdminPage.cshtml                          | tutorial-11 | Skapa tävling (Competition Creation)          |
  
---

## **REVISION SUMMARY** (2025-12-09)

### **What Changed**

**Original Design:**
- Dismissible banner on first visit
- No persistent help indicator after dismissal
- Users had no way to re-access tutorials

**Revised Design (Two-Stage Approach):**
1. **Stage 1:** Eye-catching banner (same as before)
2. **Stage 2:** Persistent fixed icon (NEW)
   - Position: Top-right corner (fixed)
   - Opacity: 60% (discrete)
   - Mobile: Hidden on screens < 768px
   - Animation: 6-second pulse on first show
   - Tooltip: "Klicka för instruktioner"

### **Key Benefits**

✅ **Always Accessible:** Help never disappears
✅ **Discrete:** Low opacity doesn't clutter UI
✅ **Mobile-Friendly:** Hidden on small screens
✅ **Intuitive:** Play icon = video universally understood
✅ **Progressive Disclosure:** Banner creates awareness, icon provides access

### **Implementation Status**

- ✅ UX design finalized
- ✅ CSS specifications complete
- ✅ JavaScript implementation ready
- ✅ Example code provided
- ⏳ Awaiting video production (Phase 1)

---

**Last Updated:** 2025-12-09 (Revised with persistent icon design)
**Original Plan Date:** 2025-11-23
**Status:** Ready for implementation after video production
**Document Location:** `Documentation/VIDEO_TUTORIAL_IMPLEMENTATION_TODO.md`
