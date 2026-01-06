# Swish Payment Implementation Guide

## Status: Ready to Implement

All backend code exists and works. This document explains how to connect the payment system to the registration flow.

---

## What's Already Done ✅

1. **PaymentService registered** in Program.cs (line 10)
2. **SwishController** implemented with all endpoints
3. **PaymentService** with full invoice management
4. **SwishQrCodeGenerator** working and tested
5. **Swish number field** in competition edit modal
6. **Payment status display** in registration management
7. **Document types documented** in SWISH_PAYMENT_SETUP.md

---

## What Needs Implementation

### 1. Add "Pay with Swish" Button After Registration

**Location:** Views/Competition.cshtml

**Current behavior:**
- User registers → Success notification shows
- No payment prompt

**Desired behavior:**
- User registers → Success notification + "Pay with Swish" button
- Button opens payment modal with QR code

**Implementation Options:**

#### Option A: Enhanced Notification (Simplest)
Modify the success notification to include a payment button:

```javascript
// Around line 1486 in Competition.cshtml
@if (TempData["Success"] != null)
{
    <text>
    showNotification('@TempData["Success"]', 'success');
    // Add payment button if registration was successful
    if ('@TempData["Success"]'.includes('anmälan')) {
        showPaymentPrompt(@Model.Id);
    }
    </text>
}
```

Add function:
```javascript
function showPaymentPrompt(competitionId) {
    // Show payment modal or section
    const paymentHtml = `
        <div class="alert alert-info mt-3" id="paymentPrompt">
            <h5><i class="bi bi-wallet2"></i> Betala avgift</h5>
            <p>Din anmälan är nu registrerad. Betala tävlingsavgiften med Swish:</p>
            <a href="/umbraco/surface/Swish/GeneratePaymentQR?competitionId=${competitionId}"
               class="btn btn-primary" target="_blank">
                <i class="bi bi-qr-code"></i> Betala med Swish
            </a>
            <button class="btn btn-secondary" onclick="document.getElementById('paymentPrompt').remove()">
                Betala senare
            </button>
        </div>
    `;

    // Insert after registration section
    const container = document.querySelector('.competition-info') || document.querySelector('.container');
    if (container) {
        container.insertAdjacentHTML('afterbegin', paymentHtml);
    }
}
```

#### Option B: Payment Modal (More Polished)
Create a payment modal that opens automatically:

```html
<!-- Add to Competition.cshtml -->
<div class="modal fade" id="paymentModal" tabindex="-1">
    <div class="modal-dialog">
        <div class="modal-content">
            <div class="modal-header">
                <h5 class="modal-title">
                    <i class="bi bi-qr-code"></i> Betala med Swish
                </h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
            </div>
            <div class="modal-body text-center" id="paymentModalBody">
                <p>Laddar QR-kod...</p>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">
                    Betala senare
                </button>
            </div>
        </div>
    </div>
</div>
```

JavaScript:
```javascript
function showPaymentModal(competitionId) {
    const modal = new bootstrap.Modal(document.getElementById('paymentModal'));

    // Load QR code
    fetch(`/umbraco/surface/Swish/GeneratePaymentQR?competitionId=${competitionId}`)
        .then(response => response.text())
        .then(html => {
            document.getElementById('paymentModalBody').innerHTML = html;
            modal.show();
        })
        .catch(error => {
            document.getElementById('paymentModalBody').innerHTML =
                '<p class="text-danger">Kunde inte ladda betalning. Försök igen senare.</p>';
            modal.show();
        });
}
```

#### Option C: Payment Section on Page (Always Visible)
Add a permanent payment section that shows for registered users:

```html
<!-- Add to Competition.cshtml after registration section -->
@if (userIsRegistered)
{
    <div class="card mt-4">
        <div class="card-header">
            <h5><i class="bi bi-wallet2"></i> Betalning</h5>
        </div>
        <div class="card-body">
            <p>Du är anmäld till denna tävling. Betala avgiften med Swish:</p>
            <a href="/umbraco/surface/Swish/GeneratePaymentQR?competitionId=@Model.Id"
               class="btn btn-primary" target="_blank">
                <i class="bi bi-qr-code"></i> Visa Swish QR-kod
            </a>
        </div>
    </div>
}
```

**Recommendation:** Start with Option A (simplest), then enhance to Option B if needed.

---

### 2. Add "Mark as Paid" Button in Admin

**Location:** Views/Partials/CompetitionRegistrationManagement.cshtml

**Current state:** Payment status is displayed (lines 261-299) but no way to update it

**What to add:**

Around line 175 (in the registration table row), add an action button:

```javascript
// In the actions column of the registration table
${r.paymentStatus === 'Pending' ? `
    <button class="btn btn-sm btn-success"
            onclick="markAsPaid(${r.registrationId})"
            title="Markera som betald">
        <i class="bi bi-check-circle"></i>
    </button>
` : ''}
```

