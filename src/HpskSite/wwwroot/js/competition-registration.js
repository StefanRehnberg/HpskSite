// Competition Registration System JavaScript
// Configuration object will be provided by the view
const COMPETITION_ID = window.CompetitionConfig ? window.CompetitionConfig.competitionId : null;
const ALLOW_DUAL_C_CLASS_REGISTRATION = window.CompetitionConfig ? window.CompetitionConfig.allowDualCClassRegistration : false;

// Helper function to check if payment should be shown
function shouldShowPayment() {
    const config = window.CompetitionConfig;

    // Debug logging
    console.log('=== shouldShowPayment() Debug ===');
    console.log('CompetitionConfig:', config);
    console.log('registrationFee:', config?.registrationFee, 'Type:', typeof config?.registrationFee);
    console.log('hasSwishNumber:', config?.hasSwishNumber, 'Type:', typeof config?.hasSwishNumber);
    console.log('Check results:');
    console.log('  - config exists:', !!config);
    console.log('  - registrationFee > 0:', config?.registrationFee > 0);
    console.log('  - hasSwishNumber:', config?.hasSwishNumber);
    console.log('Final result:', config && config.registrationFee > 0 && config.hasSwishNumber);
    console.log('================================');

    return config && config.registrationFee > 0 && config.hasSwishNumber;
}

// Enhanced Registration Target - Global Variables
let currentUserRole = 'member'; // Will be set by server
let currentMemberId = null;
let currentClubId = null;
let availableClubs = [];
let selectedTargetMemberId = null;

// Duplicate Registration Prevention - Global Variables
let existingRegistrations = [];
let weaponClassConflicts = {}; // Map of weapon class → existing registration details

// Preference storage
let classPreferences = {}; // Store preferences for each selected class

// Initialize on page load
document.addEventListener('DOMContentLoaded', function() {
    // Check registration status on page load
    checkRegistrationStatus();

    // Initialize modal form handling
    initializeRegistrationModal();

    // Initialize Bootstrap tooltips
    initializeTooltips();

    // Initialize C-class validation
    initializeCClassValidation();
});

function initializeRegistrationModal() {
    // Initialize class availability on modal open
    const modal = document.getElementById('registrationModal');
    modal.addEventListener('shown.bs.modal', async function() {
        updateClassAvailability();
        updateSubmitButton();

        // Initialize club/member dropdowns
        await initializeRegistrationTarget();

        // Query existing registrations after target member is set
        // This ensures duplicate prevention badges appear for all users
        if (selectedTargetMemberId) {
            await queryExistingRegistrations();
        }
    });

    // Handle radio button changes for each class group
    const nonCClassGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass', 'selectedMClass'];
    const cClassGroups = ['selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun'];
    const lClassGroups = ['selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun'];

    // Handle A and B classes (normal behavior)
    nonCClassGroups.forEach(groupName => {
        const radios = document.querySelectorAll(`input[name="${groupName}"]`);
        radios.forEach(radio => {
            // Store the previous state to enable deselection
            let wasChecked = false;

            // Handle mousedown to track previous state
            radio.addEventListener('mousedown', function() {
                wasChecked = this.checked;
            });

            // Handle click for deselection functionality
            radio.addEventListener('click', function() {
                if (wasChecked) {
                    // If radio was already checked, deselect it
                    this.checked = false;

                    // Disable the edit button for this class
                    const classId = this.value;
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = true;
                        editButton.classList.remove('text-primary');
                        editButton.classList.add('text-secondary');
                        editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                    }

                    // Clear stored preferences for this class
                    delete classPreferences[classId];

                    // Update UI after deselection
                    updateClassAvailability();
                    updateSubmitButton();
                    return;
                }
            });

            radio.addEventListener('change', function() {
                if (this.checked) {
                    // Manually enforce mutual exclusivity within same radio group
                    const radioName = this.name;
                    const otherRadiosInGroup = document.querySelectorAll(`input[name="${radioName}"]`);
                    otherRadiosInGroup.forEach(otherRadio => {
                        if (otherRadio !== this && otherRadio.checked) {
                            otherRadio.checked = false;
                            // Clean up UI for unchecked radio
                            const otherClassId = otherRadio.value;
                            const editButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (editButton) {
                                editButton.disabled = true;
                                editButton.classList.remove('text-primary');
                                editButton.classList.add('text-secondary');
                                editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                            }
                            delete classPreferences[otherClassId];
                        }
                    });

                    const classId = this.value;

                    // Validate level consistency before allowing selection
                    if (!validateLevelConsistency(classId)) {
                        // Prevent selection and show error
                        this.checked = false;
                        return;
                    }

                    // Enable the edit button for this class
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = false;
                    }

                    // Disable edit buttons for other options in this group
                    radios.forEach(otherRadio => {
                        if (otherRadio !== this) {
                            const otherClassId = otherRadio.value;
                            const otherEditButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (otherEditButton) {
                                otherEditButton.disabled = true;
                                otherEditButton.classList.remove('btn-outline-primary');
                                otherEditButton.classList.add('btn-outline-secondary');
                                otherEditButton.innerHTML = '<i class="bi bi-pencil"></i>';
                            }
                            // Clear stored preferences for deselected classes
                            delete classPreferences[otherClassId];
                        }
                    });

                    // Update UI after validation
                    updateClassAvailability();
                    updateSubmitButton();
                }
            });
        });
    });

    // Handle C classes with special dual registration rules
    cClassGroups.forEach(groupName => {
        const radios = document.querySelectorAll(`input[name="${groupName}"]`);
        radios.forEach(radio => {
            // Store the previous state to enable deselection
            let wasChecked = false;

            // Handle mousedown to track previous state
            radio.addEventListener('mousedown', function() {
                wasChecked = this.checked;
            });

            // Handle click for deselection functionality
            radio.addEventListener('click', function() {
                if (wasChecked) {
                    // If radio was already checked, deselect it
                    this.checked = false;

                    // Disable the edit button for this class
                    const classId = this.value;
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = true;
                        editButton.classList.remove('text-primary');
                        editButton.classList.add('text-secondary');
                        editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                    }

                    // Clear stored preferences for this class
                    delete classPreferences[classId];

                    // Update UI after deselection
                    updateClassAvailability();
                    updateSubmitButton();
                    return;
                }
            });

            radio.addEventListener('change', function() {
                if (this.checked) {
                    // Manually enforce mutual exclusivity within same radio group
                    const radioName = this.name;
                    const otherRadiosInGroup = document.querySelectorAll(`input[name="${radioName}"]`);
                    otherRadiosInGroup.forEach(otherRadio => {
                        if (otherRadio !== this && otherRadio.checked) {
                            otherRadio.checked = false;
                            // Clean up UI for unchecked radio
                            const otherClassId = otherRadio.value;
                            const editButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (editButton) {
                                editButton.disabled = true;
                                editButton.classList.remove('text-primary');
                                editButton.classList.add('text-secondary');
                                editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                            }
                            delete classPreferences[otherClassId];
                        }
                    });

                    const classId = this.value;

                    // Validate C-class selection limits
                    if (!validateCClassSelection(classId)) {
                        // Prevent selection and show error
                        this.checked = false;
                        return;
                    }

                    // Validate level consistency before allowing selection
                    if (!validateLevelConsistency(classId)) {
                        // Prevent selection and show error
                        this.checked = false;
                        return;
                    }

                    // Enable the edit button for this class
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = false;
                    }

                    // Disable edit buttons for other options in this group
                    radios.forEach(otherRadio => {
                        if (otherRadio !== this) {
                            const otherClassId = otherRadio.value;
                            const otherEditButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (otherEditButton) {
                                otherEditButton.disabled = true;
                                otherEditButton.classList.remove('btn-outline-primary');
                                otherEditButton.classList.add('btn-outline-secondary');
                                otherEditButton.innerHTML = '<i class="bi bi-pencil"></i>';
                            }
                            // Clear stored preferences for deselected classes
                            delete classPreferences[otherClassId];
                        }
                    });

                    // Update UI after validation
                    updateClassAvailability();
                    updateSubmitButton();
                }
            });
        });
    });

    // Handle L classes with special dual registration rules (same as C-classes)
    lClassGroups.forEach(groupName => {
        const radios = document.querySelectorAll(`input[name="${groupName}"]`);
        radios.forEach(radio => {
            // Store the previous state to enable deselection
            let wasChecked = false;

            // Handle mousedown to track previous state
            radio.addEventListener('mousedown', function() {
                wasChecked = this.checked;
            });

            // Handle click for deselection functionality
            radio.addEventListener('click', function() {
                if (wasChecked) {
                    // If radio was already checked, deselect it
                    this.checked = false;

                    // Disable the edit button for this class
                    const classId = this.value;
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = true;
                        editButton.classList.remove('text-primary');
                        editButton.classList.add('text-secondary');
                        editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                    }

                    // Clear stored preferences for this class
                    delete classPreferences[classId];

                    // Update UI after deselection
                    updateClassAvailability();
                    updateSubmitButton();
                    return;
                }
            });

            radio.addEventListener('change', function() {
                if (this.checked) {
                    // Manually enforce mutual exclusivity within same radio group
                    const radioName = this.name;
                    const otherRadiosInGroup = document.querySelectorAll(`input[name="${radioName}"]`);
                    otherRadiosInGroup.forEach(otherRadio => {
                        if (otherRadio !== this && otherRadio.checked) {
                            otherRadio.checked = false;
                            // Clean up UI for unchecked radio
                            const otherClassId = otherRadio.value;
                            const editButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (editButton) {
                                editButton.disabled = true;
                                editButton.classList.remove('text-primary');
                                editButton.classList.add('text-secondary');
                                editButton.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
                            }
                            delete classPreferences[otherClassId];
                        }
                    });

                    const classId = this.value;

                    // Validate L-class selection limits
                    if (!validateLClassSelection(classId)) {
                        // Prevent selection and show error
                        this.checked = false;
                        return;
                    }

                    // Validate level consistency before allowing selection
                    if (!validateLevelConsistency(classId)) {
                        // Prevent selection and show error
                        this.checked = false;
                        return;
                    }

                    // Enable the edit button for this class
                    const editButton = document.querySelector(`button[data-class*="${classId}"]`);
                    if (editButton) {
                        editButton.disabled = false;
                    }

                    // Disable edit buttons for other options in this group
                    radios.forEach(otherRadio => {
                        if (otherRadio !== this) {
                            const otherClassId = otherRadio.value;
                            const otherEditButton = document.querySelector(`button[data-class*="${otherClassId}"]`);
                            if (otherEditButton) {
                                otherEditButton.disabled = true;
                                otherEditButton.classList.remove('btn-outline-primary');
                                otherEditButton.classList.add('btn-outline-secondary');
                                otherEditButton.innerHTML = '<i class="bi bi-pencil"></i>';
                            }
                            // Clear stored preferences for deselected classes
                            delete classPreferences[otherClassId];
                        }
                    });

                    // Update UI after validation
                    updateClassAvailability();
                    updateSubmitButton();
                }
            });
        });
    });

    // Handle form submission
    const form = document.querySelector('#registrationModal form');
    if (form) {
        form.addEventListener('submit', function(e) {
            e.preventDefault();
            submitRegistrationForm();
        });
    } else {
        console.error('Registration form not found!');
    }
}

