using HpskSite.Models;
using HpskSite.Models.Firearms;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Vapenregistret. <b>Den enda vägen till vapendata</b> — grinden, läsloggen och den tvåstegs
    /// skrivningen bor här, så ingen controller kan komma åt en klartextuppgift utan att både
    /// behörigheten prövats och läsningen registrerats.
    ///
    /// <para><b>⚠️ Regeln: samma metod som lämnar ut klartext skriver loggraden.</b> Ett separat
    /// loggnings-anrop är något en ny kodväg kan glömma, och då är löftet "du ser vem som läst"
    /// tomt utan att någon märker det.</para>
    /// </summary>
    public class FirearmService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly FirearmProtector _protector;
        private readonly FirearmVaultService _vault;
        private readonly FirearmAuthorizationService _auth;
        private readonly FirearmAccessLogService _accessLog;
        private readonly ILogger<FirearmService> _logger;

        public FirearmService(
            IScopeProvider scopeProvider,
            FirearmProtector protector,
            FirearmVaultService vault,
            FirearmAuthorizationService auth,
            FirearmAccessLogService accessLog,
            ILogger<FirearmService> logger)
        {
            _scopeProvider = scopeProvider;
            _protector = protector;
            _vault = vault;
            _auth = auth;
            _accessLog = accessLog;
            _logger = logger;
        }

        // ── Läsning ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ägarens vapen med klartextkolumner och relationer — men <b>utan</b> de skyddade
        /// uppgifterna.
        ///
        /// <para><b>⚠️ Ingen loggrad skrivs här, med flit.</b> Loggen ska svara på "vem har läst
        /// mina uppgifter", och skulle varje rendering av en maskerad lista logga vore loggen brus —
        /// och en logg ingen läser är ingen kontroll. Raden skrivs när klartexten faktiskt lämnas
        /// ut, i <see cref="RevealDetailsAsync"/>.</para>
        /// </summary>
        public List<Firearm> GetForScope(FirearmScope scope, bool includeInactive = false)
        {
            if (!scope.IsValid) return new List<Firearm>();

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var sql = "SELECT * FROM Firearm WHERE ScopeKind = @0 AND ScopeId = @1";
            if (!includeInactive) sql += " AND IsActive = 1";
            sql += " ORDER BY SortOrder, ClubWeaponNumber, Alias";

            var rows = db.Fetch<Firearm>(sql, scope.KindName, scope.Id);
            if (rows.Count == 0) return rows;

            // Relationerna i TVÅ frågor för hela listan, inte två per vapen. En medlem har sällan
            // många vapen, men klubbfliken kan ha femtio och mönstret ska inte behöva ändras då.
            var ids = rows.Select(r => r.Id).ToList();
            var feds = db.Fetch<FirearmRelationRow>(
                $"SELECT FirearmId, Forbund AS [Value] FROM FirearmFederation WHERE FirearmId IN ({InList(ids)})");
            var discs = db.Fetch<FirearmRelationRow>(
                $"SELECT FirearmId, Discipline AS [Value] FROM FirearmDiscipline WHERE FirearmId IN ({InList(ids)})");

            foreach (var row in rows)
            {
                row.Federations = feds.Where(f => f.FirearmId == row.Id).Select(f => f.Value).OrderBy(v => v).ToList();
                row.Disciplines = discs.Where(d => d.FirearmId == row.Id)
                    .Select(d => d.Value).OrderBy(ActivityDiscipline.SortKey).ToList();
            }

            return rows;
        }

        /// <summary>Ett vapen, eller null. Utan skyddade uppgifter.</summary>
        public Firearm? GetById(int firearmId)
        {
            if (firearmId <= 0) return null;

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;
            var row = db.FirstOrDefault<Firearm>("SELECT * FROM Firearm WHERE Id = @0", firearmId);
            if (row is null) return row;

            row.Federations = db.Fetch<string>(
                "SELECT Forbund FROM FirearmFederation WHERE FirearmId = @0 ORDER BY Forbund", firearmId);
            row.Disciplines = db.Fetch<string>(
                "SELECT Discipline FROM FirearmDiscipline WHERE FirearmId = @0", firearmId)
                .OrderBy(ActivityDiscipline.SortKey).ToList();
            return row;
        }

        /// <summary>
        /// Lämnar ut de SKYDDADE uppgifterna för ett vapen — <b>efter</b> behörighetsprövning, och
        /// registrerar läsningen.
        ///
        /// <para>Returnerar <c>(null, felmeddelande)</c> vid nekad behörighet. Returnerar
        /// <c>(tomt objekt, null)</c> när vapnet aldrig haft några skyddade uppgifter — det är ett
        /// tomt formulär, inte ett fel.</para>
        /// </summary>
        public async Task<(FirearmDetails? Details, string? Error)> RevealDetailsAsync(
            int firearmId, string? note = null)
        {
            var row = GetById(firearmId);
            if (row is null) return (null, "Vapnet hittades inte.");

            var access = row.Scope.Kind == FirearmOwnerKind.Club
                ? await _auth.ResolveClubWeaponAccessAsync(row.ScopeId)
                : await _auth.ResolveReadAccessAsync(row.ScopeId);

            if (!access.Allowed)
                return (null, "Du har inte behörighet att läsa det här vapnets uppgifter.");

            FirearmDetails details;
            try
            {
                details = _protector.UnprotectObject<FirearmDetails>(row.Scope, row.Id, row.EncryptedDetails)
                          ?? new FirearmDetails();
            }
            catch (FirearmCryptoException ex)
            {
                // ⚠️ Aldrig ett tomt objekt här. Ett fält som tyst är tomt läses som att medlemmen
                // inte fyllt i något, medan sanningen kan vara att nyckeln är borta.
                _logger.LogError(ex, "Kunde inte avkryptera vapen {FirearmId}.", firearmId);
                return (null, "Uppgifterna kunde inte läsas. " + ex.Message);
            }

            // Loggas EFTER att klartexten faktiskt tagits fram, men FÖRE den returneras.
            _accessLog.Record(
                readerMemberId: access.ReaderMemberId,
                reason: access.Reason ?? FirearmAccessReason.Owner,
                subjectMemberId: row.Scope.Kind == FirearmOwnerKind.Member ? row.ScopeId : null,
                firearmId: row.Id,
                readerClubId: access.ClubId,
                note: note);

            return (details, null);
        }

        // ── Skrivning ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Skapar ett vapen.
        ///
        /// <para><b>⚠️ TVÅSTEGS, och det är inte valfritt.</b> AAD:n binder chiffret till radens id,
        /// så raden infogas först med sina klartextkolumner, id:t läses, och först därefter krypteras
        /// uppgifterna in. Krypterades de före insättningen vore AAD:n bunden till id 0 och raden
        /// kunde aldrig läsas igen.</para>
        /// </summary>
        public (int FirearmId, string? Error) Create(
            FirearmScope scope, FirearmWriteRequest request)
        {
            if (!scope.IsValid) return (0, "Ogiltig ägare.");

            var validation = Validate(request);
            if (validation is not null) return (0, validation);

            // ⚠️ Valvet skapas FÖRE raden. Går nyckelhanteringen fel — saknad rotnyckel — ska
            // ingenting ha skrivits; annars står en vapenrad kvar vars uppgifter aldrig kan skrivas.
            try
            {
                if (request.Details is { IsEmpty: false })
                    _vault.GetOrCreateDataKey(scope);
            }
            catch (FirearmCryptoException ex)
            {
                _logger.LogError(ex, "Kunde inte skapa valv för {Scope}.", scope);
                return (0, "Vapenregistrets kryptering är inte tillgänglig. " + ex.Message);
            }

            int newId;
            using (var uow = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = uow.Database;
                var row = new Firearm
                {
                    ScopeKind = scope.KindName,
                    ScopeId = scope.Id,
                    Alias = request.Alias!.Trim(),
                    WeaponClass = Blank(request.WeaponClass),
                    Vapentyp = Blank(request.Vapentyp),
                    AnnanVapentyp = Blank(request.AnnanVapentyp),
                    AcquisitionStatus = FirearmAcquisitionStatus.IsValid(request.AcquisitionStatus)
                        ? request.AcquisitionStatus!.Trim()
                        : FirearmAcquisitionStatus.Innehas,
                    LicenseExpiresOn = request.LicenseExpiresOn,
                    ClubWeaponNumber = scope.Kind == FirearmOwnerKind.Club ? request.ClubWeaponNumber : null,
                    IsLoanable = scope.Kind == FirearmOwnerKind.Club && request.IsLoanable,
                    Status = scope.Kind == FirearmOwnerKind.Club ? Blank(request.Status) : null,
                    IsActive = true,
                    SortOrder = request.SortOrder,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };

                db.Insert(row);
                newId = row.Id;

                if (newId <= 0)
                    return (0, "Vapnet kunde inte sparas (inget id tilldelades).");

                WriteRelations(db, newId, request);
            }

            // Steg två: nu finns id:t, så uppgifterna kan bindas till raden.
            var protectError = ProtectInto(scope, newId, request.Details);
            if (protectError is not null)
            {
                // Raden finns men bär inga uppgifter. Att radera den här vore värre: medlemmen har
                // fyllt i ett formulär, och klartextkolumnerna är riktiga. Felet rapporteras i stället.
                _logger.LogError("Vapen {FirearmId} skapades men uppgifterna kunde inte skyddas: {Error}",
                    newId, protectError);
                return (newId, "Vapnet sparades, men de skyddade uppgifterna kunde inte krypteras: " + protectError);
            }

            return (newId, null);
        }

        /// <summary>Uppdaterar ett vapen. Id:t finns redan, så krypteringen kan ske direkt.</summary>
        public string? Update(int firearmId, FirearmWriteRequest request)
        {
            var existing = GetById(firearmId);
            if (existing is null) return "Vapnet hittades inte.";

            var validation = Validate(request);
            if (validation is not null) return validation;

            var scope = existing.Scope;

            using (var uow = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = uow.Database;
                db.Execute(
                    @"UPDATE Firearm SET Alias = @0, WeaponClass = @1, Vapentyp = @2, AnnanVapentyp = @3,
                             AcquisitionStatus = @4, LicenseExpiresOn = @5, ClubWeaponNumber = @6,
                             IsLoanable = @7, Status = @8, SortOrder = @9, UpdatedAt = @10
                       WHERE Id = @11",
                    request.Alias!.Trim(),
                    Blank(request.WeaponClass),
                    Blank(request.Vapentyp),
                    Blank(request.AnnanVapentyp),
                    FirearmAcquisitionStatus.IsValid(request.AcquisitionStatus)
                        ? request.AcquisitionStatus!.Trim() : existing.AcquisitionStatus,
                    request.LicenseExpiresOn,
                    scope.Kind == FirearmOwnerKind.Club ? request.ClubWeaponNumber : null,
                    scope.Kind == FirearmOwnerKind.Club && request.IsLoanable,
                    scope.Kind == FirearmOwnerKind.Club ? Blank(request.Status) : null,
                    request.SortOrder,
                    DateTime.Now,
                    firearmId);

                // Relationerna skrivs om helt. En diff hade behövt hålla ordning på borttagningar
                // också, och mängderna är små nog att ersättning är både enklare och säkrare.
                db.Execute("DELETE FROM FirearmFederation WHERE FirearmId = @0", firearmId);
                db.Execute("DELETE FROM FirearmDiscipline WHERE FirearmId = @0", firearmId);
                WriteRelations(db, firearmId, request);
            }

            // ⚠️ `Details = null` betyder "rör inte de skyddade uppgifterna" — det är vad en
            // sparning från ett formulär där fälten var maskerade skickar. Ett tomt OBJEKT betyder
            // däremot "töm dem". Skillnaden är hela skälet fältet är nullbart: utan den hade varje
            // sparning av alias eller förfallodatum raderat fabrikat och kaliber.
            if (request.Details is not null)
            {
                var protectError = ProtectInto(scope, firearmId, request.Details);
                if (protectError is not null) return protectError;
            }

            return null;
        }

        /// <summary>
        /// Gömmer ett vapen (<c>IsActive = 0</c>). <b>Raderar aldrig</b> — gamla intyg refererar
        /// raden, och "antal vapen sedan tidigare" räknar historik.
        /// </summary>
        public string? Deactivate(int firearmId)
        {
            if (firearmId <= 0) return "Ogiltigt vapen.";

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var affected = uow.Database.Execute(
                "UPDATE Firearm SET IsActive = 0, UpdatedAt = @0 WHERE Id = @1", DateTime.Now, firearmId);
            return affected > 0 ? null : "Vapnet hittades inte.";
        }

        // ── Räkningen föreningsintyget behöver ───────────────────────────────────────────────────

        /// <summary>
        /// Antal vapen medlemmen INNEHAR inom ett förbund — blankettens "antal vapen sedan tidigare".
        ///
        /// <para><b>Ett <c>COUNT(*)</c>, inte en avkryptera-allt-loop.</b> Det är hela skälet
        /// <c>FirearmFederation</c> ligger i klartext: antalet är inte det känsliga, vapnets
        /// identitet är det.</para>
        ///
        /// <para><b>⚠️ Bara <c>Innehas</c> räknas.</b> Ett planerat vapen är just det man söker
        /// licens för, och skulle det räknas som "sedan tidigare" vore siffran ett för hög på varje
        /// intyg. Avvecklade vapen räknas inte heller — de innehas inte.</para>
        ///
        /// <para><paramref name="excludeFirearmId"/> tar bort det sökta vapnet ur räkningen, för
        /// fallet att det redan ligger i registret (en förnyelse).</para>
        /// </summary>
        public int CountHeldInFederation(int memberId, string forbund, int? excludeFirearmId = null)
        {
            if (memberId <= 0 || string.IsNullOrWhiteSpace(forbund)) return 0;

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.ExecuteScalar<int>(
                @"SELECT COUNT(*)
                    FROM Firearm f
                    JOIN FirearmFederation ff ON ff.FirearmId = f.Id
                   WHERE f.ScopeKind = @0 AND f.ScopeId = @1
                     AND f.IsActive = 1 AND f.AcquisitionStatus = @2
                     AND ff.Forbund = @3
                     AND (@4 IS NULL OR f.Id <> @4)",
                FirearmOwnerKind.Member.ToString(), memberId,
                FirearmAcquisitionStatus.Innehas, forbund.Trim(), excludeFirearmId);
        }

        // ── Etikettens kod ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Vapnets etikettkod, myntad om den saknas. Null bara om vapnet inte finns.
        ///
        /// <para><b>⚠️ LAT, inte satt vid skapandet.</b> Registret bar redan vapen när koden kom
        /// till, och de får sin kod första gången någon begär en etikett. Ett vapen som ingen
        /// skriver ut en etikett för behöver ingen kod — och en kod som aldrig tryckts är bara en
        /// hemlighet till att låna ut ett vapen, utan nytta.</para>
        ///
        /// <para><b>⚠️ Koden skrivs EN gång och skrivs aldrig över.</b> <c>WHERE LabelCode IS
        /// NULL</c> i uppdateringen är inte en optimering utan spärren: två samtidiga
        /// etikettutskrifter för samma vapen får inte ge två koder, för då blir den etikett som
        /// redan hunnit ut ur skrivaren tyst obrukbar. Vinner den andra tråden läses hennes kod i
        /// stället.</para>
        /// </summary>
        public string? EnsureLabelCode(int firearmId)
        {
            if (firearmId <= 0) return null;

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var current = db.FirstOrDefault<Firearm>("SELECT * FROM Firearm WHERE Id = @0", firearmId);
            if (current is null) return null;
            if (!string.IsNullOrWhiteSpace(current.LabelCode)) return current.LabelCode;

            // Fyra försök. En krock i ett 32^10-rum är i praktiken omöjlig, så loopen finns för det
            // unika indexet: skulle det ändå slå ska svaret bli en kod, inte ett undantag mitt i en
            // utskrift.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var candidate = FirearmLabelCode.Next();
                try
                {
                    var changed = db.Execute(
                        "UPDATE Firearm SET LabelCode = @0, UpdatedAt = @1 WHERE Id = @2 AND LabelCode IS NULL",
                        candidate, DateTime.Now, firearmId);

                    if (changed > 0) return candidate;

                    // Någon annan hann först — hennes kod är den som gäller.
                    var settled = db.ExecuteScalar<string?>(
                        "SELECT LabelCode FROM Firearm WHERE Id = @0", firearmId);
                    if (!string.IsNullOrWhiteSpace(settled)) return settled;

                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Etikettkoden för vapen {FirearmId} krockade (försök {Attempt}).",
                        firearmId, attempt + 1);
                }
            }

            _logger.LogError("Kunde inte mynta en etikettkod för vapen {FirearmId}.", firearmId);
            return null;
        }

        /// <summary>
        /// Vapnet bakom en skannad etikettkod, eller 0.
        ///
        /// <para><b>⚠️ Ingen behörighet prövas här</b> — metoden svarar bara på vilket vapen koden
        /// pekar ut. Vem som får göra något med det avgörs av <c>FirearmBookingService.ResolveScan</c>,
        /// och den kontrollen får aldrig flyttas hit: en kod som slås upp är inte en kod som ger
        /// rätt till något.</para>
        ///
        /// <para>Ett vapen som är avaktiverat slås ändå upp, med flit. Skytten som står med
        /// telefonen ska få veta att vapnet inte kan lånas — inte att koden är ogiltig, vilket hen
        /// läser som att etiketten är trasig.</para>
        /// </summary>
        public int FindIdByLabelCode(string? code)
        {
            var normalized = FirearmLabelCode.Normalize(code);
            if (normalized is null) return 0;

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.ExecuteScalar<int?>(
                "SELECT Id FROM Firearm WHERE LabelCode = @0", normalized) ?? 0;
        }

        /// <summary>
        /// Hur många lånevapen klubben har att fördela.
        ///
        /// <para><b>⚠️ Service och utgallrat räknas INTE med.</b> De kan inte lånas ut oavsett
        /// kalender, så att räkna dem vore att lova en kapacitet som inte finns — och det löftet
        /// skulle brytas i valvet, framför en nybörjare.</para>
        /// </summary>
        public int CountLoanable(int clubId)
        {
            if (clubId <= 0) return 0;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.ExecuteScalar<int>(
                    @"SELECT COUNT(*) FROM Firearm
                       WHERE ScopeKind = @0 AND ScopeId = @1
                         AND IsActive = 1 AND IsLoanable = 1
                         AND (Status IS NULL OR Status NOT IN (@2, @3))",
                    FirearmOwnerKind.Club.ToString(), clubId,
                    FirearmStatus.Service, FirearmStatus.Utgallrat);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte räkna lånevapen i klubb {ClubId}.", clubId);
                return 0;
            }
        }

        // ── Internt ──────────────────────────────────────────────────────────────────────────────

        private string? ProtectInto(FirearmScope scope, int firearmId, FirearmDetails? details)
        {
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);

                // Tomma uppgifter lagras som NULL, inte som ett krypterat tomt objekt. Skillnaden
                // syns i `HasProtectedDetails`, som avgör om kortet visar en avmaskeringsknapp alls.
                if (details is null || details.IsEmpty)
                {
                    uow.Database.Execute(
                        "UPDATE Firearm SET EncryptedDetails = NULL, UpdatedAt = @0 WHERE Id = @1",
                        DateTime.Now, firearmId);
                    return null;
                }

                var payload = _protector.ProtectObject(scope, firearmId, details);
                uow.Database.Execute(
                    "UPDATE Firearm SET EncryptedDetails = @0, UpdatedAt = @1 WHERE Id = @2",
                    payload, DateTime.Now, firearmId);
                return null;
            }
            catch (FirearmCryptoException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte skriva skyddade uppgifter för vapen {FirearmId}.", firearmId);
                return "Uppgifterna kunde inte sparas.";
            }
        }

        private static void WriteRelations(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int firearmId, FirearmWriteRequest request)
        {
            // ⚠️ Värdena valideras mot konstanterna, aldrig sparas som fritext. Ett förbund som inte
            // finns i ForeningsintygDocument.AllaForbund kan aldrig matcha intygets förbundsruta, och en
            // gren utanför ActivityDiscipline kan aldrig kopplas till aktivitetssammanställningen.
            foreach (var forbund in (request.Federations ?? new List<string>())
                     .Select(f => (f ?? "").Trim())
                     .Where(f => ForeningsintygDocument.AllaForbund.Contains(f, StringComparer.Ordinal))
                     .Distinct(StringComparer.Ordinal))
            {
                db.Execute("INSERT INTO FirearmFederation (FirearmId, Forbund) VALUES (@0, @1)",
                    firearmId, forbund);
            }

            foreach (var discipline in (request.Disciplines ?? new List<string>())
                     .Select(ActivityDiscipline.Canonical)
                     .Where(d => d.Length > 0)
                     .Distinct(StringComparer.Ordinal))
            {
                db.Execute("INSERT INTO FirearmDiscipline (FirearmId, Discipline) VALUES (@0, @1)",
                    firearmId, discipline);
            }
        }

        /// <summary>
        /// Aliaset är det ENDA obligatoriska fältet. Ett register man inte kan börja fylla i utan
        /// att ha licensbeviset framme är ett register ingen börjar fylla i.
        /// </summary>
        private static string? Validate(FirearmWriteRequest request)
        {
            if (request is null) return "Ogiltig begäran.";
            if (string.IsNullOrWhiteSpace(request.Alias)) return "Vapnet måste ha ett namn (alias).";
            if (request.Alias.Trim().Length > 80) return "Namnet är för långt (högst 80 tecken).";

            // ⚠️ Går via FirearmWeaponGroups, inte Enum.TryParse<WeaponClass>. Den senare avvisar
            // "M2" — och magnumklasserna M1–M9 är olika VAPEN (SA/DA revolver 41-44, 357, fri 9mm),
            // inte kompetensnivåer, så gruppkoden "M" identifierar inget magnumvapen.
            if (!FirearmWeaponGroups.IsValid(request.WeaponClass))
                return $"Okänd vapengrupp '{request.WeaponClass}'.";

            if (!string.IsNullOrWhiteSpace(request.Vapentyp)
                && !ForeningsintygDocument.AllaVapentyper.Contains(request.Vapentyp.Trim(), StringComparer.Ordinal))
                return $"Okänd vapentyp '{request.Vapentyp}'.";

            // Ett förfallodatum långt i förflutet är nästan alltid en felskrivning, men det är
            // medlemmens uppgift att äga — vi vägrar bara det uppenbart orimliga.
            if (request.LicenseExpiresOn is { } d && (d.Year < 1950 || d.Year > 2100))
                return "Förfallodatumet ser inte rimligt ut.";

            return null;
        }

        private static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string InList(List<int> ids) => string.Join(",", ids);

        private class FirearmRelationRow
        {
            public int FirearmId { get; set; }
            public string Value { get; set; } = "";
        }
    }

    /// <summary>
    /// Det som får skrivas till ett vapen.
    ///
    /// <para><b>⚠️ <see cref="Details"/> är NULLBART och skillnaden är bärande:</b> <c>null</c>
    /// betyder "rör inte de skyddade uppgifterna" (det ett formulär med maskerade fält skickar),
    /// medan ett tomt objekt betyder "töm dem". Utan skillnaden hade varje sparning av ett alias
    /// eller ett förfallodatum raderat fabrikat och kaliber.</para>
    /// </summary>
    public class FirearmWriteRequest
    {
        public string? Alias { get; set; }
        public string? WeaponClass { get; set; }
        public string? Vapentyp { get; set; }
        public string? AnnanVapentyp { get; set; }
        public string? AcquisitionStatus { get; set; }
        public DateTime? LicenseExpiresOn { get; set; }
        public int SortOrder { get; set; }

        public List<string>? Federations { get; set; }
        public List<string>? Disciplines { get; set; }

        // Klubbvapen — ignoreras för ett medlemsvapen.
        public int? ClubWeaponNumber { get; set; }
        public bool IsLoanable { get; set; }
        public string? Status { get; set; }

        public FirearmDetails? Details { get; set; }
    }
}
