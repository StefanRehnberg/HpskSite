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
                if (HasLegacyInvoices(db, competitionId))
                    return Write(db, competitionId, CompetitionPaymentModels.Legacy, "fakturor-fanns");

                return CompetitionPaymentModels.Ledger;
            }
            catch (Exception ex)
            {
                // ⚠️ Kan vi inte läsa beslutet är det SÄKRA svaret den gamla modellen för en tävling
                // som har fakturor, och annars den nya. Vi vet inte vilket — logga högt och svara
                // legacy: den vägen skapar inga liggarrader som sedan måste redas ut.
                _logger.LogError(ex, "Kunde inte läsa betalningsmodellen för tävling {CompetitionId}.", competitionId);
                return CompetitionPaymentModels.Legacy;
            }
        }

        /// <summary>
        /// Anropas vid anmälan. Har tävlingen ingen rad blir den <c>ledger</c> nu — för alltid.
        /// </summary>
        public string DecideAtFirstRegistration(int competitionId)
        {
            var model = Get(competitionId);
            if (Decided.ContainsKey(competitionId)) return model;

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
        /// Har tävlingen minst en gammal faktura? Samma trädregel som deploymigreringen:
        /// faktura → hubb → tävling.
        /// </summary>
        private static bool HasLegacyInvoices(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId)
            => db.ExecuteScalar<int>(
                @"SELECT COUNT(1)
                    FROM umbracoNode i
                    JOIN umbracoContent c ON c.nodeId = i.id
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                    JOIN umbracoNode hub ON hub.id = i.parentId
                   WHERE ct.alias = 'registrationInvoice' AND i.trashed = 0 AND hub.parentId = @0",
                competitionId) > 0;

        /// <summary>För tester och verifiering: glöm cachen för en tävling.</summary>
        public static void Forget(int competitionId) => Decided.TryRemove(competitionId, out _);
    }
}