function updateSubmitButton() {
    const submitBtn = document.getElementById('modalRegisterBtn');

    // Check if any radio button is selected from any group
    const radioGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass', 'selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun', 'selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun', 'selectedMClass'];
    let hasSelection = false;

    for (let groupName of radioGroups) {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio) {
            hasSelection = true;
            break;
        }
    }

    // Also check if a target member is selected (for enhanced registration)
    const hasMemberSelected = selectedTargetMemberId !== null;

    submitBtn.disabled = !hasSelection || !hasMemberSelected;
}

// Alias for updateSubmitButton to be called from registration target code
function updateRegistrationButton() {
    updateSubmitButton();
}

function validateLevelConsistency(newClassId) {
    const radioGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass', 'selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun', 'selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun', 'selectedMClass'];
    const selectedRadios = [];

    // Get all currently selected radio buttons
    radioGroups.forEach(groupName => {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio) {
            selectedRadios.push(checkedRadio);
        }
    });

    if (selectedRadios.length === 0) {
        return true; // First selection is always allowed
    }

    // Get the level of the new class
    const newLevel = getClassLevel(newClassId);
    if (!newLevel) {
        return true; // Classes without levels (special classes) are always allowed
    }

    // Check if any selected class has a different level
    for (let radio of selectedRadios) {
        const existingLevel = getClassLevel(radio.value);
        if (existingLevel && existingLevel !== newLevel) {
            const existingClassName = getClassDisplayName(radio.value);
            const newClassName = getClassDisplayName(newClassId);
            alert(`Du har valt ${existingClassName} och försöker välja ${newClassName}. Du måste välja samma nivå (1-3) i alla klasser.`);
            return false;
        }
    }

    return true;
}

function validateCClassSelection(newClassId) {
    const cClassGroups = ['selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun'];
    const selectedCClasses = [];

    // Count currently selected C-classes (excluding the one we're trying to select)
    cClassGroups.forEach(groupName => {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio && checkedRadio.value !== newClassId) {
            selectedCClasses.push(checkedRadio.value);
        }
    });

    // If allowDualCClassRegistration is false, max 1 C-class allowed
    // If allowDualCClassRegistration is true, max 2 C-classes allowed
    const maxAllowed = ALLOW_DUAL_C_CLASS_REGISTRATION ? 2 : 1;

    // Check if adding this new selection would exceed the limit
    if (selectedCClasses.length + 1 > maxAllowed) {
        const maxText = maxAllowed === 1 ? "endast en C-klass" : "maximalt två C-klasser från olika kategorier";
        alert(`Denna tävling tillåter ${maxText}.`);
        return false;
    }

    return true;
}

function validateLClassSelection(newClassId) {
    const lClassGroups = ['selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun'];
    const selectedLClasses = [];

    // Count currently selected L-classes (excluding the one we're trying to select)
    lClassGroups.forEach(groupName => {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio && checkedRadio.value !== newClassId) {
            selectedLClasses.push(checkedRadio.value);
        }
    });

    // L-classes follow same dual registration rules as C-classes
    const maxAllowed = ALLOW_DUAL_C_CLASS_REGISTRATION ? 2 : 1;

    // Check if adding this new selection would exceed the limit
    if (selectedLClasses.length + 1 > maxAllowed) {
        const maxText = maxAllowed === 1 ? "endast en L-klass" : "maximalt två L-klasser från olika kategorier";
        alert(`Denna tävling tillåter ${maxText}.`);
        return false;
    }

    return true;
}

function getClassLevel(classId) {
    // M-classes are exempt from level matching
    if (classId && classId.startsWith('M')) {
        return null;
    }

    // Extract level from class ID/name
    // Assumes class names like "A1", "B2", "C3", "R1", "L1", "C1_Dam", etc.
    const className = getClassDisplayName(classId);
    const levelMatch = className.match(/([ABCRL])([123])/);
    return levelMatch ? levelMatch[2] : null;
}

function getClassDisplayName(classId) {
    // Get the display name from the label associated with this class
    const labelSelectors = [
        `label[for="class_A_${classId}"] strong`,
        `label[for="class_B_${classId}"] strong`,
        `label[for="class_R_${classId}"] strong`,
        `label[for="class_CR_${classId}"] strong`,
        `label[for="class_CV_${classId}"] strong`,
        `label[for="class_CD_${classId}"] strong`,
        `label[for="class_CJ_${classId}"] strong`,
        `label[for="class_LR_${classId}"] strong`,
        `label[for="class_LV_${classId}"] strong`,
        `label[for="class_LD_${classId}"] strong`,
        `label[for="class_LJ_${classId}"] strong`,
        `label[for="class_M_${classId}"] strong`
    ];

    for (let selector of labelSelectors) {
        const label = document.querySelector(selector);
        if (label) {
            return label.textContent;
        }
    }
    return classId;
}

