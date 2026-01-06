// ============================================
// HPSK Video Tutorial System - JavaScript Helper
// ============================================

const TutorialHelper = {
    // Tutorial metadata (populated from Umbraco on page load)
    tutorials: {},

    // Current tutorial being viewed
    currentTutorialId: null,

    // Page-to-Tutorial mapping configuration
    // Maps page identifiers to tutorial IDs
    pageTutorialMap: {
        'home': 'tutorial-01',              // Kom igång med HPSK-sidan
        'competitions': 'tutorial-02',      // Anmälan & Swish
        'user-profile': {
            default: 'tutorial-03',         // Default: Profil & Dashboard
            'dashboard': 'tutorial-03',     // Dashboard tab
            'profile': 'tutorial-03'        // Profile tab
        },
        'training-match': 'tutorial-04',    // Träningsmatch
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

        // Add keyboard support (ESC to close)
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                const overlay = document.getElementById('tutorialModal');
                if (overlay && overlay.style.display === 'flex') {
                    this.closeModal();
                }
            }
        });
    },

    // Load tutorial metadata from Umbraco
    loadTutorials: function() {
        // Load tutorials from Umbraco API
        fetch('/umbraco/surface/Tutorial/GetAll')
            .then(response => response.json())
            .then(data => {
                if (data.success && data.tutorials) {
                    this.tutorials = data.tutorials.reduce((acc, tutorial) => {
                        acc[tutorial.tutorialId] = {
                            id: tutorial.tutorialId,
                            title: tutorial.tutorialTitle,
                            youtubeId: tutorial.youtubeId,
                            description: tutorial.tutorialDescription,
                            duration: tutorial.duration,
                            targetRole: tutorial.targetRole
                        };
                        return acc;
                    }, {});
                } else {
                    console.error('Failed to load tutorials:', data.message);
                }
            })
            .catch(error => console.error('Error loading tutorials:', error));
    },

    // Open tutorial overlay
    open: function(tutorialId) {
        const tutorial = this.tutorials[tutorialId];
        if (!tutorial) {
            console.error('Tutorial not found:', tutorialId);
            alert('Tutorial hittades inte: ' + tutorialId);
            return;
        }

        // Set overlay content
        document.getElementById('tutorialModalLabel').textContent = tutorial.title;
        document.getElementById('tutorialDescription').textContent = tutorial.description;

        // Construct YouTube embed URL
        const embedUrl = `https://www.youtube.com/embed/${tutorial.youtubeId}?autoplay=1`;
        document.getElementById('tutorialVideo').src = embedUrl;

        // Store current tutorial ID
        this.currentTutorialId = tutorialId;

        // Show overlay
        const overlayEl = document.getElementById('tutorialModal');
        overlayEl.style.display = 'flex';
        document.body.style.overflow = 'hidden'; // Prevent background scrolling

        // Setup mark as watched button
        const markWatchedBtn = document.getElementById('markWatchedBtn');
        markWatchedBtn.onclick = () => this.markAsWatched(tutorialId);
    },

    // Close tutorial overlay
    closeModal: function() {
        const overlayEl = document.getElementById('tutorialModal');
        overlayEl.style.display = 'none';
        document.body.style.overflow = ''; // Restore scrolling

        // Clear video
        document.getElementById('tutorialVideo').src = '';
    },

    // Mark tutorial as watched
    markAsWatched: function(tutorialId) {
        // Call API to save watched status
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
        .catch(error => {
            console.error('Error marking tutorial as watched:', error);
            // Fallback to localStorage for non-logged-in users
            this.markAsWatchedLocal(tutorialId);
        });
    },

    // Mark as watched in localStorage (fallback)
    markAsWatchedLocal: function(tutorialId) {
        const watched = JSON.parse(localStorage.getItem('watchedTutorials') || '[]');
        if (!watched.includes(tutorialId)) {
            watched.push(tutorialId);
            localStorage.setItem('watchedTutorials', JSON.stringify(watched));
        }
        this.updateWatchedUI(tutorialId);
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

    // Dismiss help banner
    dismissBanner: function(bannerId) {
        // Hide banner (use the centralized tutorial-banner ID)
        const banner = document.getElementById('tutorial-banner');
        if (banner) banner.style.display = 'none';

        // Save to localStorage
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
        }).catch(error => console.log('Could not save banner dismissal to server:', error));
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
        // Extract page identifier from URL
        const path = window.location.pathname.toLowerCase();

        // Check for home page first
        if (path === '/' || path === '' || path === '/home') return 'home';

        if (path.includes('competitions') || path.includes('tavlingar')) return 'competitions';
        if (path.includes('user-profile') || path.includes('profil')) return 'user-profile';
        if (path.includes('traningsmatch') || path.includes('training-match')) return 'training-match';
        if (path.includes('training-stairs') || path.includes('skyttetrappan') || path.includes('training-page')) return 'training-stairs';
        if (path.includes('club-admin') || path.includes('klubbadmin')) return 'club-admin';
        if (path.includes('club') && path.includes('events')) return 'club-events';
        if (path.includes('admin')) return 'admin';

        return null; // No page ID match
    },

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

    // Get tutorial ID for current page
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
    },

    // Open tutorial for current page (convenience method)
    openCurrentPageTutorial: function() {
        const tutorialId = this.getTutorialForCurrentPage();
        if (tutorialId) {
            this.open(tutorialId);
        } else {
            console.warn('No tutorial configured for current page:', this.getCurrentPageId());
            alert('Ingen handledning finns tillgänglig för denna sida ännu.');
        }
    },

    // Open tutorial and dismiss banner for current page
    openCurrentPageTutorialAndDismissBanner: function() {
        const tutorialId = this.getTutorialForCurrentPage();
        const pageId = this.getCurrentPageId();
        if (tutorialId && pageId) {
            this.open(tutorialId);
            this.dismissBanner(pageId);
        }
    },

    // Dismiss banner for current page
    dismissCurrentPageBanner: function() {
        const pageId = this.getCurrentPageId();
        if (pageId) {
            this.dismissBanner(pageId);
        }
    },

    // Initialize tutorial elements for current page
    initPageTutorial: function() {
        const pageId = this.getCurrentPageId();
        const tutorialId = this.getTutorialForCurrentPage();

        // No tutorial configured for this page - hide everything
        if (!tutorialId || !pageId) {
            return;
        }

        // Wait for tutorials to load
        const checkTutorials = setInterval(() => {
            if (Object.keys(this.tutorials).length > 0) {
                clearInterval(checkTutorials);

                const tutorial = this.tutorials[tutorialId];
                if (!tutorial) {
                    console.warn('Tutorial not found:', tutorialId);
                    return;
                }

                // Show persistent icon
                const icon = document.getElementById('tutorial-icon');
                if (icon) {
                    icon.style.display = 'flex';

                    // Initialize tooltip
                    new bootstrap.Tooltip(icon);

                    // Check if we should pulse the icon
                    const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');
                    if (dismissed.includes(pageId)) {
                        const seenIconKey = `seenIcon_${pageId}`;
                        const hasSeenIcon = localStorage.getItem(seenIconKey);
                        if (!hasSeenIcon) {
                            icon.classList.add('first-show');
                            setTimeout(() => {
                                icon.classList.remove('first-show');
                            }, 6000);
                            localStorage.setItem(seenIconKey, 'true');
                        }
                    }
                }

                // Show banner if not dismissed
                const dismissed = JSON.parse(localStorage.getItem('dismissedBanners') || '[]');
                if (!dismissed.includes(pageId)) {
                    const banner = document.getElementById('tutorial-banner');
                    if (banner) {
                        // Set banner text based on tutorial
                        const descEl = document.getElementById('tutorial-banner-description');
                        if (descEl && tutorial.description) {
                            descEl.textContent = tutorial.description;
                        }

                        banner.style.display = 'flex';
                    }
                }
            }
        }, 100);

        // Timeout after 5 seconds
        setTimeout(() => clearInterval(checkTutorials), 5000);
    },

    // Check if first-time welcome should be shown
    checkFirstTimeWelcome: function() {
        // This would check the member property on page load
        // Implementation depends on how you pass server data to client
        // For now, check localStorage
        const hasSeenWelcome = localStorage.getItem('firstLoginWelcomeShown');
        if (!hasSeenWelcome && this.tutorials['tutorial-01']) {
            // Show welcome modal after a delay
            setTimeout(() => {
                if (confirm('Välkommen till HPSK! Vill du se en snabb genomgång av sidan?')) {
                    this.open('tutorial-01');
                }
                localStorage.setItem('firstLoginWelcomeShown', 'true');
            }, 2000);
        }
    }
};

// Initialize on page load
document.addEventListener('DOMContentLoaded', function() {
    TutorialHelper.init();
});
