using HpskSite.Models;
using HpskSite.Models.Firearms;
using NPoco;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    public static class ForeningsintygRequestKind
    {
        /// <summary>Nytt vapen — medlemmen söker licens och äger det oftast inte ännu.</summary>
        public const string NyttVapen = "NyttVapen";

        /// <summary>Förnyelse av ett vapen som redan finns i garderoben.</summary>
        public const string Fornyelse = "Fornyelse";

        public static readonly string[] All = { NyttVapen, Fornyelse };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            NyttVapen => "Nytt vapen",
            Fornyelse => "Förnyelse",
            _ => v ?? "",
        };
    }

    public static class ForeningsintygRequestStatus
    {
        public const string Ny = "Ny";
        public const string UnderBehandling = "UnderBehandling";
        public const string Utfardad = "Utfardad";
        public const string Avslagen = "Avslagen";

        /// <summary>
        /// Medlemmen tog bort vapnet, alltså finns ingenting kvar att intyga.
        ///
        /// <para><b>⚠️ EGEN STATUS, inte "Avslagen".</b> Ett avslag är klubbens beslut och kräver ett
        /// skäl medlemmen kan läsa; det här är medlemmens eget tillbakadragande. Att bunta ihop dem
        /// skulle skriva in i klubbens historik att den nekat något den aldrig prövade.</para>
        /// </summary>
        public const string Aterkallad = "Aterkallad";

        public static readonly string[] All = { Ny, UnderBehandling, Utfardad, Avslagen, Aterkallad };

        // ⚠️ `Aterkallad` hör INTE hit. Vore den öppen skulle den ligga kvar i den gula rutan och
        // räknas i menybrickan — alltså arbete som inte finns.
        public static readonly string[] Open = { Ny, UnderBehandling };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? v) => (v ?? "").Trim() switch
        {
            Ny => "Ny",
            UnderBehandling => "Under behandling",
            Utfardad => "Utfärdad",
            Avslagen => "Avslagen",
            Aterkallad => "Återkallad",
            _ => v ?? "",
        };
    }

    [TableName("ForeningsintygRequest")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ForeningsintygRequest
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public string Kind { get; set; } = ForeningsintygRequestKind.NyttVapen;
        public int FirearmId { get; set; }
        public string Forbund { get; set; } = string.Empty;
        public string? VapengruppSkytteform { get; set; }
        public string? MemberMessage { get; set; }
        public string Status { get; set; } = ForeningsintygRequestStatus.Ny;
        public int? HandledByMemberId { get; set; }
        public DateTime? HandledAt { get; set; }
        public string? HandlerNote { get; set; }
        public int? IssuedIntygId { get; set; }
        public DateTime CreatedAt { get; set; }

        // Visningsfält, inte kolumner.
        [ResultColumn] public string? MemberName { get; set; }
        [ResultColumn] public string? FirearmAlias { get; set; }
        [ResultColumn] public string? FirearmWeaponClass { get; set; }
        [ResultColumn] public string? FirearmVapentyp { get; set; }

        /// <summary>Falskt = medlemmen har tagit bort vapnet ur sin garderob sedan förfrågan skapades.</summary>
        [ResultColumn] public bool FirearmIsActive { get; set; } = true;

        [Ignore] public string KindLabel => ForeningsintygRequestKind.Label(Kind);
        [Ignore] public string StatusLabel => ForeningsintygRequestStatus.Label(Status);
        [Ignore] public bool IsOpen => ForeningsintygRequestStatus.Open.Contains(Status, StringComparer.Ordinal);
    }

    /// <summary>
    /// Förfrågningar om föreningsintyg. Medlemmen begär, klubben behandlar.
    ///
    /// <para><b>⚠️ Tjänsten lämnar aldrig ut en vapenuppgift.</b> Den bär förfrågans egna fält plus
    /// vapnets KLARTEXTKOLUMNER (alias, vapengrupp, vapentyp) — allt annat läser utfärdaren via
    /// <c>FirearmService.RevealDetailsAsync</c>, alltså genom grinden och med en loggrad.</para>
    /// </summary>
    public class ForeningsintygRequestService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly FirearmService _firearms;
        private readonly ILogger<ForeningsintygRequestService> _logger;

        public ForeningsintygRequestService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            FirearmService firearms,
            ILogger<ForeningsintygRequestService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _firearms = firearms;
            _logger = logger;
        }

        /// <summary>
        /// Skapar en förfrågan.
        ///
        /// <para><b>⚠️ Vapnet måste tillhöra medlemmen.</b> Utan kontrollen kunde en inloggad medlem
        /// begära ett intyg för någon annans vapen — och därmed få klubben att läsa den personens
        /// uppgifter.</para>
        /// </summary>
        public (int RequestId, string? Error) Create(
            int memberId, int clubId, string kind, int firearmId,
            string forbund, string? vapengrupp, string? message)
        {
            if (memberId <= 0 || clubId <= 0) return (0, "Ogiltig medlem eller klubb.");
            if (!ForeningsintygRequestKind.IsValid(kind)) return (0, "Ogiltig typ av förfrågan.");

            var firearm = _firearms.GetById(firearmId);
            if (firearm is null) return (0, "Vapnet hittades inte.");
            if (firearm.Scope != FirearmScope.Member(memberId))
                return (0, "Du kan bara begära intyg för dina egna vapen.");

            var fb = (forbund ?? "").Trim();
            if (!ForeningsintygDocument.AllaForbund.Contains(fb, StringComparer.Ordinal))
                return (0, "Välj ett förbund ur listan.");

            // ⚠️ En förnyelse gäller per definition ett vapen medlemmen INNEHAR. Att tillåta
            // 'Fornyelse' på ett planerat vapen skulle ge klubben en motsägelse att tolka: intyget
            // säger "förnyelse" om en licens som aldrig funnits.
            if (kind == ForeningsintygRequestKind.Fornyelse
                && firearm.AcquisitionStatus != FirearmAcquisitionStatus.Innehas)
                return (0, "En förnyelse gäller ett vapen du redan innehar. " +
                           "Är det ett nytt vapen väljer du \"Nytt vapen\" i stället.");

            // En öppen förfrågan för samma vapen och förbund är en dubblett. Att skapa två skulle
            // ge klubben samma arbete två gånger utan att något skiljer dem.
            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var openStatuses = string.Join(",", ForeningsintygRequestStatus.Open.Select(s => $"'{s}'"));
            var duplicate = db.ExecuteScalar<int>(
                $@"SELECT COUNT(*) FROM ForeningsintygRequest
                    WHERE MemberId = @0 AND FirearmId = @1 AND Forbund = @2
                      AND Status IN ({openStatuses})", memberId, firearmId, fb);
            if (duplicate > 0)
                return (0, "Du har redan en obehandlad förfrågan för det här vapnet.");

            var row = new ForeningsintygRequest
            {
                MemberId = memberId,
                ClubId = clubId,
                Kind = kind.Trim(),
                FirearmId = firearmId,
                Forbund = fb,
                VapengruppSkytteform = Trim(vapengrupp, 200),
                MemberMessage = Trim(message, 1000),
                Status = ForeningsintygRequestStatus.Ny,
                CreatedAt = DateTime.Now,
            };
            db.Insert(row);

            _logger.LogInformation(
                "Föreningsintygsförfrågan {Id} skapad av medlem {MemberId} till klubb {ClubId} ({Kind}).",
                row.Id, memberId, clubId, kind);

            return (row.Id, null);
        }

        /// <summary>Klubbens inkorg. Öppna först, sedan avslutade.</summary>
        public List<ForeningsintygRequest> GetForClub(int clubId, bool openOnly = false)
        {
            if (clubId <= 0) return new List<ForeningsintygRequest>();

            // ⚠️ `f.IsActive` MÅSTE med, och joinen får INTE filtrera på den. Medlemmen kan ta bort
            // vapnet efter att förfrågan skapades — då försvinner raden ur klubbens inkorg om
            // joinen filtrerar, alltså ett ärende som tystnar mitt i handläggningen. Klubben ska i
            // stället SE att vapnet är borttaget.
            var sql = @"SELECT r.*, f.Alias AS FirearmAlias, f.WeaponClass AS FirearmWeaponClass,
                               f.Vapentyp AS FirearmVapentyp, f.IsActive AS FirearmIsActive
                          FROM ForeningsintygRequest r
                          JOIN Firearm f ON f.Id = r.FirearmId
                         WHERE r.ClubId = @0";
            if (openOnly)
                sql += " AND r.Status IN (" +
                       string.Join(",", ForeningsintygRequestStatus.Open.Select(s => $"'{s}'")) + ")";
            sql += " ORDER BY CASE WHEN r.Status IN ('Ny','UnderBehandling') THEN 0 ELSE 1 END, r.CreatedAt DESC";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var rows = uow.Database.Fetch<ForeningsintygRequest>(sql, clubId);
            ResolveNames(rows);
            return rows;
        }

        /// <summary>Medlemmens egna förfrågningar.</summary>
        public List<ForeningsintygRequest> GetForMember(int memberId)
        {
            if (memberId <= 0) return new List<ForeningsintygRequest>();

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.Fetch<ForeningsintygRequest>(
                @"SELECT r.*, f.Alias AS FirearmAlias, f.WeaponClass AS FirearmWeaponClass,
                         f.Vapentyp AS FirearmVapentyp
                    FROM ForeningsintygRequest r
                    JOIN Firearm f ON f.Id = r.FirearmId
                   WHERE r.MemberId = @0
                   ORDER BY r.CreatedAt DESC", memberId);
        }

        public ForeningsintygRequest? GetById(int requestId)
        {
            if (requestId <= 0) return null;
            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.FirstOrDefault<ForeningsintygRequest>(
                "SELECT * FROM ForeningsintygRequest WHERE Id = @0", requestId);
        }

        /// <summary>
        /// Klubbens statusändring.
        ///
        /// <para><b>⚠️ Ett avslag kräver ett skäl.</b> Ett avslag utan förklaring blir ett
        /// supportärende till klubben, och medlemmen har ingen väg vidare.</para>
        /// </summary>
        public string? SetStatus(int requestId, string status, int actorMemberId, string? note, int? issuedIntygId = null)
        {
            if (!ForeningsintygRequestStatus.IsValid(status)) return "Ogiltig status.";

            var existing = GetById(requestId);
            if (existing is null) return "Förfrågan hittades inte.";

            if (status == ForeningsintygRequestStatus.Avslagen && string.IsNullOrWhiteSpace(note))
                return "Ange ett skäl för avslaget — medlemmen ska kunna se varför.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            uow.Database.Execute(
                @"UPDATE ForeningsintygRequest
                     SET Status = @0, HandledByMemberId = @1, HandledAt = @2,
                         HandlerNote = @3, IssuedIntygId = COALESCE(@4, IssuedIntygId)
                   WHERE Id = @5",
                status.Trim(), actorMemberId, DateTime.Now,
                Trim(note, 1000), issuedIntygId, requestId);

            _logger.LogInformation(
                "Föreningsintygsförfrågan {Id} satt till {Status} av medlem {Actor}.",
                requestId, status, actorMemberId);
            return null;
        }

        /// <summary>
        /// Återkallar medlemmens ÖPPNA förfrågningar för ett vapen hen just tagit bort, och
        /// returnerar deras id:n.
        ///
        /// <para><b>⚠️ ETT BORTTAGET VAPEN LÄMNAR ETT ÄRENDE SOM INTE GÅR ATT HANDLÄGGA.</b>
        /// Förfrågan pekar på vapnet, och utfärdaren hämtar fabrikat, modell och kaliber därifrån —
        /// på ett vapen medlemmen inte längre har. Rapporterat ur prod: förfrågan låg kvar i
        /// klubbens inkorg efter att medlemmen raderat vapnet. Att ta bort vapnet ÄR att dra
        /// tillbaka begäran.</para>
        ///
        /// <para><b>⚠️ BARA ÖPPNA förfrågningar.</b> En `Utfardad` rör vi inte: dokumentet finns, det
        /// är utfärdat och undertecknat, och att medlemmen senare städar sin garderob gör inte
        /// handlingen ogjord. En `Avslagen` är likaså redan avgjord.</para>
        ///
        /// <para><b>⚠️ Vapnet raderas aldrig</b> (`IsActive = 0`), så förfrågans join håller och
        /// klubben kan fortfarande läsa vad ärendet gällde.</para>
        /// </summary>
        public List<int> WithdrawOpenForFirearm(int firearmId, int memberId)
        {
            if (firearmId <= 0 || memberId <= 0) return new List<int>();

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var openStatuses = string.Join(",", ForeningsintygRequestStatus.Open.Select(x => $"'{x}'"));

            // ⚠️ MedlemsId i WHERE, inte bara vapnets. Ett vapen tillhör en medlem, men grinden ska
            // vara strukturell: ingen kan råka återkalla någon annans ärende genom ett vapen-id.
            var ids = db.Fetch<int>(
                $@"SELECT Id FROM ForeningsintygRequest
                    WHERE FirearmId = @0 AND MemberId = @1 AND Status IN ({openStatuses})",
                firearmId, memberId);
            if (ids.Count == 0) return ids;

            db.Execute(
                $@"UPDATE ForeningsintygRequest
                      SET Status = @0, HandledAt = @1,
                          HandlerNote = @2
                    WHERE FirearmId = @3 AND MemberId = @4 AND Status IN ({openStatuses})",
                ForeningsintygRequestStatus.Aterkallad, DateTime.Now,
                "Medlemmen tog bort vapnet ur sin garderob — förfrågan är återkallad.",
                firearmId, memberId);

            _logger.LogInformation(
                "Vapen {FirearmId} borttaget av medlem {MemberId} — förfrågan {Requests} återkallad.",
                firearmId, memberId, string.Join(", ", ids));

            return ids;
        }

        /// <summary>
        /// Öppnar varje förfrågan som pekade på ett nu BORTTAGET intyg, och returnerar deras id:n.
        ///
        /// <para><b>⚠️ ETT BORTTAGET DOKUMENT FÅR INTE LÄMNA FÖRFRÅGAN "UTFÄRDAD".</b> Statusen
        /// skulle då påstå att medlemmen fått ett intyg som inte finns — precis det trasiga
        /// tillstånd inkorgen larmar om med "Markerad utfärdad — intyg saknas", och som den gamla
        /// knappen kunde skapa. Raderar man dokumentet är arbetet ogjort, alltså är förfrågan
        /// obehandlad igen.</para>
        ///
        /// <para><b>⚠️ FÖRFRÅGAN RADERAS ALDRIG.</b> Den är medlemmens begäran, inte klubbens
        /// arbetsrad. Det normala skälet att ta bort ett intyg är att det blev fel och ska skrivas
        /// om — och då måste det finnas något att skriva om det FRÅN, med medlemmens meddelande och
        /// vilket vapen det gäller. Ska ärendet i stället avvisas finns avslagsvägen, som kräver ett
        /// skäl medlemmen kan läsa. En radering av förfrågan hade tagit bort båda möjligheterna och
        /// dessutom varit osynlig för medlemmen.</para>
        ///
        /// <para><b>⚠️ `HandlerNote` SKRIVS ÖVER MED FLIT.</b> Den bar "intyget utfärdat"-spåret, och
        /// det är inte längre sant. Den nya texten säger vad som hände, så nästa läsare inte tror
        /// att förfrågan aldrig behandlats.</para>
        /// </summary>
        /// <returns>
        /// <c>Reopened</c> = förfrågningar som ligger som obehandlade igen, <c>Withdrawn</c> = de
        /// vars vapen är borta och som därför återkallades.
        ///
        /// <para><b>⚠️ BÅDA LISTORNA RETURNERAS för att anroparen ska kunna säga vad som HÄNDE.</b>
        /// En tidigare version returnerade bara id:n, och kvittot påstod därför "förfrågan ligger nu
        /// som obehandlad igen" även när den i själva verket återkallades — utfärdaren letade efter
        /// en rad i den gula rutan som aldrig kom. Exakt den sortens lögn som
        /// notifieringskvittona redan tvingats rätta en gång.</para>
        /// </returns>
        public (List<int> Reopened, List<int> Withdrawn) ReopenAfterIntygRemoved(int intygId, int actorMemberId)
        {
            var reopened = new List<int>();
            var withdrawn = new List<int>();
            if (intygId <= 0) return (reopened, withdrawn);

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var rows = db.Fetch<ForeningsintygRequest>(
                "SELECT * FROM ForeningsintygRequest WHERE IssuedIntygId = @0", intygId);
            if (rows.Count == 0) return (reopened, withdrawn);

            // ⚠️⚠️ ÅTERÖPPNA ALDRIG ETT ÄRENDE VARS VAPEN ÄR BORTA. Rapporterat ur prod i samma
            // andetag som fallet ovan: medlemmen hade tagit bort vapnet, intyget raderades, och
            // förfrågan kom då tillbaka som obehandlad — alltså en rad i den gula rutan som ingen
            // kan handlägga, eftersom uppgifterna hämtas ur ett vapen som inte finns kvar.
            // Återöppningen är rätt för "det blev fel, skriv om"; är vapnet borta har medlemmen
            // dragit tillbaka sin begäran och statusen ska säga just det.
            foreach (var row in rows)
            {
                var firearm = _firearms.GetById(row.FirearmId);
                bool firearmGone = firearm is null || !firearm.IsActive;
                (firearmGone ? withdrawn : reopened).Add(row.Id);

                db.Execute(
                    @"UPDATE ForeningsintygRequest
                         SET Status = @0, IssuedIntygId = NULL,
                             HandledByMemberId = NULL, HandledAt = @1, HandlerNote = @2
                       WHERE Id = @3",
                    firearmGone
                        ? ForeningsintygRequestStatus.Aterkallad
                        : ForeningsintygRequestStatus.Ny,
                    firearmGone ? (DateTime?)DateTime.Now : null,
                    firearmGone
                        ? "Intyget togs bort och vapnet finns inte längre i medlemmens garderob — " +
                          "förfrågan är återkallad."
                        : "Det utfärdade intyget togs bort — förfrågan är obehandlad igen.",
                    row.Id);
            }

            _logger.LogInformation(
                "Föreningsintyg {IntygId} borttaget av medlem {Actor} — öppnade igen: {Reopened}; " +
                "återkallade (vapnet borta): {Withdrawn}.",
                intygId, actorMemberId,
                reopened.Count > 0 ? string.Join(", ", reopened) : "inga",
                withdrawn.Count > 0 ? string.Join(", ", withdrawn) : "inga");

            return (reopened, withdrawn);
        }

        /// <summary>Antal öppna förfrågningar — badgen på klubbens flik.</summary>
        public int CountOpenForClub(int clubId)
        {
            if (clubId <= 0) return 0;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var openStatuses = string.Join(",", ForeningsintygRequestStatus.Open.Select(s => $"'{s}'"));
                return uow.Database.ExecuteScalar<int>(
                    $"SELECT COUNT(*) FROM ForeningsintygRequest WHERE ClubId = @0 AND Status IN ({openStatuses})",
                    clubId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte räkna öppna förfrågningar för klubb {ClubId}.", clubId);
                return 0;
            }
        }

        private void ResolveNames(List<ForeningsintygRequest> rows)
        {
            foreach (var id in rows.Select(r => r.MemberId).Distinct())
            {
                var m = _memberService.GetById(id);
                if (m is null) continue;
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                var resolved = string.IsNullOrWhiteSpace(name) ? m.Name ?? $"Medlem {id}" : name;
                foreach (var r in rows.Where(r => r.MemberId == id)) r.MemberName = resolved;
            }
        }

        private static string? Trim(string? v, int max) =>
            string.IsNullOrWhiteSpace(v) ? null : (v.Length <= max ? v.Trim() : v.Trim()[..max]);
    }
}
