using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fas B5: innehållet i träningspanelen — dialogen som öppnas när en medlem klickar på en
    /// träning i kalendern, på klubbsidan eller i flödet (Stefans beslut 2026-10-04: panel, ingen
    /// egen sida).
    ///
    /// <para>Panelen är en IFRAME i dialogen, och det här är sidan i den. Skälet är
    /// anmälningskortet: det finns i ett exemplar per sida och läser sitt tillfälle vid
    /// sidladdning, med betalningsdialog och lånevapen. Att rendera det i en iframe ger exakt samma
    /// kort som en händelse har, i stället för en andra, glidande kopia skriven för en dialog.</para>
    ///
    /// <para>Routad, ingen Umbraco-nod. Ingen inloggning krävs för att SE träningen — kortet säger
    /// själv vad en utloggad behöver göra.</para>
    /// </summary>
    [Route("traning/panel")]
    public class TrainingPanelController : Controller
    {
        private readonly ClubEventParticipationService _participation;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly HpskSite.Services.Training.TrainingCourseService _courses;

        public TrainingPanelController(
            ClubEventParticipationService participation,
            IMemberManager memberManager,
            IMemberService memberService,
            HpskSite.Services.Training.TrainingCourseService courses)
        {
            _courses = courses;
            _participation = participation;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int id = 0)
        {
            var model = new TrainingPanelModel { Id = id };
            var ctx = _participation.GetTrainingContext(id);
            if (ctx == null)
            {
                model.Error = "Träningen finns inte längre.";
                return View("~/Views/TrainingPanel.cshtml", model);
            }

            model.Name = ctx.EventName;
            model.Date = ctx.EventDate;
            model.EndDate = ctx.EventEndDate;
            model.Venue = ctx.Venue;
            model.ClubName = ctx.OwnerName;
            model.IsCancelled = ctx.IsCancelled;
            model.IsMandatory = ctx.IsMandatory;
            if (ctx.SkjutledareMemberId is > 0)
                model.SkjutledareName = _memberService.GetById(ctx.SkjutledareMemberId.Value)?.Name ?? "";

            var t = _participation.GetTrainingRow(id);
            model.Discipline = ActivityDiscipline.Label(t?.Discipline);
            model.Description = t?.Description ?? "";

            var current = await _memberManager.GetCurrentMemberAsync();
            var me = current?.Email == null ? null : _memberService.GetByEmail(current.Email);
            model.CanManage = me != null && await _participation.CanManageAsync(ctx, me.Id);
            if (me != null) model.MyCourses = _courses.CoursesForMemberOnTraining(me.Id, id);
            return View("~/Views/TrainingPanel.cshtml", model);
        }
    }

    public class TrainingPanelModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime? Date { get; set; }
        public DateTime? EndDate { get; set; }
        public string Venue { get; set; } = "";
        public string ClubName { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string Description { get; set; } = "";
        public string SkjutledareName { get; set; } = "";
        public bool IsCancelled { get; set; }
        public bool IsMandatory { get; set; }
        public bool CanManage { get; set; }
        /// <summary>Kurserna den inloggade går som tillfället hör till, med kursens krav.</summary>
        public List<HpskSite.Services.Training.TrainingCourseService.MemberCourseOnTraining> MyCourses { get; set; } = new();
        public string? Error { get; set; }
    }
}
