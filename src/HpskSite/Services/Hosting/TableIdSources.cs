using HpskSite.Models.Competitions;
using HpskSite.Services.Addresses;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// ⚖️ Id-lagen, regel 3: allt som bär ett id av en sort som flyttats från noder till en tabell. Tabellens
    /// räknare hålls över det högsta av dem (<see cref="IdentityFloorHostedService"/> vid varje start, och
    /// hälsokontrollen läser samma listor). Låg förut i varje flyttjänst; flyttjänsterna togs bort när flytten
    /// var klar (2026-10-20), men listorna gäller så länge tabellerna finns.
    /// <para>Ny tabell eller kolumn som bär ett sådant id: lägg den här.</para>
    /// </summary>
    internal static class TableIdSources
    {
        /// <summary>Anmälningar (dbo.CompetitionRegistration).</summary>
        internal static readonly IdentityFloor.Source[] Registrations =
        {
            new("dbo.CompetitionRegistrationLegacyId", "SELECT MAX(LegacyNodeId) FROM dbo.CompetitionRegistrationLegacyId"),
            new("dbo.LedgerPayment", "SELECT MAX(SourceItemId) FROM dbo.LedgerPayment WHERE SourceType = 'competition-registration'"),
            new("sbx.LedgerPayment", "SELECT MAX(SourceItemId) FROM sbx.LedgerPayment WHERE SourceType = 'competition-registration'"),
            new("dbo.CompetitionFeeChoice", "SELECT MAX(RegistrationId) FROM dbo.CompetitionFeeChoice"),
            IdentityFloor.NodesOfType("competitionRegistration"),
            IdentityFloor.NodesOfType("competitionTeamRegistration"),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'registrationId'"),
        };

        /// <summary>Händelser (dbo.ClubEvent).</summary>
        internal static readonly IdentityFloor.Source[] ClubEvents =
        {
            new("dbo.ClubEventParticipant", "SELECT MAX(EventId) FROM dbo.ClubEventParticipant WHERE OccasionKind = 'Event'"),
            new("dbo.FirearmBooking", "SELECT MAX(OccasionId) FROM dbo.FirearmBooking WHERE OccasionKind = 'Event'"),
            new("dbo.LedgerPayment", "SELECT MAX(SourceId) FROM dbo.LedgerPayment WHERE SourceType = 'event'"),
            new("sbx.LedgerPayment", "SELECT MAX(SourceId) FROM sbx.LedgerPayment WHERE SourceType = 'event'"),
            new("dbo.LedgerProject", "SELECT MAX(SourceId) FROM dbo.LedgerProject WHERE SourceType = 'event'"),
            new("sbx.LedgerProject", "SELECT MAX(SourceId) FROM sbx.LedgerProject WHERE SourceType = 'event'"),
            new("dbo.ClubEventRange", "SELECT MAX(EventId) FROM dbo.ClubEventRange"),
            new("dbo.CompetitionEventLink", "SELECT MAX(EventId) FROM dbo.CompetitionEventLink"),
            new("dbo.PageAddress", "SELECT MAX(TargetId) FROM dbo.PageAddress WHERE TargetKind = 'event'"),
            IdentityFloor.NodesOfType("clubSimpleEvent"),
        };

        /// <summary>Startlistor (dbo.CompetitionStartList).</summary>
        internal static readonly IdentityFloor.Source[] StartLists =
        {
            IdentityFloor.NodesOfType("precisionStartList"), // startliststore-ok: bara högsta id:t, ingen läsning
            IdentityFloor.NodesOfType("finalsStartList"),    // startliststore-ok: bara högsta id:t, ingen läsning
            new("dbo.CompetitionStartList", "SELECT MAX(QualificationStartListId) FROM dbo.CompetitionStartList"),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'qualificationStartListId'"),
            new("dbo.ScheduleReminderLog", @"SELECT MAX(TRY_CONVERT(int, LEFT(SUBSTRING(r.SourceKey, CHARINDEX('-', r.SourceKey) + 1, 20),
                    CHARINDEX('-', SUBSTRING(r.SourceKey, CHARINDEX('-', r.SourceKey) + 1, 20) + '-') - 1)))
                FROM dbo.ScheduleReminderLog r
                WHERE r.SourceKey LIKE 'lag-%' OR r.SourceKey LIKE 'final-%' OR r.SourceKey LIKE 'spring-%' OR r.SourceKey LIKE 'stafett-%'"),
        };

        /// <summary>Resultatlistor (dbo.CompetitionResultList).</summary>
        internal static readonly IdentityFloor.Source[] ResultLists =
        {
            IdentityFloor.NodesOfType("competitionResult"), // resultstore-ok: bara högsta id:t, ingen läsning
        };

        /// <summary>Tävlingar (dbo.Competition).</summary>
        internal static readonly IdentityFloor.Source[] Competitions =
        {
            IdentityFloor.NodesOfType(CompetitionFieldTable.NodeAlias),
            IdentityFloor.ColumnsLike("%CompetitionId"),
            IdentityFloor.ColumnsLike("TargetId", "TargetKind", PageAddressKinds.Competition, CompetitionFieldTable.NodeAlias),
            // Liggaren: anmälningsavgifter, lagavgifter, fakturor till klubbar och projekt bär TÄVLINGENS id.
            // (legacy-invoice bär fakturans nod-id och hör inte hit.)
            IdentityFloor.ColumnsLike("SourceId", "SourceType", Models.Ledger.LedgerSourceType.CompetitionRegistration,
                Models.Ledger.LedgerSourceType.TeamFee, Models.Ledger.LedgerSourceType.CompetitionInvoice,
                Models.Ledger.LedgerProjectSource.Competition),
            // Vapnets användning (FirearmUsage) bär aktivitetens sort.
            IdentityFloor.ColumnsLike("SourceId", "SourceKind", Models.MemberActivityEntry.SourceKindCompetition),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'competitionId'"),
        };

        /// <summary>Serier (dbo.CompetitionSeries).</summary>
        internal static readonly IdentityFloor.Source[] Series =
        {
            IdentityFloor.NodesOfType(CompetitionSeriesFieldTable.NodeAlias),
            new("dbo.Competition", "SELECT MAX(SeriesId) FROM dbo.Competition"),
            IdentityFloor.ColumnsLike("TargetId", "TargetKind", PageAddressKinds.Series, CompetitionSeriesFieldTable.NodeAlias),
            IdentityFloor.ColumnsLike("SourceId", "SourceType", Models.Ledger.LedgerProjectSource.Series),
            new("dbo.umbracoPropertyData", @"SELECT MAX(COALESCE(pd.intValue, TRY_CONVERT(int, LEFT(pd.varcharValue, 20))))
                FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
                WHERE pt.alias = 'seriesId'"),
        };
    }
}
