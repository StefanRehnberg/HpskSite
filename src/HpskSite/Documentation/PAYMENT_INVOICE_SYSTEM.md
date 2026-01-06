# Payment & Invoice System Documentation

**Version:** 2025-11-26
**Status:** ✅ Production Ready

## Overview

Complete Swish-based payment system for competition registrations with QR code generation, email delivery, and invoice tracking. Supports both immediate payment and deferred payment via email.

---

## Table of Contents

1. [System Architecture](#system-architecture)
2. [Document Types](#document-types)
3. [Payment Workflow](#payment-workflow)
4. [Swish Integration](#swish-integration)
5. [Email System](#email-system)
6. [Invoice Management](#invoice-management)
7. [Admin Features](#admin-features)
8. [API Endpoints](#api-endpoints)
9. [Configuration](#configuration)
10. [Testing](#testing)

---

## System Architecture

### Components

```
┌─────────────────────────────────────────────────────────────┐
│                    Registration Flow                         │
├─────────────────────────────────────────────────────────────┤
│  User registers → Success Modal → Payment Options           │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  1. Betala nu med Swish (QR modal)                   │  │
│  │  2. Skicka QR-kod via e-post (Email)                 │  │
│  │  3. Betala senare (Close)                            │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│                   Payment Processing                         │
├─────────────────────────────────────────────────────────────┤
│  SwishController → PaymentService → Invoice Creation        │
│                                                              │
│  Invoice Hub (auto-created) → Invoice Documents             │
│                                                              │
│  Email Service → QR Code Attachment + Deep Link             │
└─────────────────────────────────────────────────────────────┘
```

### Key Files

**Controllers:**
- `SwishController.cs` - QR code generation, email sending, payment redirect
- `PaymentController.cs` - Invoice management, payment status
- `CompetitionController.cs` - Registration endpoints

**Services:**
- `PaymentService.cs` - Invoice creation, status tracking
- `EmailService.cs` - Email with QR code and deep link
- `SwishQrCodeGenerator.cs` - QR code generation

**Views:**
- `Views/Partials/CompetitionRegistrationManagement.cshtml` - Admin payment buttons
- `Views/Competition.cshtml` - Swish payment modal
- `wwwroot/js/competition-registration.js` - Payment UI logic

---

## Document Types

### 1. registrationInvoicesHub

**Purpose:** Container for all invoices related to a competition

**Parent:** Competition document
**Allowed Children:** `registrationInvoice`

**Properties:**
- None (acts as organizational container only)

**Naming Convention:** "Registration Invoices" (auto-created)

**Creation:** Auto-created by `PaymentService.CreateInvoiceAsync()` when first invoice is needed

### 2. registrationInvoice

**Purpose:** Tracks individual payment for member's competition registration(s)

**Parent:** `registrationInvoicesHub`

**Properties:**

| Property | Type | Description | Example |
|----------|------|-------------|---------|
| `competitionId` | Textstring | Competition ID | "1067" |
| `memberId` | Textstring | Member ID | "2043" |
| `memberName` | Textstring | Display name | "John Doe" |
| `totalAmount` | Decimal | Total payment | 150.00 |
| `paymentMethod` | Textstring | Payment type | "Swish" |
| `paymentStatus` | Textstring | Status | "Pending", "Paid", "Failed", "Cancelled", "Refunded" |
| `paymentDate` | Date Picker | When paid | 2025-11-26 |
| `transactionId` | Textstring | Swish transaction | (Optional) |
| `invoiceNumber` | Textstring | Unique ID | "1067-2043-1" |
| `relatedRegistrationIds` | Textarea | JSON array | "[1234,1235]" |
| `createdDate` | Date Picker | Creation date | 2025-11-26 |
| `notes` | Textarea | Admin notes | (Optional) |
| `isActive` | True/False | Active status | true |

**Invoice Number Format:** `{competitionId}-{memberId}-{sequence}`
- Example: `1067-2043-1` (Competition 1067, Member 2043, First invoice)
- Sequence auto-increments if member has multiple invoices

---

## Payment Workflow

### Scenario 1: Immediate Payment (User Registration)

```
1. User completes registration form
2. Success modal appears with 3 payment buttons
3. User clicks "Betala nu med Swish"
   └─> Modal shows QR code
   └─> User scans with Swish app
   └─> Payment completed in app
4. Admin marks as "Paid" in registration management
```

### Scenario 2: Email Payment (User Registration)

```
1. User completes registration form
2. Success modal appears with 3 payment buttons
3. User clicks "Skicka QR-kod via e-post"
   └─> System generates invoice
   └─> Email sent with QR code + deep link
   └─> User receives email
4a. Desktop: User scans QR code with phone
4b. Mobile: User clicks button to open Swish app
5. Payment completed in Swish app
6. Admin marks as "Paid" in registration management
```

### Scenario 3: Deferred Payment (Pay Later)

```
1. User completes registration form
2. Success modal appears with 3 payment buttons
3. User clicks "Betala senare"
   └─> Modal closes
   └─> Registration saved
   └─> Invoice created with status "Pending"
4. Later: Admin/User clicks wallet button in registration list
   └─> Payment options modal appears
   └─> Choose payment method
5. Payment completed
```

### Scenario 4: Admin-Initiated Payment

```
1. Admin opens "Anmälningar" tab
2. Clicks wallet button (💳) on any registration row
3. Payment options modal appears
4. Admin chooses payment method
5. QR code shown or email sent to member
6. Member completes payment
7. Admin marks as "Paid"
```

---

## Swish Integration

### QR Code Generation

**Library:** QRCoder (NuGet package)

**Implementation:** `SwishQrCodeGenerator.cs`

**Format:** Swish-compatible QR code with embedded payment data

```csharp
var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(
    swishNumber: "0701234567",
    amount: "150.00",
    message: "Betalning: 1067-2043-1"
);
```

**QR Code Data Structure:**
```json
{
  "version": 1,
  "payee": {
    "value": "0701234567"
  },
  "amount": {
    "value": 150.00
  },
  "message": {
    "value": "Betalning: 1067-2043-1"
  }
}
```

### Deep Link Support

**Direct Link (Blocked by Gmail):**
```
swish://payment?data=eyJ2ZXJzaW9u...
```

**Gmail-Compatible Redirect:**
```
https://hpsktest.se/umbraco/surface/Swish/SwishRedirect?payee=0701234567&amount=150&message=Betalning%3A%201067-2043-1
```

**How It Works:**
1. Email contains HTTPS link (Gmail allows)
2. User clicks link → Opens in browser
3. Server redirects to `swish://` URL
4. Swish app opens with pre-filled data

**Endpoint:** `SwishController.SwishRedirect`
- Accepts: `payee`, `amount`, `message` (query parameters)
- Returns: HTTP 302 redirect to Swish deep link

---

## Email System

### Email Template

**Template:** `EmailService.SendSwishQRCodeEmailAsync()`

**Components:**
1. **QR Code Image** (200x200px, inline attachment)
2. **Green Swish Button** (deep link for mobile users)
3. **Payment Information Box** (amount, classes, invoice number)
4. **Instructions** (separate for mobile and desktop)

**Email Structure:**

```html
┌────────────────────────────────────┐
│  Subject: Swish-betalning för...  │
├────────────────────────────────────┤
│  Hej {memberName}!                 │
│                                    │
│  ┌──────────────────────────────┐ │
│  │   [QR Code Image 200x200]    │ │
│  │   📱 Scanna med Swish        │ │
│  └──────────────────────────────┘ │
│                                    │
│  ┌──────────────────────────────┐ │
│  │ 📱 Läser du detta på din     │ │
│  │     telefon?                 │ │
│  │                              │ │
│  │  [💳 Öppna Swish och betala] │ │
│  │    (Clickable button)        │ │
│  └──────────────────────────────┘ │
│                                    │
│  📋 Betalningsinformation:         │
│  • Belopp: 150.00 kr               │
│  • Klass: A1, B2                   │
│  • Fakturanummer: 1067-2043-1      │
│                                    │
│  💡 Två sätt att betala:           │
│  📱 Mobile: Click button           │
│  💻 Desktop: Scan QR code          │
└────────────────────────────────────┘
```

### QR Code Embedding

**Method:** Inline attachment with Content-ID (CID)

**Why Not Base64?**
- Gmail, Outlook, and other clients block `data:image/png;base64,...` for security
- Inline attachments work in all email clients

**Implementation:**
```csharp
// Create attachment
var qrAttachment = new Attachment(qrStream, "swish-qr-code.png", "image/png");
qrAttachment.ContentId = "swishQRCode";
qrAttachment.ContentDisposition.Inline = true;

// Reference in HTML
<img src='cid:swishQRCode' alt='Swish QR Code' />
```

### Button Styling (Email-Safe)

**Challenge:** CSS classes are stripped by email clients

**Solution:** Inline styles with table-based layout

```html
<table width='100%' cellpadding='0' cellspacing='0'>
    <tr>
        <td align='center'>
            <a href='{swishRedirectUrl}'
               style='display: inline-block;
                      background-color: #00a652;
                      color: #ffffff;
                      padding: 16px 40px;
                      text-decoration: none;
                      border-radius: 8px;
                      font-weight: bold;
                      font-size: 18px;'>
                💳 Öppna Swish och betala
            </a>
        </td>
    </tr>
</table>
```

---

## Invoice Management

### Auto-Creation

Invoices are automatically created when:
1. User registers and payment is required
2. Admin clicks payment button for registration
3. QR code is requested (modal or email)

**Invoice Hub Auto-Creation:**
```csharp
// PaymentService checks if hub exists
var hub = competition.Children()
    .FirstOrDefault(x => x.ContentType.Alias == "registrationInvoicesHub");

// If not found, creates it automatically
if (hub == null) {
    hub = _contentService.Create(
        "Registration Invoices",
        competitionId,
        "registrationInvoicesHub"
    );
    _contentService.SaveAndPublish(hub);
}
```

### Duplicate Prevention

**Check Before Creating:**
```csharp
var existingInvoice = await GetExistingInvoiceForMember(competitionId, memberId);
if (existingInvoice != null) {
    // Reuse existing invoice
    return existingInvoice;
}
```

**Benefits:**
- Prevents duplicate invoices for same member
- Maintains consistent invoice number
- Supports multiple registration classes under one invoice

### Payment Status Tracking

**Status Values:**
- `Pending` - Invoice created, payment not received
- `Paid` - Payment confirmed by admin
- `Failed` - Payment attempt failed
- `Cancelled` - Registration cancelled
- `Refunded` - Payment refunded

**Status Display:**
```javascript
// Badge colors in registration list
'Pending'    → Yellow badge (bg-warning)
'Paid'       → Green badge (bg-success)
'Failed'     → Red badge (bg-danger)
'Cancelled'  → Red badge (bg-danger)
'Refunded'   → Red badge (bg-danger)
'No Invoice' → Gray badge (bg-secondary)
```

---

## Admin Features

### Registration Management Payment Buttons

**Location:** Admin Page → Competitions → Select Competition → "Anmälningar" Tab

**Features:**

1. **Wallet Button (💳)** - Every registration row
   - Opens payment options modal
   - Same 3 options as user registration flow

2. **Mark as Paid Button (✓)** - Only for "Pending" status
   - Quick action to mark payment as received
   - Updates invoice status to "Paid"
   - Adds admin note

3. **Payment Status Badge** - Color-coded status display
   - Visual indication of payment state
   - Swedish text labels

### Payment Options Modal

**Triggered By:**
- Wallet button in registration list
- Any admin/user managing payments

**Options:**
1. **Betala nu med Swish** → Shows QR modal
2. **Skicka QR-kod via e-post** → Sends to member's email
3. **Stäng** → Closes modal

**Code Location:** `Views/Partials/CompetitionRegistrationManagement.cshtml`

### Mark as Paid Workflow

```javascript
function markAsPaid(registrationId) {
    1. Get invoice ID from registration
    2. Call UpdatePaymentStatus endpoint
    3. Set status to "Paid"
    4. Add timestamp and admin note
    5. Refresh registration list
}
```

---

## API Endpoints

### SwishController

#### 1. GetPaymentDialog
```
GET /umbraco/surface/Swish/GetPaymentDialog?competitionId={id}
```
**Returns:** HTML with Swish QR code
**Used By:** QR code modal

#### 2. SendQRCodeEmail
```
POST /umbraco/surface/Swish/SendQRCodeEmail
Body: { competitionId: 1067 }
```
**Returns:** `{ success: true, email: "user@example.com" }`
**Used By:** Email button in payment options

#### 3. SwishRedirect
```
GET /umbraco/surface/Swish/SwishRedirect?payee={phone}&amount={amount}&message={msg}
```
**Returns:** HTTP 302 redirect to `swish://payment?data=...`
**Used By:** Email button deep link (Gmail-compatible)

#### 4. GetPaymentStatus
```
GET /umbraco/surface/Swish/GetPaymentStatus?competitionId={id}
```
**Returns:** Payment status for user's registrations
**Used By:** Registration status display

### PaymentController

#### 1. GetRegistrationPaymentStatus
```
GET /umbraco/surface/Payment/GetRegistrationPaymentStatus?registrationId={id}
```
**Returns:** Payment status and invoice ID
**Used By:** Mark as paid functionality

#### 2. UpdatePaymentStatus
```
POST /umbraco/surface/Payment/UpdatePaymentStatus
Body: { invoiceId: 123, newStatus: "Paid", notes: "..." }
```
**Returns:** `{ success: true }`
**Used By:** Admin marking payments as paid

### PaymentService (Internal)

#### CreateInvoiceAsync
```csharp
Task<IContent?> CreateInvoiceAsync(
    int competitionId,
    string memberId,
    string memberName,
    List<int> registrationIds,
    decimal totalAmount,
    string paymentMethod
)
```
**Creates:** Invoice hub (if needed) + Invoice document
**Returns:** IContent invoice object

#### GetExistingInvoiceForMember
```csharp
Task<IPublishedContent?> GetExistingInvoiceForMember(
    int competitionId,
    string memberId
)
```
**Returns:** Existing invoice or null

---

## Configuration

### Competition Properties Required

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `swishNumber` | Textstring | Yes | 10-digit phone number (0701234567) |
| `registrationFee` | Decimal | Yes | Fee per class (e.g., 150.00) |

**Validation:**
- Swish number must be 10 digits starting with 0
- Registration fee must be > 0
- Both required for payment button to show

### Email Configuration

**appsettings.json:**
```json
{
  "Email": {
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "Username": "noreply@hpsk.se",
    "Password": "***",
    "FromAddress": "noreply@hpsk.se",
    "FromName": "HPSK Site",
    "SiteUrl": "https://hpsktest.se"
  }
}
```

**SiteUrl Usage:**
- Used for generating deep link redirect URLs
- Must be HTTPS for production
- Example: `https://hpsktest.se/umbraco/surface/Swish/SwishRedirect?...`

---

## Testing

### Test Checklist

#### Registration Flow
- [ ] Register for competition with Swish configured
- [ ] Verify success modal shows 3 payment buttons
- [ ] Click "Betala nu med Swish" → QR code appears
- [ ] Click "Skicka QR-kod via e-post" → Email sent
- [ ] Click "Betala senare" → Modal closes

#### Email Delivery
- [ ] Email received within 1 minute
- [ ] QR code visible (not red X)
- [ ] QR code scannable with Swish app
- [ ] Green button visible
- [ ] Button clickable on mobile Gmail
- [ ] Deep link opens Swish app
- [ ] Payment info pre-filled in Swish

#### Admin Payment Management
- [ ] Open registration management tab
- [ ] Wallet button visible on each row
- [ ] Click wallet button → Payment modal appears
- [ ] All 3 options functional
- [ ] Payment status badge displays correctly
- [ ] Mark as Paid button works (for Pending status)

#### Invoice Creation
- [ ] Invoice hub auto-created on first payment
- [ ] Invoice document created with correct data
- [ ] Invoice number follows format: `{compId}-{memberId}-{seq}`
- [ ] No duplicate invoices for same member
- [ ] Multiple registration IDs linked correctly

#### Error Handling
- [ ] Competition without Swish number → Button hidden
- [ ] Competition without fee → Button hidden
- [ ] Member without email → Error message
- [ ] SMTP not configured → Logged warning
- [ ] Invalid Swish number → Error message

### Test Data

**Test Competition Setup:**
```
Competition Name: Test Championship 2025
Swish Number: 0701234567
Registration Fee: 150.00
Shooting Classes: A1, A2, B1, B2, C1
```

**Test Member:**
```
Name: Test User
Email: test@example.com
Club: Test Club
```

**Expected Invoice:**
```
Invoice Number: 1067-2043-1
Amount: 150.00 (1 class) or 300.00 (2 classes)
Status: Pending
Payment Method: Swish
```

---

## Troubleshooting

### QR Code Not Displaying in Email

**Symptom:** Red X or broken image icon
**Cause:** Email client blocking base64 images
**Solution:** Already fixed - using inline attachment with CID

### Button Not Clickable in Gmail

**Symptom:** Button appears but doesn't open Swish
**Cause:** Gmail blocks `swish://` deep links
**Solution:** Already fixed - using HTTPS redirect endpoint

### No Payment Button After Registration

**Check:**
1. Competition has `swishNumber` property set
2. Competition has `registrationFee` > 0
3. JavaScript function `shouldShowPayment()` returns true

**Debug:**
```javascript
console.log('Swish Number:', competition.swishNumber);
console.log('Fee:', competition.registrationFee);
console.log('Should Show:', shouldShowPayment());
```

### Invoice Not Created

**Check:**
1. `registrationInvoicesHub` document type exists
2. Competition is published
3. Member ID is valid
4. PaymentService registered in DI container

**Logs:**
```
Check Umbraco logs for:
- "Creating invoice hub for competition {CompetitionId}"
- "Created invoice {InvoiceNumber} for member {MemberId}"
```

### Email Not Sending

**Check:**
1. SMTP settings in appsettings.json
2. Member has valid email address
3. EmailService registered in DI container

**Test SMTP:**
```csharp
// Send test email from EmailService
await _emailService.SendEmailAsync(
    "test@example.com",
    "Test Subject",
    "<html><body>Test</body></html>"
);
```

---

## Future Enhancements

### Potential Improvements

1. **Automatic Payment Verification**
   - Swish API integration for automatic status updates
   - Callback webhook when payment received
   - Auto-mark as Paid without admin action

2. **Payment Reminders**
   - Scheduled emails for unpaid registrations
   - Configurable reminder intervals
   - Auto-cancel after X days

3. **Refund Management**
   - Refund request workflow
   - Partial refund support
   - Refund history tracking

4. **Multi-Currency Support**
   - Support for other payment methods
   - Currency conversion
   - International competitions

5. **Receipt Generation**
   - PDF receipt download
   - Automatic receipt email on payment
   - Receipt numbering system

6. **Payment Analytics**
   - Payment success rate
   - Average time to payment
   - Popular payment times
   - Payment method statistics

---

## Related Documentation

- [Swish Payment Setup Guide](SWISH_PAYMENT_SETUP.md)
- [Swish Payment Implementation Details](SWISH_PAYMENT_IMPLEMENTATION.md)
- [Email System Documentation](LOGIN_REGISTRATION_SYSTEM.md#email-system)
- [Competition Configuration Guide](COMPETITION_CONFIGURATION_GUIDE.md)

---

## Changelog

### 2025-11-26 - Payment Management Enhancement
- ✅ Added wallet button to registration rows
- ✅ Created payment options modal for admin use
- ✅ Enabled resending payment emails from admin panel
- ✅ Updated documentation

### 2025-11-26 - Gmail Compatibility Fix
- ✅ Added HTTPS redirect endpoint for deep links
- ✅ Fixed button not clickable in Gmail mobile app
- ✅ Reduced QR code size to 200x200px

### 2025-11-26 - Email Enhancement
- ✅ Added clickable Swish button for mobile users
- ✅ Changed from base64 to inline attachment for QR code
- ✅ Improved email template with color-coded instructions

### 2025-01-12 - Initial Release
- ✅ Swish QR code generation
- ✅ Invoice system with document types
- ✅ Payment modal in registration flow
- ✅ Email delivery with QR code

---

**Document Version:** 1.0
**Last Updated:** 2025-11-26
**Maintained By:** Development Team
