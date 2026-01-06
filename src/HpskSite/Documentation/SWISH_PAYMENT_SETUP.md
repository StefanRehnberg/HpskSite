# Swish Payment System Setup Guide

## Overview
This guide explains how to set up the Swish QR code payment system for competition registrations. The system allows users to pay competition fees by scanning QR codes with the Swish mobile app.

## System Architecture

**Payment Flow:**
1. User registers for competition
2. System generates invoice with unique number
3. QR code is generated with club's Swish number + amount + reference
4. User scans QR code → Swish app opens → Payment sent to club
5. Club admin verifies payment in Swish app
6. Admin marks payment as "Paid" in system

**Key Components:**
- `SwishController` - Generates QR codes and manages payment initiation
- `PaymentService` - Creates invoices and tracks payment status
- `SwishQrCodeGenerator` - Creates Swish-compatible QR codes
- Umbraco document types - Stores invoice records

---

## Umbraco Document Types Required

### 1. registrationInvoicesHub

**Purpose:** Container for all invoices related to a competition

**Parent:** competition (child of competition document)
**Allowed Children:** registrationInvoice

**Properties:** None (just acts as container)

**How to Create:**
1. Go to Settings → Document Types
2. Click "+ Create" → Document Type
3. Name: `registrationInvoicesHub`
4. Alias: `registrationInvoicesHub`
5. Icon: Choose folder or invoice icon
6. Allowed Child Node Types: Add `registrationInvoice`
7. Save

---

### 2. registrationInvoice

**Purpose:** Individual invoice record for a member's competition registration payment

**Parent:** registrationInvoicesHub
**Allowed Children:** None

**Properties to Create:**

| Property Name | Alias | Data Type | Description | Required |
|--------------|-------|-----------|-------------|----------|
| Competition ID | competitionId | Textstring | ID of the competition | Yes |
| Member ID | memberId | Textstring | ID of the member who registered | Yes |
| Member Name | memberName | Textstring | Name of member for display | Yes |
| Total Amount | totalAmount | Decimal | Total amount to pay (e.g., 150.00) | Yes |
| Payment Method | paymentMethod | Textstring | Payment method (Swish, Bank Transfer, etc.) | No |
| Payment Status | paymentStatus | Textstring | Status: Pending, Paid, Failed, Cancelled, Refunded | Yes |
| Payment Date | paymentDate | Date Picker | Date when payment was completed | No |
| Transaction ID | transactionId | Textstring | Swish transaction ID (if available) | No |
| Invoice Number | invoiceNumber | Textstring | Unique invoice number (auto-generated) | Yes |
| Related Registration IDs | relatedRegistrationIds | Textarea | JSON array of registration IDs | Yes |
| Created Date | createdDate | Date Picker | When invoice was created | Yes |
| Notes | notes | Textarea | Admin notes about payment | No |
| Is Active | isActive | True/False | Whether invoice is active | No |

**How to Create:**
1. Go to Settings → Document Types
2. Click "+ Create" → Document Type
3. Name: `registrationInvoice`
4. Alias: `registrationInvoice`
5. Icon: Choose document or money icon
6. Add each property listed in table above
7. Save

---

## Invoice Number Format

Invoices use the format: `{competitionId}-{memberId}-{sequence}`

Example: `1067-2043-1` (Competition 1067, Member 2043, First invoice)

If a member has multiple invoices for same competition, sequence increments: `-1`, `-2`, `-3`, etc.

---

## Payment Status Values

The system uses these status values (case-sensitive):

- **Pending** - Invoice created, payment not yet received
- **Paid** - Payment verified and completed
- **Failed** - Payment attempt failed
- **Cancelled** - Invoice/payment cancelled
- **Refunded** - Payment was refunded to member

---

## Swish Number Configuration

Each competition should have a Swish number configured:

1. Edit competition in backoffice or via edit modal
2. Enter Swish number in "Swish-nummer" field
3. Format: 10 digits starting with 0 (e.g., `0701234567`)
4. This is the club's Swish number where payments will be sent

---

## QR Code Generation

