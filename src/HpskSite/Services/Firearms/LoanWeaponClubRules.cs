using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Klubbens regler för lånevapen, lästa på ETT ställe.
    ///
    /// <para><b>⚠️ Bokningen och gränssnittet MÅSTE läsa samma svar.</b> Fönsterregeln låg en gång i
    /// två handskrivna kopior och hade glidit isär — listan visade ett vapen som ledigt medan
    /// bokningen vägrade samma fönster. Reglerna här är av samma slag: de avgör vad som är möjligt,
    /// och en yta som räknar själv kommer att erbjuda något servern nekar.</para>
    ///
    /// <para><b>⚠️ SAKNAD EGENSKAP = SÄKER DEFAULT, aldrig ett undantag.</b> Ingen horisont och
    /// inga externa lån är båda det försiktiga svaret, så en klubb vars doctype saknar
    /// egenskaperna får dagens beteende i stället för en trasig sida. Men <b>skrivvägen måste
    /// vägra och namnge egenskapen</b> — <c>SetValue</c> på en saknad egenskap är en tyst no-op,
    /// och switchen skulle se ut att spara och återgå vid nästa laddning.</para>
    /// </summary>
    public class LoanWeaponClubRules
    {
        /// <summary>Alias på klubbdoctypen. Namngivna, så ingen yta stavar dem för hand.</summary>
        public const string AllowExternalProperty = "lanevapenAllowExternal";
        public const string HorizonProperty = "lanevapenHorizonDays";

        /// <summary>Alias på <c>clubSimpleEvent</c>. Klubb och krets delar doctype.</summary>
        public const string EventOfferedProperty = "lanevapenOffered";

        private readonly IContentService _contentService;
        private readonly ILogger<LoanWeaponClubRules> _logger;

        public LoanWeaponClubRules(IContentService contentService, ILogger<LoanWeaponClubRules> logger)
        {
            _contentService = contentService;
            _logger = logger;
        }

        /// <summary>Klubbens inställningar, plus om egenskaperna alls finns på doctypen.</summary>
        public LoanWeaponClubSettings For(int clubId)
        {
            if (clubId <= 0) return new LoanWeaponClubSettings();

            try
            {
                var club = _contentService.GetById(clubId);
                if (club is null) return new LoanWeaponClubSettings();

                var hasAllow = club.HasProperty(AllowExternalProperty);
                var hasHorizon = club.HasProperty(HorizonProperty);

                return new LoanWeaponClubSettings
                {
                    AllowExternalPropertyExists = hasAllow,
                    HorizonPropertyExists = hasHorizon,
                    AllowExternal = hasAllow && club.GetValue<bool>(AllowExternalProperty),
                    // ⚠️ 0 betyder INGEN gräns, inte "noll dagar framåt". En klubb som aldrig rört
                    // inställningen ska inte plötsligt bara kunna boka i dag.
                    HorizonDays = hasHorizon ? Math.Max(0, club.GetValue<int>(HorizonProperty)) : 0,
                };
            }
            catch (Exception ex)
            {
                // Fail-open: en skytt som inte kan boka är ett supportärende om en trasig klubbnod,
                // medan en som slinker igenom bara blir en rad valvlistan ändå visar.
                _logger.LogDebug(ex, "Kunde inte läsa lånevapenregler för klubb {ClubId}.", clubId);
                return new LoanWeaponClubSettings();
            }
        }

        /// <summary>
        /// Erbjuder det här tillfället lånevapen?
        ///
        /// <para>Saknas egenskapen svarar den <c>false</c> och <c>PropertyExists</c> är falskt, så
        /// anroparen kan skilja "klubben har valt bort det" från "egenskapen finns inte".</para>
        /// </summary>
        public (bool Offered, bool PropertyExists) EventOffersLoanWeapons(int eventId)
        {
            if (eventId <= 0) return (false, false);
            try
            {
                var node = _contentService.GetById(eventId);
                if (node is null) return (false, false);

                var has = node.HasProperty(EventOfferedProperty);
                return (has && node.GetValue<bool>(EventOfferedProperty), has);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa lånevapenflaggan för händelse {EventId}.", eventId);
                return (false, false);
            }
        }
    }

    public class LoanWeaponClubSettings
    {
        /// <summary>
        /// Får klubbens vapen lämna banan? <b>Av som standard, med flit</b> — en klubb som slår på
        /// det har tänkt efter, och default på hade betytt att ett vapen kan lämna utan att någon
        /// beslutat att det ska vara möjligt.
        /// </summary>
        public bool AllowExternal { get; set; }

        /// <summary>Hur långt fram en medlem får boka. <b>0 = ingen gräns.</b></summary>
        public int HorizonDays { get; set; }

        public bool AllowExternalPropertyExists { get; set; }
        public bool HorizonPropertyExists { get; set; }

        /// <summary>Ligger datumet inom klubbens horisont?</summary>
        public bool WithinHorizon(DateTime from, DateTime now) =>
            HorizonDays <= 0 || from.Date <= now.Date.AddDays(HorizonDays);
    }
}
