using HpskSite.Models.Firearms;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Tillgängligheten för klubbens lånevapen i ett bestämt tidsfönster.
    ///
    /// <para><b>⚠️ Tillgänglighet finns bara FÖR ETT FÖNSTER, aldrig "i allmänhet".</b> Ett vapen som
    /// är ledigt idag kan vara bokat i morgon, så en endpoint som svarade "ledigt" utan att veta när
    /// vore det enda som är säkert fel. Därför är <c>from</c>/<c>to</c> obligatoriska.</para>
    ///
    /// <para>Egen controller och inte en metod på <c>LoanWeaponController</c>: den senare är en routad
    /// sida (<c>/lanevapen</c>), medan det här är ett AJAX-anrop som ska ligga under
    /// <c>/umbraco/surface/</c> som resten av kodbasens anrop.</para>
    /// </summary>
    public class LoanWeaponApiController : SurfaceController
    {
        private readonly FirearmService _firearms;
        private readonly FirearmBookingService _bookings;
        private readonly MemberClubService _memberClubs;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ILogger<LoanWeaponApiController> _logger;

        public LoanWeaponApiController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FirearmService firearms,
            FirearmBookingService bookings,
            MemberClubService memberClubs,
            IMemberManager memberManager,
            IMemberService memberService,
            ILogger<LoanWeaponApiController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _firearms = firearms;
            _bookings = bookings;
            _memberClubs = memberClubs;
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAvailability(int clubId, string? from, string? to)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            var member = _memberService.GetByEmail(current.Email);
            var memberId = member?.Id ?? 0;
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            // ⚠️ Medlemskapet är grinden — en klubbs lånevapen är för klubbens egna medlemmar.
            if (!_memberClubs.GetAllClubIds(member).Contains(clubId))
                return Json(new { success = false, message = "Du kan bara se lånevapen i en klubb du är medlem i." });

            if (!TryWindow(from, to, out var winFrom, out var winTo, out var label))
                return Json(new { success = false, message = "Ange ett giltigt datum." });

            try
            {
                var weapons = _firearms.GetForScope(FirearmScope.Club(clubId))
                    .Where(f => f.IsLoanable)
                    .OrderBy(f => f.ClubWeaponNumber ?? int.MaxValue)
                    .ThenBy(f => f.Alias, StringComparer.Ordinal)
                    .ToList();

                // EN fråga för hela klubben, inte en krockkontroll per vapen.
                var booked = _bookings.BookedFirearmIds(clubId, winFrom, winTo);

                // Vem som bokat spelar roll för TEXTEN: "bokat av dig" är ett annat svar än "bokat",
                // och utan skillnaden ser medlemmen sin egen bokning som ett hinder.
                var mine = _bookings.GetForMember(memberId)
                    .Where(b => b.IsActive && !(b.ToTime <= winFrom || b.FromTime >= winTo))
                    .Select(b => b.FirearmId)
                    .ToHashSet();

                return Json(new
                {
                    success = true,
                    windowLabel = label,
                    weapons = weapons.Select(f =>
                    {
                        // Service och utgallrat blockerar oavsett kalender — det är ett fysiskt läge.
                        var blockedByStatus = f.Status is FirearmStatus.Service or FirearmStatus.Utgallrat;
                        var isBooked = booked.Contains(f.Id);
                        return new
                        {
                            f.Id,
                            f.Alias,
                            number = f.ClubWeaponNumber,
                            isBooked,
                            bookedByMe = mine.Contains(f.Id),
                            isBookable = !isBooked && !blockedByStatus,
                            statusLabel = string.IsNullOrWhiteSpace(f.Status)
                                ? FirearmStatus.Tillgangligt : f.Status!,
                        };
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAvailability failed for club {ClubId}", clubId);
                return Json(new { success = false, message = "Kunde inte läsa tillgängligheten." });
            }
        }

        /// <summary>
        /// Tolkar fönstret och bygger etiketten som visas ovanför listan.
        ///
        /// <para><b>Tom sluttid = hela dagen</b>, samma regel som <c>FirearmBookingService</c>
        /// tillämpar vid bokning. Regeln bor på två ställen därför att svaret måste vara identiskt:
        /// hade listan visat tillgänglighet för ett annat fönster än bokningen sedan tar, skulle en
        /// "ledig" rad kunna nekas i nästa klick.</para>
        /// </summary>
        private static bool TryWindow(string? from, string? to, out DateTime f, out DateTime t, out string label)
        {
            f = default; t = default; label = "";
            if (!DateTime.TryParse((from ?? "").Trim(), out f)) return false;

            if (!DateTime.TryParse((to ?? "").Trim(), out t) || t <= f)
            {
                f = f.Date;
                t = f.AddDays(1).AddSeconds(-1);
                label = $"{f:yyyy-MM-dd} (hela dagen)";
                return true;
            }

            label = f.Date == t.Date
                ? $"{f:yyyy-MM-dd} {f:HH\\:mm}–{t:HH\\:mm}"
                : $"{f:yyyy-MM-dd HH\\:mm} – {t:yyyy-MM-dd HH\\:mm}";
            return true;
        }
    }
}