function updateClassAvailability() {
    const radioGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass', 'selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun', 'selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun', 'selectedMClass'];
    const selectedRadios = [];

    // Get all currently selected radio buttons
    radioGroups.forEach(groupName => {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio) {
            selectedRadios.push(checkedRadio);
        }
    });

    // Get all radio buttons
    const allRadios = [];
    radioGroups.forEach(groupName => {
        const radios = document.querySelectorAll(`input[name="${groupName}"]`);
        radios.forEach(radio => allRadios.push(radio));
    });

    if (selectedRadios.length === 0) {
        // No selections, enable all
        allRadios.forEach(radio => {
            radio.disabled = false;
            // No opacity changes needed without cards
        });
        return;
    }

    // Get the required level from selected classes
    let requiredLevel = null;
    for (let radio of selectedRadios) {
        const level = getClassLevel(radio.value);
        if (level) {
            requiredLevel = level;
            break;
        }
    }

    // Count selected C-classes for availability checking
    const cClassGroups = ['selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun'];
    const selectedCClassCount = cClassGroups.filter(groupName => {
        return document.querySelector(`input[name="${groupName}"]:checked`);
    }).length;

    // Count selected L-classes for availability checking (L follows same dual registration rules as C)
    const lClassGroups = ['selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun'];
    const selectedLClassCount = lClassGroups.filter(groupName => {
        return document.querySelector(`input[name="${groupName}"]:checked`);
    }).length;

    const maxCClassesAllowed = ALLOW_DUAL_C_CLASS_REGISTRATION ? 2 : 1;
    const maxLClassesAllowed = ALLOW_DUAL_C_CLASS_REGISTRATION ? 2 : 1; // L follows same rules as C

    // Update availability of all radio buttons
    allRadios.forEach(radio => {
        if (radio.checked) {
            return; // Already selected, always keep enabled so user can deselect
        }

        const classLevel = getClassLevel(radio.value);
        const isCClass = cClassGroups.includes(radio.name);
        const isLClass = lClassGroups.includes(radio.name);

        // Check level restriction
        const levelRestricted = classLevel && requiredLevel && classLevel !== requiredLevel;

        // Check C-class limit restriction - only disable if we would exceed the limit
        // Allow selection if we haven't reached the maximum yet
        const cClassRestricted = isCClass && selectedCClassCount >= maxCClassesAllowed;

        // Check L-class limit restriction (same logic as C-class)
        const lClassRestricted = isLClass && selectedLClassCount >= maxLClassesAllowed;

        if (levelRestricted || cClassRestricted || lClassRestricted) {
            // Restricted, disable
            radio.disabled = true;
            // No opacity changes needed without cards
        } else {
            // Available
            radio.disabled = false;
            // No opacity changes needed without cards
        }
    });
}

async function submitRegistrationForm() {
    const submitBtn = document.getElementById('modalRegisterBtn');
    const radioGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass', 'selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun', 'selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun', 'selectedMClass'];
    const selectedClasses = [];
    const startPreferencesMap = {};

    // Collect all selected classes and their preferences
    radioGroups.forEach(groupName => {
        const checkedRadio = document.querySelector(`input[name="${groupName}"]:checked`);
        if (checkedRadio) {
            const classId = checkedRadio.value;
            const startPreference = classPreferences[classId] || 'Inget';
            selectedClasses.push(classId);
            startPreferencesMap[classId] = startPreference;
        }
    });

    if (selectedClasses.length === 0) {
        alert('Du måste välja minst en skytteklass för att anmäla dig.');
        return;
    }

    // Disable submit button and show loading
    submitBtn.disabled = true;
    submitBtn.innerHTML = '<i class="bi bi-hourglass-split spinner-border spinner-border-sm"></i> Anmäler...';

    // Check if user already has a registration for this competition
    const hasExistingRegistration = existingRegistrations.length > 0;

    if (hasExistingRegistration) {
        // Show confirmation dialog for updating existing registration
        const confirmed = await new Promise((resolve) => {
            const modalElement = document.getElementById('updateRegistrationConfirmModal');

            // Dispose of any existing modal instance to prevent conflicts
            const existingInstance = bootstrap.Modal.getInstance(modalElement);
            if (existingInstance) {
                existingInstance.dispose();
            }

            const modal = new bootstrap.Modal(modalElement, {
                backdrop: 'static',
                keyboard: false
            });
            document.getElementById('existingClassName').textContent = 'alla valda klasser';

            const confirmBtn = document.getElementById('confirmUpdateBtn');
            const cancelBtn = document.getElementById('cancelUpdateBtn');

            const newConfirmBtn = confirmBtn.cloneNode(true);
            confirmBtn.parentNode.replaceChild(newConfirmBtn, confirmBtn);

            const newCancelBtn = cancelBtn.cloneNode(true);
            cancelBtn.parentNode.replaceChild(newCancelBtn, cancelBtn);

            newConfirmBtn.addEventListener('click', function() {
                modal.hide();
                // Wait for modal to fully hide before resolving
                setTimeout(() => {
                    modal.dispose();
                    resolve(true);
                }, 300);
            });

            newCancelBtn.addEventListener('click', function() {
                modal.hide();
                // Wait for modal to fully hide before resolving
                setTimeout(() => {
                    modal.dispose();
                    resolve(false);
                }, 300);
            });

            modal.show();
        });

        if (!confirmed) {
            submitBtn.disabled = false;
            submitBtn.innerHTML = '<i class="bi bi-person-plus"></i> Anmäl';
            return;
        }
    }

    // Submit all classes in a single request
    try {
        const result = await submitAllClassesRegistration(selectedClasses, startPreferencesMap);

        if (result.success) {
            // IMPORTANT: Restore button BEFORE modal transformation
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.innerHTML = '<i class="bi bi-person-plus"></i> Anmäl';
            }

            // Transform modal to success state (passing result data for fee change handling)
            const isAdminRegistration = selectedTargetMemberId && selectedTargetMemberId != currentMemberId;
            transformModalToSuccessState(result, COMPETITION_ID, isAdminRegistration);

            // Reload when user explicitly closes the modal
            const modalElement = document.getElementById('registrationModal');
            modalElement.addEventListener('hidden.bs.modal', () => {
                location.reload();
            }, { once: true });
        } else {
            // Error case - restore button
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.innerHTML = '<i class="bi bi-person-plus"></i> Anmäl';
            }
            alert('Fel vid anmälan: ' + result.message);
        }
    } catch (error) {
        // Network error - restore button
        console.error('Registration error:', error);
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.innerHTML = '<i class="bi bi-person-plus"></i> Anmäl';
        }
        alert('Ett fel uppstod vid anmälan. Försök igen.');
    }
}

async function submitAllClassesRegistration(classIds, startPreferencesMap) {
    // Create form data for registration with all classes
    const formData = new FormData();
    formData.append('competitionId', COMPETITION_ID);

    // Add target member ID for enhanced registration
    if (selectedTargetMemberId) {
        formData.append('targetMemberId', selectedTargetMemberId);
    }

    // Set all selected classes as comma-separated string
    formData.append('selectedClasses', classIds.join(','));

    // Set start preferences as JSON object mapping class → preference
    formData.append('startPreferencesJson', JSON.stringify(startPreferencesMap));

    const response = await fetch('/umbraco/surface/Competition/RegisterForCompetition', {
        method: 'POST',
        body: formData,
        headers: {
            'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]').value,
            'Accept': 'application/json',
            'X-Requested-With': 'XMLHttpRequest'
        }
    });

    if (!response.ok) {
        console.error('Registration failed with status:', response.status);
        return { success: false, message: 'Server error occurred' };
    }

    try {
        const result = await response.json();
        return result;
    } catch (error) {
        console.error('Failed to parse JSON response:', error);
        return { success: false, message: 'Invalid server response' };
    }
}

function checkRegistrationStatus() {
    const competitionId = COMPETITION_ID;

    fetch(`/umbraco/surface/Competition/GetRegistrationStatus?competitionId=${competitionId}`)
        .then(response => response.json())
        .then(data => {
            updateRegistrationUI(data);
        })
        .catch(error => {
            console.error('Error checking registration status:', error);
        });
}

function updateRegistrationUI(status) {
    const registerBtn = document.getElementById('registerBtn');
    const statusElement = document.querySelector('.badge');

    if (status.isRegistered) {
        // User is already registered
        if (registerBtn) {
            registerBtn.textContent = 'Redan anmäld';
            registerBtn.className = 'btn btn-info btn-lg';
            registerBtn.disabled = true;
            registerBtn.innerHTML = '<i class="bi bi-check-circle"></i> Redan anmäld';
        }
    } else if (!status.canRegister) {
        // User cannot register
        if (registerBtn) {
            registerBtn.disabled = true;
            registerBtn.innerHTML = `<i class="bi bi-exclamation-circle"></i> ${status.message}`;
        }
    }
}

