using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Vilket slags ärende ett svar hör till.
    ///
    /// <para><b>⚠️ NYCKELN LAGRAS SOM TEXT I DATABASEN.</b> Byt aldrig en befintlig sträng — då
    /// tappar de svar som redan ligger där sin koppling till ärendet, tyst. Lägg till nya i
    /// stället.</para>
    /// </summary>
    public static class MailThreadKind
    {
        /// <summary>En föreningsintygsförfrågan (<c>ForeningsintygRequest.Id</c>).</summary>
        public const string Foreningsintyg = "Foreningsintyg";

        public static readonly string[] All = { Foreningsintyg };

        public static bool IsValid(string? v) => All.Contains((v ?? "").Trim(), StringComparer.Ordinal);
    }

    [TableName("MailReply")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MailReply
    {
        public int Id { get; set; }

        /// <summary>Se <see cref="MailThreadKind"/>.</summary>
        public string ThreadKind { get; set; } = "";

        /// <summary>Ärendets id inom sin egen tabell. Ingen FK — se migreringen.</summary>
        public int ThreadRefId { get; set; }

        /// <summary>Klubben ärendet hör till, för uppslag och behörighet.</summary>
        public int ClubId { get; set; }

        /// <summary>Medlemmen som svarade. Kommer ur den signerade länken, inte ur en session.</summary>
        public int MemberId { get; set; }

        public string Body { get; set; } = "";

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Medlemmens svar på ett mejl, lagrat i ärendet i stället för i en inkorg.
    ///
    /// <para><b>⚠️ FINNS FÖR ATT SVAREN INTE HADE NÅGON PLATS.</b> Fram till nu kunde medlemmen bara
    /// svara på mejlet — och det mejlet kom från <c>admin@pistol.nu</c>, så svaret nådde aldrig
    /// klubben. Nu bär mejlet en signerad länk till ett svarsfält, och svaret hamnar på förfrågan.</para>
    ///
    /// <para><b>⚠️ MEDVETET GENERISK.</b> Föreningsintyget är först, men certifieringskön,
    /// banläggargodkännandet och bemanningsförfrågan har exakt samma form (ett ärende med ett öppet
    /// tillstånd som väntar på motparten). Att bygga det Föreningsintyg-specifikt hade betytt att
    /// bygga det fyra gånger.</para>
    ///
    /// <para><b>⚠️ TILLSTÅNDET GENERALISERAS INTE.</b> Tjänsten lagrar svaret och svarar på "finns
    /// det ett svar nyare än X". Vad det BETYDER för ärendet — vilken status det får, vem som
    /// aviseras — avgör varje yta själv. Ett gemensamt "ärendet har svar"-tillstånd över olika
    /// tillståndsmaskiner hade tvingat in fel semantik i den ena av dem.</para>
    /// </summary>
    public class MailReplyService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<MailReplyService> _logger;

        /// <summary>Taket i databasen är NVARCHAR(MAX), men ett svar är ett meddelande, inte en bok.</summary>
        public const int MaxBodyLength = 4000;

        public MailReplyService(IScopeProvider scopeProvider, ILogger<MailReplyService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>
        /// Utfallet av ett sparförsök.
        ///
        /// <para><b>⚠️ ETT AVVISAT SVAR OCH ETT TAPPAT SVAR ÄR OLIKA SAKER.</b> "Skriv ditt svar
        /// först" är medlemmens att åtgärda och ska inte störa någon annan; ett sparfel är VÅRT och
        /// måste nå klubben, annars står medlemmen och trycker på en knapp som aldrig kommer att
        /// fungera medan klubben tror att hen tiger. Anroparen kan inte skilja dem åt på
        /// felsträngen — därför <see cref="SaveFailed"/>.</para>
        /// </summary>
        public sealed record AddResult(int Id, string? Error, bool SaveFailed)
        {
            /// <summary>Sparat, eller en slängd dubbelpostning — inget mer behöver göras.</summary>
            public bool Ok => Error is null;
        }

        /// <summary>
        /// Sparar ett svar.
        ///
        /// <para><b>⚠️ DUBBELPOSTNING SLÄNGS.</b> Samma medlem, samma ärende, samma text inom en
        /// minut är en andra tryckning på knappen eller en omladdning — inte ett andra svar. Utan
        /// spärren ser klubben två identiska svar och undrar vilket som gäller.</para>
        /// </summary>
        public AddResult Add(string threadKind, int threadRefId, int clubId, int memberId, string body)
        {
            if (!MailThreadKind.IsValid(threadKind)) return new AddResult(0, "Okänt ärendeslag.", false);
            if (threadRefId <= 0 || memberId <= 0) return new AddResult(0, "Ogiltigt ärende.", false);

            var text = (body ?? "").Trim();
            if (text.Length == 0) return new AddResult(0, "Skriv ditt svar först.", false);
            if (text.Length > MaxBodyLength)
                return new AddResult(0, $"Svaret är för långt (max {MaxBodyLength} tecken).", false);

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);

                var dupe = uow.Database.ExecuteScalar<int>(
                    @"SELECT COUNT(*) FROM MailReply
                       WHERE ThreadKind = @0 AND ThreadRefId = @1 AND MemberId = @2
                         AND Body = @3 AND CreatedAt > DATEADD(second, -60, GETDATE())",
                    threadKind, threadRefId, memberId, text);
                if (dupe > 0)
                {
                    _logger.LogInformation(
                        "MailReply: dubbelpostning slängd för {Kind} {RefId} av medlem {MemberId}.",
                        threadKind, threadRefId, memberId);
                    return new AddResult(0, null, false);
                }

                var row = new MailReply
                {
                    ThreadKind = threadKind,
                    ThreadRefId = threadRefId,
                    ClubId = clubId,
                    MemberId = memberId,
                    Body = text,
                    CreatedAt = DateTime.Now,
                };
                uow.Database.Insert(row);
                return new AddResult(row.Id, null, false);
            }
            catch (Exception ex)
            {
                // ⚠️ Svaret är medlemmens ENDA väg in. Ett tappat svar utan felmeddelande hade
                // låtit hen tro att klubben fått det.
                _logger.LogError(ex, "MailReply: kunde inte spara svar på {Kind} {RefId}.",
                    threadKind, threadRefId);

                // ⚠️ SÄG INTE "försök igen" SOM ENDA RÅD. Det vanligaste sparfelet är en okörd
                // migrering, och då kan medlemmen trycka hur många gånger som helst utan att något
                // ändras — ett råd som inte kan hjälpa är värre än inget, för det flyttar skulden
                // till den som inte kan göra något. Klubben aviseras separat (se anroparen), och
                // det ska texten säga så hen vet att någon fått veta.
                return new AddResult(0,
                    "Ditt svar kunde tyvärr inte sparas — det är ett fel hos oss, inte hos dig. "
                    + "Klubben får ett meddelande om att du försökt. Du kan gärna prova igen om en "
                    + "stund, eller höra av dig till klubben direkt.",
                    true);
            }
        }

        /// <summary>
        /// Finns tabellen? Används av startkontrollen.
        ///
        /// <para><b>⚠️ Frågar SCHEMAT, inte innehållet.</b> En tom tabell och en saknad tabell är
        /// helt olika tillstånd: det första är en normal ny miljö, det andra är en okörd migrering
        /// som gör varje svarslänk i varje utskickat mejl till en återvändsgränd.</para>
        /// </summary>
        public bool TableExists()
        {
            // ⚠️ INGEN catch här, med flit. Kan vi inte fråga vet vi inte, och "vet inte" får varken
            // rapporteras som "finns" eller som "saknas". En tidigare version svalde felet och
            // svarade false, så en delad anslutning vid uppstart (se IsolatedBackgroundService)
            // loggades som det falska larmet "SVARSLAGRET ÄR TRASIGT" på Fatal-nivå — och
            // disponeringsfelet som visade den verkliga orsaken försvann på Debug-nivå.
            // Anroparen (startkontrollen) fångar undantaget och loggar att kontrollen inte kunde
            // genomföras.
            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.ExecuteScalar<int>(
                "SELECT CASE WHEN OBJECT_ID('dbo.MailReply','U') IS NULL THEN 0 ELSE 1 END") == 1;
        }

        /// <summary>Alla svar på ett ärende, äldst först.</summary>
        public List<MailReply> GetForThread(string threadKind, int threadRefId)
        {
            if (!MailThreadKind.IsValid(threadKind) || threadRefId <= 0) return new List<MailReply>();

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<MailReply>(
                    @"SELECT * FROM MailReply
                       WHERE ThreadKind = @0 AND ThreadRefId = @1
                       ORDER BY CreatedAt ASC, Id ASC",
                    threadKind, threadRefId);
            }
            catch (Exception ex)
            {
                // Saknad tabell (omigrerad miljö) → tom lista. Ytan ska rendera utan svar.
                _logger.LogDebug(ex, "MailReply: kunde inte läsa svar på {Kind} {RefId}.",
                    threadKind, threadRefId);
                return new List<MailReply>();
            }
        }

        /// <summary>
        /// Senaste svaret per ärende för en hel klubb, i ETT anrop.
        ///
        /// <para><b>⚠️ EN FRÅGA, INTE EN PER RAD.</b> Inkorgen renderar alla öppna ärenden på en
        /// gång. Ett uppslag per rad hade blivit N+1 på precis den sida som redan varit långsam en
        /// gång (fakturaadmin, 12 s → 33 ms).</para>
        /// </summary>
        public Dictionary<int, MailReply> LatestPerThreadForClub(string threadKind, int clubId)
        {
            var map = new Dictionary<int, MailReply>();
            if (!MailThreadKind.IsValid(threadKind) || clubId <= 0) return map;

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<MailReply>(
                    @"SELECT r.* FROM MailReply r
                       WHERE r.ThreadKind = @0 AND r.ClubId = @1
                         AND r.Id = (SELECT MAX(r2.Id) FROM MailReply r2
                                      WHERE r2.ThreadKind = r.ThreadKind AND r2.ThreadRefId = r.ThreadRefId)",
                    threadKind, clubId);
                foreach (var r in rows) map[r.ThreadRefId] = r;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MailReply: kunde inte läsa senaste svar för klubb {ClubId}.", clubId);
            }
            return map;
        }
    }
}
