using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>Ett stödlägestillstånd ur <c>dbo.SiteSupportGrant</c>.</summary>
    public sealed class SupportGrantRow
    {
        public int Id { get; set; }
        public int? MemberId { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public string Reason { get; set; } = "";
        public DateTime CreatedDate { get; set; }
        public DateTime ExpiresDate { get; set; }
        public DateTime? EndedDate { get; set; }
    }

    /// <summary>
    /// Stödläget (klubbsidorna 2b, plan v3 B2/H2): kretsadmin i klubbens krets och sajtadmin skriver på en klubbs sidor bara i ett LOGGAT,
    /// tidsbegränsat läge med en uttryckligen angiven anledning. Tillståndet ligger i SQL (fungerar över flera instanser), gäller en person,
    /// en ägare och högst <see cref="Minutes"/> minuter, och kan avslutas i förtid. Ren SQL över <see cref="IDatabase"/>, provad mot en riktig SQL Server.
    ///
    /// <para>Vem som FÅR öppna ett stödläge avgörs av controllern (<c>AdminAuthorizationService</c>), inte här; det här är bara tillståndet.</para>
    /// </summary>
    public static class SiteSupport
    {
        public const int Minutes = 60;
        public const int MinReasonLength = 10;
        public const int MaxReasonLength = 300;

        /// <summary>Anledningen är obligatorisk, 10–300 tecken, utan kontrolltecken. Null = godkänd, annars meddelandet.</summary>
        public static string? ReasonProblem(string? reason)
        {
            var r = (reason ?? "").Trim();
            if (r.Length < MinReasonLength) return $"Skriv varför du behöver ändra föreningens sidor (minst {MinReasonLength} tecken). Klubbens administratörer får se anledningen.";
            if (r.Length > MaxReasonLength) return $"Anledningen får vara högst {MaxReasonLength} tecken.";
            if (r.Any(c => char.IsControl(c) && c != '\n')) return "Anledningen innehåller otillåtna tecken.";
            return null;
        }

        /// <summary>Öppnar ett stödläge (avslutar personens tidigare öppna för samma ägare först). Returnerar tillståndets id.</summary>
        public static int Start(IDatabase db, int memberId, string ownerType, int ownerId, string reason, DateTime now)
        {
            var problem = ReasonProblem(reason);
            if (problem != null) throw new ArgumentException(problem, nameof(reason));
            db.Execute(
                "UPDATE dbo.SiteSupportGrant SET EndedDate = @3 WHERE MemberId = @0 AND OwnerType = @1 AND OwnerId = @2 AND EndedDate IS NULL AND ExpiresDate > @3",
                memberId, ownerType, ownerId, now);
            return db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteSupportGrant (MemberId, OwnerType, OwnerId, Reason, CreatedDate, ExpiresDate)
                  VALUES (@0, @1, @2, @3, @4, @5); SELECT CAST(SCOPE_IDENTITY() AS int)",
                memberId, ownerType, ownerId, reason.Trim(), now, now.AddMinutes(Minutes));
        }

        /// <summary>Personens öppna, ej utgångna och ej avslutade stödläge för just den här ägaren, eller null. Sorten följer id:t.</summary>
        public static SupportGrantRow? Active(IDatabase db, int memberId, string ownerType, int ownerId, DateTime now) =>
            db.FirstOrDefault<SupportGrantRow>(
                @"SELECT TOP 1 Id, MemberId, OwnerType, OwnerId, Reason, CreatedDate, ExpiresDate, EndedDate FROM dbo.SiteSupportGrant
                  WHERE MemberId = @0 AND OwnerType = @1 AND OwnerId = @2 AND EndedDate IS NULL AND ExpiresDate > @3 ORDER BY Id DESC",
                memberId, ownerType, ownerId, now);

        /// <summary>Avslutar personens öppna stödläge för ägaren. Returnerar antalet avslutade.</summary>
        public static int End(IDatabase db, int memberId, string ownerType, int ownerId, DateTime now) =>
            db.Execute(
                "UPDATE dbo.SiteSupportGrant SET EndedDate = @3 WHERE MemberId = @0 AND OwnerType = @1 AND OwnerId = @2 AND EndedDate IS NULL AND ExpiresDate > @3",
                memberId, ownerType, ownerId, now);
    }
}