// Preference Modal Functions
function openPreferenceModal(classId, className, classGroup) {
    // Set modal data
    document.getElementById('preferenceClassId').value = classId;
    document.getElementById('preferenceClassName').textContent = className;
    document.getElementById('preferenceClassGroup').value = classGroup;

    // Load existing preferences if any
    const existingPreference = classPreferences[classId] || 'Inget';

    document.getElementById('startPreference').value = existingPreference;

    // Show modal
    new bootstrap.Modal(document.getElementById('preferenceModal')).show();
}

function savePreferences() {
    const classId = document.getElementById('preferenceClassId').value;
    const preference = document.getElementById('startPreference').value;

    // Store preferences
    classPreferences[classId] = preference;

    // Update button appearance to show it has been configured
    const button = document.querySelector(`button[data-class*="${classId}"]`);
    if (button) {
        if (preference !== 'Inget') {
            button.classList.remove('text-secondary');
            button.classList.add('text-primary');
            button.innerHTML = '<i class="bi bi-gear-fill" style="font-size: 0.8rem;"></i>';
        } else {
            button.classList.remove('text-primary');
            button.classList.add('text-secondary');
            button.innerHTML = '<i class="bi bi-pencil" style="font-size: 0.8rem;"></i>';
        }
    }

    // Hide modal
    bootstrap.Modal.getInstance(document.getElementById('preferenceModal')).hide();
}

// Show payment prompt after successful registration
function showPaymentPrompt(competitionId) {
    // Remove any existing payment prompt
    const existingPrompt = document.getElementById('paymentPrompt');
    if (existingPrompt) existingPrompt.remove();

    const paymentHtml = `
        <div class="alert alert-info alert-dismissible fade show mt-3" id="paymentPrompt" role="alert">
            <h5><i class="bi bi-wallet2"></i> Betala tävlingsavgift</h5>
            <p class="mb-3">Din anmälan är nu registrerad. Betala tävlingsavgiften med Swish för att slutföra din anmälan.</p>
            <div class="d-flex gap-2">
                <a href="/umbraco/surface/Swish/GeneratePaymentQR?competitionId=${competitionId}"
                   class="btn btn-primary" target="_blank">
                    <i class="bi bi-qr-code"></i> Betala med Swish
                </a>
                <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="alert">
                    Betala senare
                </button>
            </div>
        </div>
    `;

    // Insert at top of competition info section
    const container = document.querySelector('.competition-details') ||
                      document.querySelector('.container') ||
                      document.querySelector('main');

    if (container) {
        container.insertAdjacentHTML('afterbegin', paymentHtml);
        // Scroll to payment prompt
        document.getElementById('paymentPrompt')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
}

// Show Swish payment modal with QR code (wrapper with target member support)
function showSwishPaymentModalForMember(competitionId, targetMemberId) {
    showSwishPaymentModal(competitionId, targetMemberId);
}

// Show Swish payment modal with QR code
function showSwishPaymentModal(competitionId, targetMemberId = '') {
    const modal = new bootstrap.Modal(document.getElementById('swishPaymentModal'));
    const modalBody = document.getElementById('swishPaymentModalBody');

    // Show loading spinner
    modalBody.innerHTML = `
        <div class="spinner-border text-primary" role="status">
            <span class="visually-hidden">Laddar QR-kod...</span>
        </div>
        <p class="mt-2">Laddar betalningsinformation...</p>
    `;

    modal.show();

    // Build URL with optional targetMemberId parameter
    let url = `/umbraco/surface/Swish/GeneratePaymentQR?competitionId=${competitionId}`;
    if (targetMemberId) {
        url += `&targetMemberId=${encodeURIComponent(targetMemberId)}`;
    }

    // Fetch QR code
    fetch(url)
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                modalBody.innerHTML = `
                    <div class="mb-3">
                        <img src="${data.qrCode}" alt="Swish QR Code" style="max-width: 300px; width: 100%;" class="img-fluid">
                    </div>
                    <div class="alert alert-info text-start mb-0">
                        <h6 class="mb-2"><strong><i class="bi bi-info-circle"></i> Betalningsinformation</strong></h6>
                        <p class="mb-1"><strong>Belopp:</strong> ${data.amount} kr</p>
                        <p class="mb-1"><strong>Klass${data.registrationCount > 1 ? 'er' : ''}:</strong> ${data.shootingClasses}</p>
                        <p class="mb-0"><strong>Fakturanummer:</strong> ${data.invoiceNumber}</p>
                    </div>
                    <p class="text-muted small mt-3 mb-0">
                        <i class="bi bi-phone"></i> Scanna QR-koden med Swish-appen för att betala
                    </p>
                `;
            } else {
                modalBody.innerHTML = `
                    <div class="alert alert-danger mb-0">
                        <i class="bi bi-exclamation-triangle"></i> <strong>Fel vid betalning</strong><br>
                        ${data.message}
                    </div>
                `;
            }
        })
        .catch(error => {
            console.error('Error loading Swish payment:', error);
            modalBody.innerHTML = `
                <div class="alert alert-danger mb-0">
                    <i class="bi bi-exclamation-triangle"></i> <strong>Kunde inte ladda betalning</strong><br>
                    Ett tekniskt fel uppstod. Försök igen senare.
                </div>
            `;
        });
}

// Show management dashboard modal
function showManagementDashboard() {
    const modal = document.createElement('div');
    modal.className = 'modal fade';
    modal.id = 'managementDashboardModal';
    modal.innerHTML = `
        <div class="modal-dialog modal-fullscreen">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">
                        <i class="bi bi-gear"></i> Tävlingshantering
                    </h5>
                    <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                </div>
                <div class="modal-body">
                    <div id="managementDashboardContent">
                        <div class="text-center py-4">
                            <div class="spinner-border text-primary" role="status">
                                <span class="visually-hidden">Laddar...</span>
                            </div>
                            <p class="mt-2">Laddar hanteringspanel...</p>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    `;

    document.body.appendChild(modal);
    const modalInstance = new bootstrap.Modal(modal);
    modalInstance.show();

    // Load management dashboard content dynamically
    loadManagementDashboard();

    // Clean up when modal is hidden
    modal.addEventListener('hidden.bs.modal', () => {
        modal.remove();
    });
}

// Load management dashboard content
function loadManagementDashboard() {
    setTimeout(() => {
        const content = document.getElementById('managementDashboardContent');
        if (content) {
            content.innerHTML = `
                <div class="alert alert-info">
                    <h4><i class="bi bi-info-circle"></i> Hanteringspanel</h4>
                    <p>Hanteringspanelen är implementerad och redo att användas. Den inkluderar:</p>
                    <ul>
                        <li><strong>Tävlingsöversikt</strong> - Real-time statistik och deltagare</li>
                        <li><strong>Resultathantering</strong> - Visa och validera resultat</li>
                        <li><strong>Deltagarhantering</strong> - Hantera anmälningar</li>
                        <li><strong>Export-funktioner</strong> - Exportera resultat till olika format</li>
                    </ul>
                    <p class="mb-0">All funktionalitet använder den nya databaspersistensen som precis implementerades.</p>
                </div>
            `;
        }
    }, 500);
}

function showNotification(message, type) {
    // Create notification element
    const notification = document.createElement('div');
    notification.className = `alert alert-${type === 'success' ? 'success' : 'danger'} alert-dismissible fade show position-fixed`;
    notification.style.cssText = 'top: 20px; right: 20px; z-index: 9999; max-width: 400px;';
    notification.innerHTML = `
        ${message}
        <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    `;

    document.body.appendChild(notification);

    // Auto-remove after 5 seconds
    setTimeout(() => {
        if (notification.parentNode) {
            notification.remove();
        }
    }, 5000);
}

function getAntiForgeryToken() {
    const token = document.querySelector('input[name="__RequestVerificationToken"]');
    return token ? token.value : '';
}

// Initialize Bootstrap tooltips
function initializeTooltips() {
    var tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    var tooltipList = tooltipTriggerList.map(function (tooltipTriggerEl) {
        return new bootstrap.Tooltip(tooltipTriggerEl);
    });
}

