using System.Reflection;
using NPoco;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// VILKA lagrade tider som är UTC. Allt annat i våra tabeller är svensk tid (DateTime.Now, GETDATE()
    /// eller ett datum en människa skrivit in) — prod-servern kör i svensk tid.
    ///
    /// Registret bygger på inventeringen av alla 351 datumkolumner (fas E, 2026-10-17): vilken kod som
    /// skriver varje kolumn, och med vilken klocka. Kolumnnamnet räcker INTE — 59 av UTC-kolumnerna heter
    /// CreatedAt/CreatedDate/EnteredAt/LastModified, som andra tabellers svenska kolumner.
    ///
    /// ⚠️ Ny kolumn som skrivs med DateTime.UtcNow eller SYSUTCDATETIME(): lägg den här, annars visas den två
    /// timmar fel. Hellre: döp den till …Utc OCH lägg den här (StoredTimeBasisTests kräver att alla …Utc finns).
    /// </summary>
    public static class StoredTimeBasis
    {
        public static readonly HashSet<string> UtcColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "CertificationRequests.CreatedAt", "CertificationRequests.RequestedAt", "CertificationRequests.ReviewedAt",
            "ClubMembership.CreatedDate",
            "CompetitionChampions.EnteredAt",
            "CompetitionChange.AtUtc",
            "CompetitionEventLink.LinkedUtc",
            "CompetitionFeeChoice.ChosenUtc",
            "CompetitionFeeSettings.UpdatedUtc",
            "CompetitionPaymentModel.DecidedUtc",
            "CompetitionRecords.EnteredAt",
            "CompetitionStoreVersion.UpdatedUtc",
            "CompetitionTeam.CreatedAt",
            "RateCounter.WindowEndsUtc",
            "MemberToken.CreatedUtc",
            "MemberToken.ExpiresUtc",
            "MemberTutorialWatch.LastWatchedUtc",
            "CompetitionTeamMember.JoinedAt",
            "CourseReviewers.GrantedAt",
            "DataProtectionKey.CreatedUtc",
            "DeviceRegistrations.CreatedDateUtc", "DeviceRegistrations.UpdatedDateUtc",
            "DocumentCategories.CreatedAt",
            "Documents.CreatedAt", "Documents.UpdatedAt",
            "EventMessage.CreatedDate",
            "EventMessageAck.AckDate",
            "FaltskyttePatrol.DepartedAt",
            "FaltskytteResultEntry.EnteredAt", "FaltskytteResultEntry.LastModified",
            "FaltskytteShootOffEntry.EnteredAt", "FaltskytteShootOffEntry.LastModified",
            "InvoicePaymentEvents.OccurredAt",
            "LandingPage.UpdatedUtc",
            "LedgerApproval.ApprovedUtc",
            "LedgerAsset.CreatedUtc",
            "LedgerAttachment.UploadedUtc", "LedgerAttachment.VoidedUtc",
            "LedgerAuditEvent.OccurredUtc",
            "LedgerAuditorGrant.AcceptedUtc", "LedgerAuditorGrant.ExpiresUtc", "LedgerAuditorGrant.InvitedUtc", "LedgerAuditorGrant.LastSeenUtc", "LedgerAuditorGrant.RevokedUtc",
            "LedgerBankImport.ImportedUtc",
            "LedgerBankRow.MatchedUtc",
            "LedgerBudget.CreatedUtc",
            "LedgerCharge.CreatedUtc", "LedgerCharge.SentUtc", "LedgerCharge.VoidedUtc",
            "LedgerExpense.ApprovedUtc", "LedgerExpense.RegisteredUtc",
            "LedgerFiscalYear.EstablishedDate",
            "LedgerIssuer.AbandonedUtc", "LedgerIssuer.CreatedUtc",
            "LedgerJournalEntry.RegisteredUtc",
            "LedgerJournalEntryDraft.CommitClaimedUtc", "LedgerJournalEntryDraft.CreatedUtc", "LedgerJournalEntryDraft.UpdatedUtc",
            "LedgerLegacyInvoiceDismissal.DismissedUtc",
            "LedgerLegacyInvoiceLink.LinkedUtc",
            "LedgerNote.CreatedUtc", "LedgerNote.ModifiedUtc",
            "LedgerNumberGap.RecordedUtc",
            "LedgerPayment.ClaimedUtc", "LedgerPayment.ConfirmedUtc", "LedgerPayment.CreatedUtc", "LedgerPayment.VoidedUtc",
            "LedgerProject.CreatedUtc",
            "LedgerProjectGroup.CreatedUtc",
            "LedgerProjectGroupMember.AddedUtc",
            "LedgerReceipt.IssuedUtc",
            "LedgerWriteGrant.GrantedUtc", "LedgerWriteGrant.RevokedUtc",
            "MemberAccessKey.CreatedDate",
            "MemberActivity.LastMobileUtc", "MemberActivity.LastWebUtc",
            "MemberCertificateIssue.CreatedDate",
            "MemberCertifications.CreatedAt", "MemberCertifications.RevokedAt",
            "MembershipFeeCategory.CreatedDate",
            "MembershipFeeCharge.CreatedDate", "MembershipFeeCharge.MemberChosenAt", "MembershipFeeCharge.PaidDate", "MembershipFeeCharge.PaymentSentDate", "MembershipFeeCharge.RequestSentDate",
            "MembershipFeeChargeLine.CreatedUtc",
            "MembershipFeePayerEmail.UpdatedUtc",
            "RankingPushLog.NotifiedAt",
            "RankingSnapshot.CreatedAt",
            "RefreshTokens.CreatedAt", "RefreshTokens.ExpiresAt", "RefreshTokens.RevokedAt",
            "RegionFeeCompetition.CreatedUtc",
            "SiteSetting.UpdatedUtc",
            "StaffAssignment.CheckedInAt", "StaffAssignment.CreatedDate", "StaffAssignment.ModifiedDate",
            "StaffAvailability.CreatedDate",
            "StaffDay.CreatedDate",
            "StaffHelpSignup.CreatedDate", "StaffHelpSignup.ModifiedDate",
            "StaffHelpSlot.CreatedDate",
            "StaffingSourceScope.CreatedDate",
            "StaffingTemplate.CreatedDate", "StaffingTemplate.ModifiedDate",
            "StaffPass.CreatedDate",
            "StaffRequestLog.CreatedDate",
            "StaffRole.CreatedDate", "StaffRole.ModifiedDate",
            "TrainingMatchGuests.ClaimTokenExpiresAt", "TrainingMatchGuests.CreatedAt", "TrainingMatchGuests.SessionExpiresAt",
            "VisitorLogs.VisitedAt",
            "WebPushSubscription.CreatedAt",
            "WorkArea.CreatedDate",
            "WorkItem.CreatedDate", "WorkItem.ModifiedDate",
            "WorkItemComment.CreatedDate",
            "WorkItemDependency.CreatedDate",
            "WorkLink.CreatedDate",
        };

        public static bool IsUtc(string table, string column) => UtcColumns.Contains(table + "." + column);
    }

    /// <summary>
    /// Märker datumen som NPoco läser in efter vad de ÄR. Umbraco märker varje DateTime i en klass med
    /// Kind = Utc (UmbracoDefaultMapper, i både 16 och 17) — rätt för Umbracos systemdatum men fel för våra
    /// svenska kolumner: JSON fick ett Z och webbläsaren visade tiden två timmar för sent.
    ///
    /// För en klass i HpskSite med [TableName]: kolumnen i <see cref="StoredTimeBasis"/> → Utc, annars
    /// Unspecified (svensk tid). Datumegenskapernas lagrade värde (PropertyDataDto.DateValue) → Unspecified.
    /// En klass utan [TableName] (en DTO till en sammansatt fråga) lämnas åt Umbraco, eftersom tabellen inte
    /// går att avgöra — där gäller 16:s beteende. Ligger först bland NPoco-mapparna (första som svarar vinner).
    /// Skrivning påverkas inte: NPoco lagrar värdet som det är oavsett Kind (mätt).
    /// </summary>
    public sealed class StoredTimeMapper : DefaultMapper
    {
        private const string PropertyDataDtoName = "Umbraco.Cms.Infrastructure.Persistence.Dtos.PropertyDataDto";

        public override Func<object, object>? GetFromDbConverter(MemberInfo destMemberInfo, Type sourceType)
        {
            if (sourceType != typeof(DateTime)) return null;
            var memberType = destMemberInfo switch { PropertyInfo p => p.PropertyType, FieldInfo f => f.FieldType, _ => null };
            if (memberType != typeof(DateTime) && memberType != typeof(DateTime?)) return null;
            var kind = KindFor(destMemberInfo);
            if (kind == null) return null;
            var k = kind.Value;
            return value => value is DateTime d ? DateTime.SpecifyKind(d, k) : value;
        }

        /// <summary>Vilken Kind ett fält ska ha, eller null = lämna åt Umbraco.</summary>
        internal static DateTimeKind? KindFor(MemberInfo member)
        {
            var type = member.DeclaringType;
            if (type == null) return null;
            // PropertyDataDto är intern i Umbraco, därför jämförs namnet.
            if (type.FullName == PropertyDataDtoName && member.Name == "DateValue") return DateTimeKind.Unspecified;
            if (type.Namespace == null || !type.Namespace.StartsWith("HpskSite", StringComparison.Ordinal)) return null;
            var table = type.GetCustomAttribute<TableNameAttribute>()?.Value;
            if (string.IsNullOrEmpty(table)) return null;
            var column = member.GetCustomAttribute<ColumnAttribute>()?.Name;
            if (string.IsNullOrEmpty(column)) column = member.Name;
            return StoredTimeBasis.IsUtc(table, column) ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        }
    }

    /// <summary>Lägger <see cref="StoredTimeMapper"/> FÖRST bland NPoco-mapparna.</summary>
    public sealed class StoredTimeMapperComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            var mappers = builder.NPocoMappers();
            if (mappers == null) return;
            var existing = mappers.GetTypes().Where(t => t != typeof(StoredTimeMapper)).ToList();
            mappers.Clear();
            mappers.Add<StoredTimeMapper>();
            foreach (var t in existing) mappers.Add(t);
        }
    }
}