**QR Code contains:**
- Swish payload format: `C{phoneNumber};{amount};{message};0`
- Phone number: Club's Swish number (10 digits)
- Amount: Total with 2 decimal places (e.g., `150.00`)
- Message: Invoice number for reference (max 50 chars)

**Example QR payload:**
```
C0701234567;150.00;Betalning: 1067-2043-1;0
```

When scanned, this opens the Swish app with:
- Recipient: 070-123 45 67
- Amount: 150.00 SEK
- Message: "Betalning: 1067-2043-1"

---

## Admin Workflow

### Verifying Payments

1. Navigate to Competition Admin → Registrations tab
2. View list of registrations with payment status badges:
   - 🟢 **Paid** - Payment verified
   - 🟡 **Pending** - Awaiting payment
   - ⚪ **No Invoice** - No payment required or invoice not created

3. To verify a payment:
   - Check Swish app for incoming payment
   - Match payment reference (invoice number) with registration
   - Click "Markera som betald" button next to registration
   - Payment status updates to "Paid"

### Handling Payment Issues

**If payment doesn't match:**
- Check invoice number in payment message
- Verify amount matches invoice
- Add note in invoice record for audit trail

**If user didn't pay:**
- Payment remains in "Pending" status
- Can send reminder to member
- Can cancel invoice if registration is cancelled

---

## Swish Logo Requirement

The QR code generator embeds the Swish logo in generated QR codes.

**Required file:** `Swish Logo.png`
**Location:** Must be in application output directory at runtime
**Usage:** Embedded in center of QR code

**To add:**
1. Obtain Swish logo (PNG format, transparent background recommended)
2. Place in `wwwroot/images/Swish Logo.png`
3. Ensure it's included in published output

**Note:** QR codes will still work without logo, but won't have Swish branding in center.

---

## Testing the System

### Test Payment Flow

1. **Create test competition:**
   - Add test Swish number (can use your personal Swish)
   - Set competition fee amount

2. **Test registration:**
   - Register as test user
   - Click "Pay with Swish" button
   - Verify QR code displays
   - Verify QR can be scanned (opens Swish app with correct details)

3. **Test admin verification:**
   - Make actual payment via Swish app
   - Go to admin registration management
   - Click "Markera som betald"
   - Verify status changes to "Paid"

4. **Test invoice creation:**
   - Check Content tree in backoffice
   - Navigate to Competition → registrationInvoicesHub → invoices
   - Verify invoice record was created with correct properties

---

## Troubleshooting

### QR Code Not Generating

**Possible causes:**
- Competition has no Swish number configured
- Swish number format invalid (must be 10 digits starting with 0)
- Amount invalid (must have exactly 2 decimal places)
- Invoice creation failed (check document types exist)

**Solution:**
- Check browser console for JavaScript errors
- Check server logs for backend errors
- Verify document types are created correctly

### Payment Status Not Updating

**Possible causes:**
- PaymentService not registered in DI
- Document type properties don't match expected names
- Permission issues (member can't update content)

**Solution:**
- Verify Program.cs has `builder.Services.AddScoped<HpskSite.Services.PaymentService>();`
- Check document type property aliases match exactly
- Check user has permissions to modify invoice content

### Invoice Not Created

**Possible causes:**
- registrationInvoicesHub doesn't exist under competition
- registrationInvoice document type not created
- Member doesn't have registered for competition

**Solution:**
- Manually create registrationInvoicesHub under competition if needed
- Verify both document types exist
- Check member has valid registration records

---

## Future Enhancements

Potential improvements for future development:

1. **Automatic Payment Verification** - Integrate with Swish API for automatic payment callbacks
2. **Payment Reminders** - Email reminders for unpaid invoices
3. **Bulk Payment Updates** - Mark multiple payments as paid at once
4. **Payment Reports** - Export payment data for accounting
5. **Partial Payments** - Support for partial payment installments
6. **Refund Processing** - Automatic refund initiation via Swish API

---

## Support & Documentation

For technical issues or questions:
- Check CLAUDE.md for overall project architecture
- See SwishController.cs for implementation details
- See PaymentService.cs for business logic
- Contact system administrator for Swish merchant account issues

---

**Last Updated:** 2025-01-12
**Version:** 1.0
**Status:** Active