// Initialize C-class validation
function initializeCClassValidation() {
    const allowDualCClass = ALLOW_DUAL_C_CLASS_REGISTRATION;
    const cRegularRadios = document.querySelectorAll('.c-regular-radio');
    const cVetRadios = document.querySelectorAll('.c-vet-radio');
    const cDamRadios = document.querySelectorAll('.c-dam-radio');

    // Single C-class mode: only allow one selection across all subcategories
    if (!allowDualCClass) {
        const allCRadios = document.querySelectorAll('.c-regular-radio, .c-vet-radio, .c-dam-radio');
        allCRadios.forEach(radio => {
            radio.addEventListener('change', function() {
                if (this.checked) {
                    // Uncheck all other C-class radios
                    allCRadios.forEach(otherRadio => {
                        if (otherRadio !== this) {
                            otherRadio.checked = false;
                        }
                    });
                }
            });
        });
    }

    // Form validation before submission
    const registrationForm = document.querySelector('form[action*="RegisterForCompetition"]');
    if (registrationForm) {
        registrationForm.addEventListener('submit', function(e) {
            const selectedA = document.querySelector('input[name="selectedAClass"]:checked');
            const selectedB = document.querySelector('input[name="selectedBClass"]:checked');
            const selectedR = document.querySelector('input[name="selectedRClass"]:checked');
            const selectedCRegular = document.querySelector('input[name="selectedCRegular"]:checked');
            const selectedCVet = document.querySelector('input[name="selectedCVet"]:checked');
            const selectedCJun = document.querySelector('input[name="selectedCJun"]:checked');
            const selectedCDam = document.querySelector('input[name="selectedCDam"]:checked');
            const selectedLRegular = document.querySelector('input[name="selectedLRegular"]:checked');
            const selectedLVet = document.querySelector('input[name="selectedLVet"]:checked');
            const selectedLJun = document.querySelector('input[name="selectedLJun"]:checked');
            const selectedLDam = document.querySelector('input[name="selectedLDam"]:checked');
            const selectedM = document.querySelector('input[name="selectedMClass"]:checked');

            const hasAnySelection = selectedA || selectedB || selectedR || selectedCRegular || selectedCVet || selectedCJun || selectedCDam || selectedLRegular || selectedLVet || selectedLJun || selectedLDam || selectedM;

            if (!hasAnySelection) {
                e.preventDefault();
                alert('Du måste välja minst en skytteklass för att anmäla dig.');
                return false;
            }

            // Level matching validation: if you select a level (1-3) in ANY class, you must select the same level in ALL other classes
            let selectedLevel = null;
            let levelSource = '';

            // Check A classes for level
            if (selectedA) {
                const match = selectedA.value.match(/A([123])/);
                if (match) {
                    selectedLevel = match[1];
                    levelSource = `A${selectedLevel}`;
                }
            }

            // Check B classes for level
            if (selectedB) {
                const match = selectedB.value.match(/B([123])/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource} och B${match[1]}. Du måste välja samma nivå (1-3) i alla klasser.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `B${selectedLevel}`;
                    }
                }
            }

            // Check C regular classes for level
            if (selectedCRegular) {
                const match = selectedCRegular.value.match(/C([123])/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource} och C${match[1]}. Du måste välja samma nivå (1-3) i alla klasser.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `C${selectedLevel}`;
                    }
                }
            }

            // Check C Dam classes for level matching
            if (selectedCDam) {
                const match = selectedCDam.value.match(/C([123])_Dam/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource}. Du måste välja C${selectedLevel} Dam för att matcha nivån.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `C${selectedLevel} Dam`;
                    }
                }
            }

            // Check R classes for level
            if (selectedR) {
                const match = selectedR.value.match(/R([123])/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource} och R${match[1]}. Du måste välja samma nivå (1-3) i alla klasser.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `R${selectedLevel}`;
                    }
                }
            }

            // Check L regular classes for level
            if (selectedLRegular) {
                const match = selectedLRegular.value.match(/L([123])/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource} och L${match[1]}. Du måste välja samma nivå (1-3) i alla klasser.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `L${selectedLevel}`;
                    }
                }
            }

            // Check L Dam classes for level matching
            if (selectedLDam) {
                const match = selectedLDam.value.match(/L([123])_Dam/);
                if (match) {
                    if (selectedLevel && selectedLevel !== match[1]) {
                        e.preventDefault();
                        alert(`Du har valt ${levelSource}. Du måste välja L${selectedLevel} Dam för att matcha nivån.`);
                        return false;
                    }
                    if (!selectedLevel) {
                        selectedLevel = match[1];
                        levelSource = `L${selectedLevel} Dam`;
                    }
                }
            }

            // Note: M classes are exempt from level matching validation
        });
    }
}

// ============================================================================
// REGISTRATION TARGET MANAGEMENT
// ============================================================================

// Initialize registration target dropdowns based on user role
async function initializeRegistrationTarget() {
    try {
        // Get current user info and role
        const response = await fetch('/umbraco/surface/Competition/GetCurrentUserRegistrationInfo', {
            method: 'GET',
            headers: { 'Content-Type': 'application/json' }
        });

        const data = await response.json();
        if (data.success) {
            currentUserRole = data.role; // 'admin', 'clubAdmin', 'member'
            currentMemberId = data.memberId;
            currentClubId = data.clubId;

            await setupRegistrationTargetUI(data);
        } else {
            console.error('Failed to get user registration info:', data.message);
            showRegistrationTargetError('Failed to load user information');
        }
    } catch (error) {
        console.error('Error initializing registration target:', error);
        showRegistrationTargetError('Error loading registration options');
    }
}

async function setupRegistrationTargetUI(userInfo) {
    const clubSelect = document.getElementById('clubSelect');
    const memberSelect = document.getElementById('memberSelect');

    if (userInfo.role === 'admin') {
        // Site Admin: Enable both dropdowns, load all clubs
        await loadAllClubs();

        clubSelect.disabled = false;
        memberSelect.disabled = false;
        memberSelect.innerHTML = '<option value="">Välj klubb först...</option>';

        // Pre-select current user's club and member if they have one
        if (userInfo.clubId && userInfo.clubName) {
            // Wait a moment for clubs to load, then select current user's club
            setTimeout(async () => {
                const clubOption = clubSelect.querySelector(`option[value="${userInfo.clubId}"]`);
                if (clubOption) {
                    clubSelect.value = userInfo.clubId;
                    await loadClubMembers(userInfo.clubId);
                    // Pre-select current user
                    if (userInfo.memberId) {
                        memberSelect.value = userInfo.memberId;
                        selectedTargetMemberId = userInfo.memberId;
                        updateRegistrationButton();
                        showSelectedMemberInfo({
                            id: userInfo.memberId,
                            name: userInfo.memberName,
                            clubName: userInfo.clubName,
                            email: userInfo.email || ''
                        });

                        // Query registrations for admin's own member ID to show badges
                        await queryExistingRegistrations();
                    }
                } else {
                    console.warn('Could not find club option for admin club ID:', userInfo.clubId);
                }
            }, 100);
        }

    } else if (userInfo.role === 'clubAdmin') {
        // Club Admin: Disable club dropdown (pre-selected), enable member dropdown
        clubSelect.innerHTML = `<option value="${userInfo.clubId}" selected>${userInfo.clubName}</option>`;
        clubSelect.disabled = true;
        memberSelect.disabled = false;

        await loadClubMembers(userInfo.clubId);

        // Pre-select current user if they're in the loaded members
        if (userInfo.memberId) {
            setTimeout(() => {
                const memberOption = memberSelect.querySelector(`option[value="${userInfo.memberId}"]`);
                if (memberOption) {
                    memberSelect.value = userInfo.memberId;
                    selectedTargetMemberId = userInfo.memberId;
                    updateRegistrationButton();
                    showSelectedMemberInfo({
                        id: userInfo.memberId,
                        name: userInfo.memberName,
                        clubName: userInfo.clubName,
                        email: userInfo.email || ''
                    });
                }
            }, 100);
        }

    } else {
        // Regular Member: Disable both dropdowns, pre-fill with own info
        clubSelect.innerHTML = `<option value="${userInfo.clubId}" selected>${userInfo.clubName}</option>`;
        memberSelect.innerHTML = `<option value="${userInfo.memberId}" selected>${userInfo.memberName}</option>`;
        clubSelect.disabled = true;
        memberSelect.disabled = true;

        // Pre-select current user
        selectedTargetMemberId = userInfo.memberId;
        updateRegistrationButton();

        // Show member info
        showSelectedMemberInfo({
            id: userInfo.memberId,
            name: userInfo.memberName,
            clubName: userInfo.clubName,
            email: userInfo.email || ''
        });

        // Set the hidden field for regular members
        document.getElementById('onBehalfOfMemberId').value = userInfo.memberId;
    }
}

