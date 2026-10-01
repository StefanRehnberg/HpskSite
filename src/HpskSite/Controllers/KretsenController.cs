using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Kretsgranskning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kretsgranskningens egna sidor (fas 1). Routad kontroller, ingen Umbraco-nod — samma mönster
    /// som /styrelse.
    ///
    /// <list type="bullet">
    /// <item><c>/kretsen/kom-igang?krets=</c> — hur kretsen börjar ta emot ärenden inloggat: utse
    /// vem som har vilket uppdrag. Öppen utan inloggning, eftersom den är dit mejlen i länkläget pekar.</item>
    /// <item><c>/kretsen/uppdrag?t=</c> — bekräfta en medlems förfrågan om ett uppdrag.</item>
    /// </list>
    /// </summary>
    [Route("kretsen")]
    public class KretsenController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _roles;
        private readonly KretsUppdragService _uppdrag;
        private readonly KretsLinkTokenService _tokens;

        public KretsenController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            BoardRoleService roles,
            KretsUppdragService uppdrag,
            KretsLinkTokenService tokens)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _roles = roles;
            _uppdrag = uppdrag;
            _tokens = tokens;
        }

        [HttpGet("kom-igang")]
        public async Task<IActionResult> KomIgang(int? krets)
        {
            if (!TryRoot(out var root, out var ctx)) return StatusCode(500, "Umbraco-kontext saknas.");

            var region = krets is > 0 ? ctx!.Content!.GetById(krets.Value) : null;
            if (region == null || region.ContentType.Alias != "regionalPage")
                return NotFound("Kretsen hittades inte.");

            var me = await CurrentMemberAsync();
            var code = region.Value<string>("regionCode") ?? "";
            var canManage = me != null && !string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code);

            var model = new KretsKomIgangModel
            {
                RegionId = region.Id,
                RegionName = region.Value<string>("regionName") ?? region.Name ?? "",
                RegionUrl = region.Url(),
                IsLoggedIn = me != null,
                MemberName = me?.Name ?? "",
                CanManageRoles = canManage,
                Uppdrag = _uppdrag.Status(region.Id).Select(s => new KretsKomIgangUppdrag
                {
                    RoleKey = s.RoleKey,
                    Label = s.Label,
                    Task = BoardRoleDefinitions.KretsUppdragTask(s.RoleKey),
                    IsFilled = !s.IsMissing,
                    // Namnen visas bara för inloggade: en krets funktionärer är inte hemliga, men
                    // sidan är öppen och behöver inte vara en personkatalog.
                    HolderNames = me != null ? s.Holders.Select(h => h.Name).ToList() : new List<string>(),
                    IHaveIt = me != null && s.Holders.Any(h => h.MemberId == me.Id)
                }).ToList()
            };

            ViewData["KomIgang"] = model;
            return View("KretsenKomIgang", root);
        }

        [HttpGet("uppdrag")]
        public async Task<IActionResult> Uppdrag(string? t)
        {
            if (!TryRoot(out var root, out var ctx)) return StatusCode(500, "Umbraco-kontext saknas.");

            var model = new KretsUppdragConfirmModel { Token = t ?? "" };
            var p = _tokens.ReadUppdragRequest(t);
            if (p == null)
            {
                model.Invalid = true;
            }
            else
            {
                var region = ctx!.Content!.GetById(p.RegionId);
                var requester = _memberService.GetById(p.MemberId);
                model.RegionId = p.RegionId;
                model.RegionName = region?.Value<string>("regionName") ?? region?.Name ?? "";
                model.RoleLabel = BoardRoleDefinitions.GetLabel(p.RoleKey);
                model.RequesterName = requester == null ? "" : DisplayName(requester);
                model.RequestedAt = p.RequestedAt;
                model.AlreadyHolder = _uppdrag.HasUppdrag(p.RegionId, p.MemberId, p.RoleKey);

                var me = await CurrentMemberAsync();
                model.IsLoggedIn = me != null;
                if (me != null && region != null)
                {
                    var code = region.Value<string>("regionCode") ?? "";
                    model.CanConfirm = (!string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code))
                        || _roles.HasActiveRole(DocumentOwnerType.Region, p.RegionId, me.Id, BoardRoleDefinitions.RoleOrdforande);
                }
            }

            ViewData["UppdragConfirm"] = model;
            return View("KretsenUppdragConfirm", root);
        }

        /// <summary>
        /// Kretsens inkorg för tävlingsansökningar. En EGEN sida och inte en flik i kretsens
        /// adminpanel: den tävlingsansvarige är ofta inte kretsadministratör och når inte panelen.
        /// Själva datat och behörigheten ligger i CompetitionApplication-endpointsen.
        /// </summary>
        [HttpGet("ansokningar")]
        public async Task<IActionResult> Ansokningar(int? krets)
        {
            if (!TryRoot(out var root, out var ctx)) return StatusCode(500, "Umbraco-kontext saknas.");
            var region = krets is > 0 ? ctx!.Content!.GetById(krets.Value) : null;
            if (region == null || region.ContentType.Alias != "regionalPage") return NotFound("Kretsen hittades inte.");

            var me = await CurrentMemberAsync();
            if (me == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/kretsen/ansokningar?krets={region.Id}")}");

            var code = region.Value<string>("regionCode") ?? "";
            var allowed = _uppdrag.HasUppdrag(region.Id, me.Id, BoardRoleDefinitions.RoleTavlingsansvarig)
                          || (!string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code));
            ViewData["KretsAnsokningar"] = new KretsAnsokningarModel
            {
                RegionId = region.Id,
                RegionName = region.Value<string>("regionName") ?? region.Name ?? "",
                RegionUrl = region.Url(),
                Allowed = allowed
            };
            return View("KretsenAnsokningar", root);
        }

        /// <summary>Länkläget: ett ärende utan inloggning. Länken bär ärendet; sidan hämtar det.</summary>
        [HttpGet("arende")]
        public IActionResult Arende(string? t)
        {
            if (!TryRoot(out var root, out _)) return StatusCode(500, "Umbraco-kontext saknas.");
            ViewData["ArendeToken"] = t ?? "";
            return View("KretsenArende", root);
        }

        /// <summary>
        /// Kretsens sammanställning till Förbundet: nationella och landsdelsansökningar med kretsens
        /// yttrande, i blankettens kolumner (Datum, Namn, Gren, Plats, Arrangör, Tillstyrks/Avstyrks).
        /// Utskriftsvänlig sida — vi har ingen pdf-motor. Sparas som pdf ur webbläsaren och bifogas.
        /// </summary>
        [HttpGet("sammanstallning")]
        public async Task<IActionResult> Sammanstallning(int? krets, int? year)
        {
            if (!TryRoot(out var root, out var ctx)) return StatusCode(500, "Umbraco-kontext saknas.");
            var region = krets is > 0 ? ctx!.Content!.GetById(krets.Value) : null;
            if (region == null || region.ContentType.Alias != "regionalPage") return NotFound("Kretsen hittades inte.");
            var me = await CurrentMemberAsync();
            if (me == null) return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(Request.Path + Request.QueryString)}");
            var code = region.Value<string>("regionCode") ?? "";
            if (!_uppdrag.HasUppdrag(region.Id, me.Id, BoardRoleDefinitions.RoleTavlingsansvarig)
                && !(!string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code)))
                return StatusCode(403, "Bara kretsens tävlingsansvarige eller kretsadministratören kan se sammanställningen.");

            var y = year ?? DateTime.Today.Year + 1;
            var apps = HttpContext.RequestServices.GetRequiredService<CompetitionApplicationService>()
                .ForRegion(region.Id, new DateTime(y, 1, 1), new DateTime(y, 12, 31))
                .Where(a => a.NeedsForbundet && a.KretsOpinion != null
                            && (a.Status == HpskSite.Models.Kretsgranskning.CompetitionApplicationStatus.HosForbundet
                                || a.Status == HpskSite.Models.Kretsgranskning.CompetitionApplicationStatus.Beviljad
                                || a.Status == HpskSite.Models.Kretsgranskning.CompetitionApplicationStatus.Avslagen))
                .OrderBy(a => a.CompetitionDate).ToList();
            var cal = HttpContext.RequestServices.GetRequiredService<KretsCalendarService>();
            var clubs = HttpContext.RequestServices.GetRequiredService<ClubService>();

            ViewData["Sammanstallning"] = new KretsSammanstallningModel
            {
                RegionId = region.Id,
                RegionName = region.Value<string>("regionName") ?? region.Name ?? "",
                Year = y,
                Deadline = HpskSite.Models.Kretsgranskning.CompetitionApplicationRules.ForbundetDeadline(y),
                Rows = apps.Select(a => new KretsSammanstallningRow
                {
                    Id = a.Id,
                    Date = a.CompetitionDate,
                    Name = a.Name,
                    Discipline = ActivityDiscipline.Label(a.Discipline),
                    Level = HpskSite.CompetitionTypes.Common.CompetitionLevel.Find(a.Level)?.Label ?? a.Level,
                    Place = a.Place ?? "",
                    Organiser = a.ClubId > 0 ? (clubs.GetClubNameById(a.ClubId) ?? "") : (cal.Region(a.RegionId)?.Name ?? ""),
                    Tillstyrks = a.KretsOpinion == HpskSite.Models.Kretsgranskning.CompetitionApplicationOpinion.Tillstyrker,
                    Motivering = a.KretsOpinionText ?? "",
                    SentAt = a.SentToForbundetAt
                }).ToList()
            };
            return View("KretsenSammanstallning");
        }

        private bool TryRoot(out IPublishedContent? root, out IUmbracoContext? ctx)
        {
            root = null;
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out ctx) || ctx.Content == null) return false;
            root = ctx.Content.GetAtRoot().FirstOrDefault();
            return root != null;
        }

        private async Task<Umbraco.Cms.Core.Models.IMember?> CurrentMemberAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            return current?.Email == null ? null : _memberService.GetByEmail(current.Email);
        }

        private static string DisplayName(Umbraco.Cms.Core.Models.IMember m)
        {
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m.Name ?? "");
        }
    }

    public class KretsKomIgangModel
    {
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public string RegionUrl { get; set; } = "";
        public bool IsLoggedIn { get; set; }
        public string MemberName { get; set; } = "";
        public bool CanManageRoles { get; set; }
        public List<KretsKomIgangUppdrag> Uppdrag { get; set; } = new();
        public bool AllFilled => Uppdrag.All(u => u.IsFilled);
    }

    public class KretsKomIgangUppdrag
    {
        public string RoleKey { get; set; } = "";
        public string Label { get; set; } = "";
        public string Task { get; set; } = "";
        public bool IsFilled { get; set; }
        public bool IHaveIt { get; set; }
        public List<string> HolderNames { get; set; } = new();
    }

    public class KretsAnsokningarModel
    {
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public string RegionUrl { get; set; } = "";
        public bool Allowed { get; set; }
    }

    public class KretsSammanstallningModel
    {
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public int Year { get; set; }
        public DateTime Deadline { get; set; }
        public List<KretsSammanstallningRow> Rows { get; set; } = new();
    }

    public class KretsSammanstallningRow
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Name { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string Level { get; set; } = "";
        public string Place { get; set; } = "";
        public string Organiser { get; set; } = "";
        public bool Tillstyrks { get; set; }
        public string Motivering { get; set; } = "";
        public DateTime? SentAt { get; set; }
    }

    public class KretsUppdragConfirmModel
    {
        public string Token { get; set; } = "";
        public bool Invalid { get; set; }
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public string RoleLabel { get; set; } = "";
        public string RequesterName { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public bool AlreadyHolder { get; set; }
        public bool IsLoggedIn { get; set; }
        public bool CanConfirm { get; set; }
    }
}