Add function at bottom of file:
```javascript
function markAsPaid(registrationId) {
    if (!confirm('Markera denna anmälan som betald?')) return;

    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    // Get invoice ID from registration
    fetch(`/umbraco/surface/Payment/GetRegistrationPaymentStatus?registrationId=${registrationId}`)
        .then(response => response.json())
        .then(data => {
            if (data.success && data.data && data.data.invoiceId) {
                // Update payment status
                return fetch('/umbraco/surface/Payment/UpdatePaymentStatus', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({
                        invoiceId: data.data.invoiceId,
                        newStatus: 'Paid',
                        notes: 'Manually marked as paid by admin'
                    })
                });
            } else {
                throw new Error('No invoice found for this registration');
            }
        })
        .then(response => response.json())
        .then(result => {
            if (result.success) {
                alert('Betalning markerad som betald!');
                // Refresh registration list
                loadCompetitionRegistrations();
            } else {
                alert('Kunde inte uppdatera: ' + result.message);
            }
        })
        .catch(error => {
            console.error('Error:', error);
            alert('Ett fel uppstod: ' + error.message);
        });
}
```

**Alternative - Simpler approach:**
Add a direct "Mark Paid" button that calls a new simplified endpoint:

```javascript
function markAsPaid(registrationId) {
    if (!confirm('Markera denna anmälan som betald?')) return;

    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    fetch('/umbraco/surface/Payment/MarkRegistrationAsPaid', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
        },
        body: JSON.stringify({ registrationId: registrationId })
    })
    .then(response => response.json())
    .then(result => {
        if (result.success) {
            showAlert('success', 'Betalning markerad som betald!');
            loadCompetitionRegistrations();
        } else {
            showAlert('danger', result.message);
        }
    })
    .catch(error => {
        console.error('Error:', error);
        showAlert('danger', 'Ett fel uppstod vid uppdatering');
    });
}
```

Then add this endpoint to PaymentController.cs:
```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> MarkRegistrationAsPaid([FromBody] MarkAsPaidRequest request)
{
    // Get registration
    var registration = _contentService.GetById(request.RegistrationId);
    if (registration == null)
        return Json(new { success = false, message = "Registration not found" });

    // Get or create invoice
    var invoice = await _paymentService.GetOrCreateInvoiceForRegistrations(...);

    // Update status to Paid
    await _paymentService.UpdatePaymentStatusAsync(invoice.Id, "Paid", notes: "Marked as paid by admin");

    return Json(new { success = true });
}
```

---

### 3. Swish Logo File

**Required:** `Swish Logo.png` file in output directory

**Steps:**
1. Obtain Swish logo (PNG, transparent background preferred)
2. Place in: `wwwroot/images/Swish Logo.png`
3. Verify it's copied to output during build

**Alternative:** If logo not available, QR codes still work without it.

---

## Testing Checklist

### 1. Document Types
- [ ] Create `registrationInvoicesHub` document type
- [ ] Create `registrationInvoice` document type with all properties
- [ ] Test creating invoice manually in backoffice

### 2. Payment Flow
- [ ] Register for test competition
- [ ] "Pay with Swish" button/link appears
- [ ] Click button → QR code displays
- [ ] QR code contains correct Swish number and amount
- [ ] Scan QR → Swish app opens with correct details

### 3. Invoice Creation
- [ ] Invoice created in content tree under competition
- [ ] Invoice has unique number (format: competitionId-memberId-sequence)
- [ ] Invoice properties populated correctly
- [ ] Payment status shows as "Pending"

### 4. Admin Verification
- [ ] View registrations in admin panel
- [ ] Payment status badges show correctly
- [ ] Click "Mark as Paid" button
- [ ] Status updates to "Paid"
- [ ] Badge color changes to green

### 5. Edge Cases
- [ ] User registers multiple times → separate invoices created
- [ ] User has existing pending invoice → shows choice to use existing or create new
- [ ] Competition has no Swish number → shows error message
- [ ] Invalid Swish number format → shows validation error

---

## API Endpoints Summary

### User-Facing
- `GET /umbraco/surface/Swish/GeneratePaymentQR?competitionId={id}` - Generate QR code page
- `GET /umbraco/surface/Payment/GetRegistrationPaymentStatus?registrationId={id}` - Check payment status

### Admin-Facing
- `POST /umbraco/surface/Payment/UpdatePaymentStatus` - Update payment status
- `POST /umbraco/surface/Payment/MarkRegistrationAsPaid` - Quick mark as paid (needs to be added)
- `GET /umbraco/surface/Payment/GetUserInvoices?memberId={id}` - Get member's invoices

---

## Configuration

### Required in Umbraco Backoffice
1. Create document types (see SWISH_PAYMENT_SETUP.md)
2. Add Swish numbers to competitions
3. Ensure admins have permissions to modify invoice documents

### Optional Enhancements
1. Add payment amount field to competition document type
2. Add payment deadline field
3. Add payment reminder email system
4. Add refund tracking

---

## Quick Start - Minimal Implementation

**For fastest implementation, do this:**

1. **Create document types** (15 minutes)
   - Use Umbraco backoffice
   - Follow SWISH_PAYMENT_SETUP.md

2. **Add payment button** (10 minutes)
   - Add Option A code to Competition.cshtml
   - Test QR generation

3. **Add mark as paid** (10 minutes)
   - Add button to CompetitionRegistrationManagement.cshtml
   - Use simplified markAsPaid() function

4. **Test** (15 minutes)
   - Register → Pay → Mark as Paid
   - Verify full flow works

**Total time: ~50 minutes for basic working system**

---

## Next Steps

1. Review this implementation guide
2. Decide on approach for payment button (Option A, B, or C)
3. Create Umbraco document types
4. Implement chosen option
5. Test with real registration
6. Deploy to production

---

**Document Version:** 1.0
**Last Updated:** 2025-01-12
**Status:** Ready for Implementation