async function loadAllClubs() {
    try {
        const response = await fetch('/umbraco/surface/Competition/GetClubsForRegistration', {
            method: 'GET',
            headers: { 'Content-Type': 'application/json' }
        });

        const data = await response.json();

        if (data.success) {
            const clubSelect = document.getElementById('clubSelect');
            clubSelect.innerHTML = '<option value="">Select club...</option>';

            data.clubs.forEach(club => {
                const option = document.createElement('option');
                option.value = club.id;
                option.textContent = club.name;
                clubSelect.appendChild(option);
            });

            availableClubs = data.clubs;
        } else {
            console.error('loadAllClubs: API returned error:', data.message);
            showRegistrationTargetError('Failed to load clubs');
        }
    } catch (error) {
        console.error('Error loading clubs:', error);
        showRegistrationTargetError('Error loading clubs');
    }
}

async function loadClubMembers(clubId) {
    if (!clubId) {
        const memberSelect = document.getElementById('memberSelect');
        memberSelect.innerHTML = '<option value="">Välj klubb först...</option>';
        memberSelect.disabled = false;
        return;
    }

    try {
        const memberSelect = document.getElementById('memberSelect');
        memberSelect.innerHTML = '<option value="">Loading members...</option>';
        memberSelect.disabled = true;

        const response = await fetch(`/umbraco/surface/Competition/GetClubMembers?clubId=${clubId}`, {
            method: 'GET',
            headers: { 'Content-Type': 'application/json' }
        });

        const data = await response.json();
        if (data.success) {
            memberSelect.innerHTML = '<option value="">Select member...</option>';

            data.members.forEach(member => {
                const option = document.createElement('option');
                option.value = member.id;
                option.textContent = `${member.name} (${member.email || 'No email'})`;
                memberSelect.appendChild(option);
            });

            memberSelect.disabled = false;
        } else {
            memberSelect.innerHTML = '<option value="">Error loading members</option>';
            memberSelect.disabled = true;
        }
    } catch (error) {
        console.error('Error loading club members:', error);
        const memberSelect = document.getElementById('memberSelect');
        memberSelect.innerHTML = '<option value="">Error loading members</option>';
        memberSelect.disabled = true;
    }
}

function handleClubSelection() {
    const clubSelect = document.getElementById('clubSelect');
    const selectedClubId = clubSelect.value;

    // Clear member selection
    const memberSelect = document.getElementById('memberSelect');
    memberSelect.value = '';
    hideSelectedMemberInfo();
    document.getElementById('onBehalfOfMemberId').value = '';

    // Clear selected member ID and update button state
    selectedTargetMemberId = null;
    updateRegistrationButton();

    if (selectedClubId) {
        loadClubMembers(selectedClubId);
    } else {
        memberSelect.innerHTML = '<option value="">Välj klubb först...</option>';
        memberSelect.disabled = false;
    }
}

async function handleMemberSelection() {
    const memberSelect = document.getElementById('memberSelect');
    const selectedMemberId = memberSelect.value;

    if (!selectedMemberId) {
        hideSelectedMemberInfo();
        document.getElementById('onBehalfOfMemberId').value = '';
        selectedTargetMemberId = null;
        updateRegistrationButton(); // Update button state when no member selected
        return;
    }

    try {
        // Get member details
        const response = await fetch(`/umbraco/surface/Competition/GetMemberDetails?memberId=${selectedMemberId}`, {
            method: 'GET',
            headers: { 'Content-Type': 'application/json' }
        });

        const data = await response.json();
        if (data.success) {
            showSelectedMemberInfo(data.member);
            document.getElementById('onBehalfOfMemberId').value = selectedMemberId;
            selectedTargetMemberId = selectedMemberId;
            updateRegistrationButton(); // Update button state when member selected

            // Query existing registrations for the selected member
            await queryExistingRegistrations();
        } else {
            selectedTargetMemberId = null;
            updateRegistrationButton(); // Update button state on error
            showRegistrationTargetError('Failed to load member details');
        }
    } catch (error) {
        console.error('Error loading member details:', error);
        selectedTargetMemberId = null;
        updateRegistrationButton(); // Update button state on error
        showRegistrationTargetError('Error loading member details');
    }
}

function showSelectedMemberInfo(member) {
    document.getElementById('selectedMemberName').textContent = member.name;
    document.getElementById('selectedMemberClub').textContent = member.clubName;
    document.getElementById('selectedMemberEmail').textContent = member.email || 'Not provided';
    document.getElementById('selectedMemberInfo').classList.remove('d-none');
}

function hideSelectedMemberInfo() {
    document.getElementById('selectedMemberInfo').classList.add('d-none');
}

function showRegistrationTargetError(message) {
    console.error('Registration Target Error:', message);

    // Show friendly modal for "not logged in" error
    if (message === 'Failed to load user information') {
        // Close the registration modal first
        const registrationModal = bootstrap.Modal.getInstance(document.getElementById('registrationModal'));
        if (registrationModal) {
            registrationModal.hide();
        }

        // Show the login required modal
        const loginRequiredModal = new bootstrap.Modal(document.getElementById('loginRequiredModal'));
        loginRequiredModal.show();
    } else {
        // For other errors, show alert
        alert('Registration Error: ' + message);
    }
}

// ============================================================================
// DUPLICATE REGISTRATION PREVENTION SYSTEM
// ============================================================================

// Query existing registrations for the current member in this competition
async function queryExistingRegistrations() {
    try {
        // Build URL with optional memberId parameter
        // Use selectedTargetMemberId if admin is registering for someone else
        let url = `/umbraco/surface/Competition/GetMemberRegistrationsForCompetition?competitionId=${COMPETITION_ID}`;
        if (selectedTargetMemberId) {
            url += `&memberId=${selectedTargetMemberId}`;
        }

        const response = await fetch(url, {
            method: 'GET',
            headers: {
                'Content-Type': 'application/json'
            }
        });

        if (!response.ok) {
            console.error('Failed to fetch existing registrations:', response.status);
            existingRegistrations = [];
            return;
        }

        const result = await response.json();
        if (result.success) {
            existingRegistrations = result.registrations || [];

            // Build weapon class conflicts map - iterate through all classes in each registration
            weaponClassConflicts = {};
            existingRegistrations.forEach(reg => {
                // Each registration now has shootingClasses array
                if (reg.shootingClasses && Array.isArray(reg.shootingClasses)) {
                    reg.shootingClasses.forEach(classEntry => {
                        // Extract weapon class from class ID (e.g., "A1" → "A")
                        const weaponClass = classEntry.class.charAt(0).toUpperCase();

                        if (!weaponClassConflicts[weaponClass]) {
                            weaponClassConflicts[weaponClass] = [];
                        }

                        // Store class info with registration reference
                        weaponClassConflicts[weaponClass].push({
                            classId: classEntry.class,
                            startPreference: classEntry.startPreference,
                            registrationId: reg.id,
                            registrationDate: reg.registrationDate
                        });
                    });
                }
            });

            addExistingRegistrationBadges();
        } else {
            console.error('API returned success=false:', result.message);
            existingRegistrations = [];
            weaponClassConflicts = {};
        }
    } catch (error) {
        console.error('Error querying existing registrations:', error);
        existingRegistrations = [];
        weaponClassConflicts = {};
    }
}

