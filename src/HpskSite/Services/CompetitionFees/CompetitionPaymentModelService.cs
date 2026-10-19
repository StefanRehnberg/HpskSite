using System.Collections.Concurrent;
using HpskSite.Models.CompetitionFees;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.CompetitionFees
{
    /// <summary>
    /// <b>EN läsväg</b> för frågan "vilken pengamodell har den här tävlingen?".
    ///
    /// <para><b>⚠️⚠️ Ingen yta får gissa ur förekomsten av fakturor.</b> Varje ställe som i dag
    /// anropar <c>PaymentService</c> frågar här först. Två tolkningar av samma fråga är exakt hur en
    /// tävling hamnar med avgifter i båda systemen.</para>
    ///
    /// <para><b>Beslutet:</b> en rad i <c>CompetitionPaymentModel</c> gäller för alltid. Saknas raden
    /// avgörs det av om tävlingen redan har gamla fakturor — i så fall skrivs <c>legacy</c> (samma
    /// regel som deploymigreringen, så en missad migrering läker sig själv). Annars är tävlingen
    /// NY, och första anmälan skriver <c>ledger</c> (<see cref="DecideAtFirstRegistration"/>).</para>
    ///
    /// <para><b>⚠️ Egen anslutning, aldrig inuti någon annans scope.</b> Anropas efter att anmälan
    /// sparats, aldrig inifrån ett <c>IScope</c> — ett andra anslutningsgrepp där låser (se
    /// <c>LedgerMembershipFeeBridge</c>, 2026-09-22).</para>
    /// </summary>
    public class CompetitionPaymentModelService
    {
        // Ett beslut ändras aldrig (trigger i databasen), så det kan cachas för processens livstid.
        // Obeslutade tävlingar cachas INTE — de ska kunna beslutas.
        private static readonly ConcurrentDictionary<int, string> Decided = new();

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<CompetitionPaymentModelService> _logger;

        public CompetitionPaymentModelService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<CompetitionPaymentModelService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>Den nya modellen — avgifter i liggaren.</summary>
        public bool IsLedger(int competitionId) => Get(competitionId) == CompetitionPaymentModels.Ledger;

        /// <summary>Den gamla fakturamodellen.</summary>
        public bool IsLegacy(int competitionId) => Get(competitionId) == CompetitionPaymentModels.Legacy;

        public string Get(int competitionId)
        {
            if (competitionId <= 0) return CompetitionPaymentModels.Ledger;
            if (Decided.TryGetValue(competitionId, out var cached)) return cached;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var row = db.SingleOrDefault<CompetitionPaymentModelRow>(
                    "SELECT * FROM dbo.CompetitionPaymentModel WHERE CompetitionId = @0", competitionId);
                if (row != null)
                {
                    Decided[competitionId] = row.Model;
                    return row.Model;
                }

                // ⚠️ Självläkning: har tävlingen gamla fakturor men ingen rad, missade migreringen
                // den. Den är då legacy — och det skrivs ned, så svaret aldrig kan glida.
                if (CompetitionsWithLegacyInvoices(db).Contains(competitionId))
                    return Write(db, competitionId, CompetitionPaymentModels.Legacy, "fakturor-fanns");

                return CompetitionPaymentModels.Ledger;
            }
            catch (Exception ex)
            {
                // ⚠️⚠️ Kan vi inte läsa beslutet GISSAR vi inte (Stefan 2026-10-12). Förut svarade vi
                // "legacy", vilket fick PaymentService att skapa gamla fakturanoder för en ny tävling
                // så snart läsningen föll — tyst, och för varje tävling på en gång om tabellen saknades.
                // "unknown" cachas inte och skrivs aldrig: varje skapande väg vägrar med
                // CompetitionPaymentModels.UnknownMessage, och nästa läsning försöker igen.
                _logger.LogError(ex,
                    "Kunde inte läsa betalningsmodellen för tävling {CompetitionId}. Inga fakturor eller avgifter skapas förrän den går att läsa.",
                    competitionId);
                return CompetitionPaymentModels.Unknown;
            }
        }

        /// <summary>Beslutet gick inte att läsa — skapa ingenting.</summary>
        public bool IsUnknown(int competitionId) => Get(competitionId) == CompetitionPaymentModels.Unknown;

        /// <summary>
        /// Anropas vid anmälan. Har tävlingen ingen rad blir den <c>ledger</c> nu — för alltid.
        /// </summary>
        public string DecideAtFirstRegistration(int competitionId)
        {
            var model = Get(competitionId);
            if (Decided.ContainsKey(competitionId)) return model;
            // ⚠️ Ett oläsbart beslut får aldrig skrivas ned — då vore "unknown" tävlingens modell för alltid.
            if (model == CompetitionPaymentModels.Unknown) return model;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return Write(db, competitionId, model, "forsta-anmalan");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte skriva betalningsmodellen för tävling {CompetitionId}.", competitionId);
                return model;
            }
        }

        private string Write(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId, string model, string reason)
        {
            try
            {
                db.Execute(
                    @"INSERT INTO dbo.CompetitionPaymentModel (CompetitionId, Model, DecidedUtc, Reason)
                      SELECT @0, @1, GETUTCDATE(), @2
                       WHERE NOT EXISTS (SELECT 1 FROM dbo.CompetitionPaymentModel WHERE CompetitionId = @0)",
                    competitionId, model, reason);
            }
            catch (Exception ex)
            {
                // Förlorad kapplöpning mot primärnyckeln — raden finns nu, och den är svaret.
                _logger.LogInformation(ex, "Betalningsmodellen för {CompetitionId} skrevs samtidigt av någon annan.", competitionId);
            }

            var stored = db.ExecuteScalar<string>(
                "SELECT Model FROM dbo.CompetitionPaymentModel WHERE CompetitionId = @0", competitionId);
            var final = string.IsNullOrEmpty(stored) ? model : stored;
            Decided[competitionId] = final;
            return final;
        }

        /// <summary>
        /// Har tävlingen minst en gammal faktura? Faktura → mapp → tävling (som deploymigreringen), ELLER
        /// fakturans egen <c>competitionId</c>. Den andra vägen gör att svaret står sig när fakturamapparna
        /// flyttas till arkivet (C3-planen F1, 2026-10-13) — utan den hade en sådan tävling tyst blivit
        /// "ledger", och raden i <c>CompetitionPaymentModel</c> är oföränderlig.
        /// </summary>
        /// <para>⚠️ <b>Mängden byggs med EN fråga och cachas</b> (skalplanen, mätningen 2026-10-19). Förut frågades
        /// en tävling i taget med ett OR som prövade egenskapsdatan för varje faktura i hela sajten — två gånger per
        /// öppning av Anmälningar för en obeslutad tävling, ~56 ms styck på 20 000-kopian. Cachen är säker: en
        /// obeslutad tävling kan inte FÅ en gammal faktura (PaymentService vägrar utom i den gamla modellen), så
        /// mängden kan bara bli inaktuell för tävlingar som redan är beslutade och aldrig frågas här igen.
        /// Samma svar som den gamla frågan för alla 783 tävlingar i 20 000-kopian och alla 204 i dev.</para>
        private static HashSet<int> CompetitionsWithLegacyInvoices(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db)
        {
            var cached = _legacySet;
            if (cached != null && DateTime.UtcNow < cached.Value.Until) return cached.Value.Ids;
            var ids = db.Fetch<int>(
                @"SELECT hub.parentId FROM umbracoNode hub
                    JOIN umbracoContent pc ON pc.nodeId = hub.parentId
                    JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId AND pct.alias = 'competition'
                    JOIN umbracoNode i ON i.parentId = hub.id AND i.trashed = 0
                    JOIN umbracoContent c ON c.nodeId = i.id
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = 'registrationInvoice'
                  UNION
                  SELECT pd.intValue FROM umbracoPropertyData pd
                    JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId AND pt.alias = 'competitionId'
                    JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.[current] = 1
                    JOIN umbracoNode i ON i.id = cv.nodeId AND i.trashed = 0
                    JOIN umbracoContent c ON c.nodeId = i.id
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = 'registrationInvoice'
                   WHERE pd.intValue IS NOT NULL").ToHashSet();
            _legacySet = (ids, DateTime.UtcNow.AddMinutes(10));
            return ids;
        }

        private static (HashSet<int> Ids, DateTime Until)? _legacySet;

        /// <summary>För tester och verifiering: glöm cachen för en tävling.</summary>
        public static void Forget(int competitionId) => Decided.TryRemove(competitionId, out _);
    }
}