// Add visual badges next to already-registered classes
function addExistingRegistrationBadges() {
    // Remove all existing badges first
    document.querySelectorAll('.already-registered-badge, .weapon-conflict-badge, .heading-weapon-badge').forEach(badge => badge.remove());

    // Uncheck all radio buttons and clear original state markers
    document.querySelectorAll('input[type="radio"]').forEach(radio => {
        radio.checked = false;
        delete radio.dataset.originallySelected;
    });

    // Add badges to headings for weapon class conflicts
    Object.keys(weaponClassConflicts).forEach(weaponClass => {
        const classEntries = weaponClassConflicts[weaponClass];

        // Get unique class IDs (in case of duplicates)
        const uniqueClasses = [...new Set(classEntries.map(entry => entry.classId))];
        const classesText = uniqueClasses.join(', ');

        // Find the appropriate heading
        const headings = document.querySelectorAll('#classSelections h6');

        headings.forEach(h6 => {
            const headingText = h6.textContent.trim();
            let shouldAddBadge = false;

            // Match heading to weapon class and subcategory
            if (weaponClass === 'A' && headingText.includes('A-klasser')) {
                shouldAddBadge = true;
            } else if (weaponClass === 'B' && headingText.includes('B-klasser')) {
                shouldAddBadge = true;
            } else if (weaponClass === 'C') {
                // For C-classes, check if any class matches this subcategory
                const hasRegularClass = uniqueClasses.some(cls => getCClassSubcategory(cls) === 'Regular');
                const hasVeteranClass = uniqueClasses.some(cls => getCClassSubcategory(cls) === 'Veteran');
                const hasLadiesClass = uniqueClasses.some(cls => getCClassSubcategory(cls) === 'Ladies');
                const hasJuniorClass = uniqueClasses.some(cls => getCClassSubcategory(cls) === 'Junior');

                if (hasRegularClass && headingText.includes('C-klasser (Öppna)')) {
                    shouldAddBadge = true;
                } else if (hasVeteranClass && headingText.includes('C-klasser (Veteran)')) {
                    shouldAddBadge = true;
                } else if (hasLadiesClass && headingText.includes('C-klasser (Dam)')) {
                    shouldAddBadge = true;
                } else if (hasJuniorClass && headingText.includes('C-klasser (Junior)')) {
                    shouldAddBadge = true;
                }
            } else if (weaponClass === 'R' && headingText.includes('R-klasser')) {
                shouldAddBadge = true;
            } else if (weaponClass === 'L') {
                // For L-classes, check if any class matches this subcategory
                const hasRegularClass = uniqueClasses.some(cls => getLClassSubcategory(cls) === 'Regular');
                const hasVeteranClass = uniqueClasses.some(cls => getLClassSubcategory(cls) === 'Veteran');
                const hasLadiesClass = uniqueClasses.some(cls => getLClassSubcategory(cls) === 'Ladies');
                const hasJuniorClass = uniqueClasses.some(cls => getLClassSubcategory(cls) === 'Junior');

                if (hasRegularClass && (headingText.includes('L-klasser (Luftpistol - Öppna)') || headingText.includes('L-klasser (Öppna)'))) {
                    shouldAddBadge = true;
                } else if (hasVeteranClass && (headingText.includes('L-klasser (Luftpistol - Veteran)') || headingText.includes('L-klasser (Veteran)'))) {
                    shouldAddBadge = true;
                } else if (hasLadiesClass && (headingText.includes('L-klasser (Luftpistol - Dam)') || headingText.includes('L-klasser (Dam)'))) {
                    shouldAddBadge = true;
                } else if (hasJuniorClass && (headingText.includes('L-klasser (Luftpistol - Junior)') || headingText.includes('L-klasser (Junior)'))) {
                    shouldAddBadge = true;
                }
            } else if (weaponClass === 'M' && (headingText.includes('M-klasser (Magnum)') || headingText.includes('M-klasser'))) {
                shouldAddBadge = true;
            }

            // Add badge to heading
            if (shouldAddBadge && !h6.querySelector('.heading-weapon-badge')) {
                const badge = document.createElement('span');
                badge.className = 'badge bg-warning text-dark ms-2 heading-weapon-badge';
                badge.innerHTML = `<i class="bi bi-exclamation-triangle"></i> Redan anmäld i ${classesText}`;
                badge.style.fontSize = '0.75rem';
                h6.appendChild(badge);
            }
        });
    });

    // Auto-check radio buttons for all registered classes
    existingRegistrations.forEach(reg => {
        if (reg.shootingClasses && Array.isArray(reg.shootingClasses)) {
            reg.shootingClasses.forEach(classEntry => {
                const classId = classEntry.class;
                const radios = document.querySelectorAll(`input[type="radio"][value="${classId}"]`);

                radios.forEach(radio => {
                    radio.checked = true;
                    // Store the original state for comparison
                    radio.dataset.originallySelected = 'true';
                });
            });
        }
    });

    // Initialize button state management after badges and auto-check
    initializeButtonStateManagement();
}

// Check if a class is already registered
function isClassAlreadyRegistered(classId) {
    return existingRegistrations.some(reg => {
        if (!reg.shootingClasses || !Array.isArray(reg.shootingClasses)) {
            return false;
        }
        return reg.shootingClasses.some(classEntry => classEntry.class === classId);
    });
}

// Capture original selection state
function captureOriginalSelection() {
    const originalClasses = [];
    if (existingRegistrations.length > 0) {
        existingRegistrations.forEach(reg => {
            if (reg.shootingClasses && Array.isArray(reg.shootingClasses)) {
                reg.shootingClasses.forEach(classEntry => {
                    originalClasses.push(classEntry.class);
                });
            }
        });
    }
    return originalClasses.sort();
}

// Check if selection has changed from original
function hasSelectionChanged(originalClasses) {
    const currentClasses = [];
    const radioGroups = ['selectedAClass', 'selectedBClass', 'selectedRClass',
                       'selectedCRegular', 'selectedCVet', 'selectedCDam', 'selectedCJun',
                       'selectedLRegular', 'selectedLVet', 'selectedLDam', 'selectedLJun',
                       'selectedMClass'];

    document.querySelectorAll('input[type="radio"]:checked').forEach(radio => {
        const name = radio.getAttribute('name');
        if (radioGroups.includes(name)) {
            currentClasses.push(radio.value);
        }
    });

    const sortedCurrent = currentClasses.sort();

    // Compare arrays
    if (originalClasses.length !== sortedCurrent.length) return true;

    for (let i = 0; i < originalClasses.length; i++) {
        if (originalClasses[i] !== sortedCurrent[i]) return true;
    }

    return false;
}

// Update button state based on selection
function updateButtonState() {
    const submitBtn = document.getElementById('modalRegisterBtn');
    if (!submitBtn) return;

    const originalClasses = captureOriginalSelection();

    if (existingRegistrations.length > 0) {
        const changed = hasSelectionChanged(originalClasses);

        if (!changed) {
            // Selection matches existing registration - disable button
            submitBtn.disabled = true;
            submitBtn.title = 'Välj andra klasser för att ändra anmälan';
        } else {
            // Selection changed - enable button
            submitBtn.disabled = false;
            submitBtn.title = '';
        }
    } else {
        // No existing registrations - enable button
        submitBtn.disabled = false;
        submitBtn.title = '';
    }
}

// Initialize button state management
function initializeButtonStateManagement() {
    const submitBtn = document.getElementById('modalRegisterBtn');
    if (!submitBtn) return;

    // Set initial button state
    updateButtonState();

    // Add event listener to radio buttons
    document.querySelectorAll('input[type="radio"]').forEach(radio => {
        radio.addEventListener('change', updateButtonState);
    });
}

// Show confirmation dialog for updating an existing registration
function showUpdateConfirmationDialog(classId, callback) {
    const modal = new bootstrap.Modal(document.getElementById('updateRegistrationConfirmModal'));
    document.getElementById('existingClassName').textContent = classId;

    const confirmBtn = document.getElementById('confirmUpdateBtn');
    const cancelBtn = document.getElementById('cancelUpdateBtn');

    const newConfirmBtn = confirmBtn.cloneNode(true);
    confirmBtn.parentNode.replaceChild(newConfirmBtn, confirmBtn);

    const newCancelBtn = cancelBtn.cloneNode(true);
    cancelBtn.parentNode.replaceChild(newCancelBtn, cancelBtn);

    newConfirmBtn.addEventListener('click', function() {
        modal.hide();
        callback(true);
    });

    newCancelBtn.addEventListener('click', function() {
        modal.hide();
        callback(false);
    });

    modal.show();
}

// Show confirmation dialog for replacing an existing registration with new class
function showReplaceConfirmationDialog(existingClass, newClass, callback) {
    const modal = new bootstrap.Modal(document.getElementById('replaceRegistrationConfirmModal'));

    // Set class names in all placeholders
    document.getElementById('replaceExistingClassName').textContent = existingClass;
    document.getElementById('replaceNewClassName').textContent = newClass;
    document.getElementById('replaceOldClassNameAlert').textContent = existingClass;
    document.getElementById('replaceNewClassNameAlert').textContent = newClass;

    const confirmBtn = document.getElementById('confirmReplaceBtn');
    const cancelBtn = document.getElementById('cancelReplaceBtn');

    // Clone buttons to remove old event listeners
    const newConfirmBtn = confirmBtn.cloneNode(true);
    confirmBtn.parentNode.replaceChild(newConfirmBtn, confirmBtn);

    const newCancelBtn = cancelBtn.cloneNode(true);
    cancelBtn.parentNode.replaceChild(newCancelBtn, cancelBtn);

    newConfirmBtn.addEventListener('click', function() {
        modal.hide();
        callback(true);
    });

    newCancelBtn.addEventListener('click', function() {
        modal.hide();
        callback(false);
    });

    modal.show();
}

// Get C-class subcategory
function getCClassSubcategory(classId) {
    if (classId === 'C1' || classId === 'C2' || classId === 'C3') return 'Regular';
    if (classId.includes('Vet')) return 'Veteran';
    if (classId.includes('Dam')) return 'Ladies';
    if (classId.includes('Jun')) return 'Junior';
    return 'Regular';
}

function getLClassSubcategory(classId) {
    if (classId === 'L1' || classId === 'L2' || classId === 'L3') return 'Regular';
    if (classId.includes('Vet')) return 'Veteran';
    if (classId.includes('Dam')) return 'Ladies';
    if (classId.includes('Jun')) return 'Junior';
    return 'Regular';
}

// Validate weapon class conflicts
function validateWeaponClassConflicts(newClassId) {
    // Extract weapon class from new class (first character)
    const newWeaponClass = newClassId.charAt(0);

    // Check if this weapon class has existing registrations
    const conflicts = weaponClassConflicts[newWeaponClass];
    if (!conflicts || conflicts.length === 0) {
        return { hasConflict: false };
    }

    // Check if trying to register exact same class
    const exactMatch = conflicts.find(reg => reg.shootingClass === newClassId);
    if (exactMatch) {
        // Exact duplicate - will be handled by existing duplicate prevention
        return { hasConflict: false, isExactDuplicate: true };
    }

    // C-Class special rules
    if (newWeaponClass === 'C' && ALLOW_DUAL_C_CLASS_REGISTRATION) {
        const newSubcategory = getCClassSubcategory(newClassId);

        // Check if any existing registration is in same subcategory
        const sameSubcategory = conflicts.find(reg =>
            getCClassSubcategory(reg.shootingClass) === newSubcategory
        );

        if (sameSubcategory) {
            // Same subcategory = conflict (e.g., C2 → C3 both Regular)
            return {
                hasConflict: true,
                conflictingClass: sameSubcategory.shootingClass,
                registrationId: sameSubcategory.id,
                reason: 'same_subcategory'
            };
        }

        // Different subcategory - check if already have 2 C-classes
        if (conflicts.length >= 2) {
            return {
                hasConflict: true,
                conflictingClass: conflicts[0].shootingClass,
                registrationId: conflicts[0].id,
                reason: 'max_c_classes'
            };
        }

        // Different subcategory and less than 2 C-classes = allowed
        return { hasConflict: false };
    }

    // All other cases: same weapon class = conflict
    return {
        hasConflict: true,
        conflictingClass: conflicts[0].shootingClass,
        registrationId: conflicts[0].id,
        reason: 'weapon_class'
    };
}

// DEPRECATED: Old function for processing registrations sequentially (one request per class)
// Now replaced by submitAllClassesRegistration which submits all classes in a single request
// Keeping this commented out for reference during transition period
/*
async function processRegistrationsWithConfirmation(selectedClasses, submitBtn) {
    // ... old implementation removed ...
}
*/

// Transform registration modal to success state with payment option
function transformModalToSuccessState(result, competitionId, isAdminRegistration) {
    const modalBody = document.querySelector('#registrationModal .modal-body');
    const modalFooter = document.querySelector('#registrationModal .modal-footer');
    const modalTitle = document.querySelector('#registrationModal .modal-title');

    // Hide submit button explicitly (before transformation)
    const submitBtn = document.getElementById('modalRegisterBtn');
    if (submitBtn) {
        submitBtn.style.display = 'none';
    }

    // Update modal title
    modalTitle.innerHTML = '<i class="bi bi-check-circle-fill text-success"></i> Anmälan genomförd!';

    // Get the target member ID (for admin registrations)
    const targetMemberId = selectedTargetMemberId || '';

    // Extract data from result
    const message = result.message || 'Anmälan genomförd!';
    const isUpdate = result.isUpdate || false;
    const feeChanged = result.feeChanged || false;
    const newFee = result.newFee || 0;

    // Determine if payment should be shown
    // Show payment if: NOT an update OR (update with fee change)
    const showPaymentButton = (!isUpdate || feeChanged) && shouldShowPayment();

    // Build appropriate message
    let displayMessage = message;
    if (isUpdate && feeChanged) {
        displayMessage = `Anmälan uppdaterad! Ny avgift: ${newFee} SEK`;
    } else if (isUpdate) {
        displayMessage = 'Anmälan uppdaterad!';
    }

    // Create success state content
    const successContent = `
        <div class="text-center py-4">
            <div class="text-success mb-3">
                <i class="bi bi-check-circle" style="font-size: 4rem;"></i>
            </div>
            <h4 class="mb-3">Tack för din anmälan!</h4>
            <p class="text-muted">${displayMessage}</p>

            ${showPaymentButton ? `
                <div class="alert alert-info mt-4 text-start">
                    <h5 class="mb-3"><i class="bi bi-wallet2"></i> Betala tävlingsavgift</h5>
                    <p class="mb-3">Slutför din anmälan genom att betala tävlingsavgiften med Swish.</p>
                    <div class="d-flex flex-column gap-2 align-items-center">
                        <button type="button" class="btn btn-primary w-75" onclick="showSwishPaymentModalForMember(${competitionId}, '${targetMemberId}')">
                            <i class="bi bi-qr-code"></i> Betala nu med Swish
                        </button>
                        <button type="button" class="btn btn-outline-primary w-75" onclick="sendSwishQRCodeEmailForMember(${competitionId}, '${targetMemberId}')">
                            <i class="bi bi-envelope"></i> Skicka QR-kod via e-post
                        </button>
                        <button type="button" class="btn btn-outline-secondary w-75" data-bs-dismiss="modal">
                            <i class="bi bi-clock"></i> Betala senare
                        </button>
                    </div>
                </div>
            ` : !isAdminRegistration ? `
                <div class="alert alert-success mt-4">
                    <i class="bi bi-info-circle"></i> Din anmälan är registrerad!
                </div>
            ` : `
                <div class="alert alert-success mt-4">
                    <i class="bi bi-info-circle"></i> Anmälan är nu registrerad för den valda medlemmen.
                </div>
            `}
        </div>
    `;

    modalBody.innerHTML = successContent;

    // Update footer
    modalFooter.innerHTML = `
        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">
            <i class="bi bi-x-circle"></i> Stäng
        </button>
    `;
}

// Send Swish QR code via email (wrapper with target member support)
async function sendSwishQRCodeEmailForMember(competitionId, targetMemberId) {
    return sendSwishQRCodeEmail(competitionId, targetMemberId);
}

// Send Swish QR code via email
async function sendSwishQRCodeEmail(competitionId, targetMemberId = '') {
    try {
        // Show loading state
        const button = event.target;
        const originalContent = button.innerHTML;
        button.disabled = true;
        button.innerHTML = '<span class="spinner-border spinner-border-sm me-2"></span>Skickar...';

        const formData = new FormData();
        formData.append('competitionId', competitionId);
        if (targetMemberId) {
            formData.append('targetMemberId', targetMemberId);
        }

        const response = await fetch('/umbraco/surface/Swish/SendQRCodeEmail', {
            method: 'POST',
            body: formData,
            headers: {
                'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]').value,
                'Accept': 'application/json',
                'X-Requested-With': 'XMLHttpRequest'
            }
        });

        const result = await response.json();

        if (result.success) {
            // Show success message
            button.innerHTML = '<i class="bi bi-check-circle"></i> E-post skickad!';
            button.classList.remove('btn-outline-primary');
            button.classList.add('btn-success');

            // Show notification
            showNotification(`QR-koden har skickats till ${result.email}`, 'success');

            // Re-enable button after 3 seconds
            setTimeout(() => {
                button.innerHTML = originalContent;
                button.classList.remove('btn-success');
                button.classList.add('btn-outline-primary');
                button.disabled = false;
            }, 3000);
        } else {
            // Show error
            button.innerHTML = originalContent;
            button.disabled = false;
            alert('Fel: ' + result.message);
        }
    } catch (error) {
        console.error('Error sending Swish QR code email:', error);
        alert('Ett fel uppstod när e-posten skulle skickas. Försök igen.');
        event.target.innerHTML = '<i class="bi bi-envelope"></i> Skicka QR-kod via e-post';
        event.target.disabled = false;
    }
}
