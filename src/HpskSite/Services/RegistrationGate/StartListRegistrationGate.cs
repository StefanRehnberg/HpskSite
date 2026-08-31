using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Services.RegistrationGate
{
    /// <summary>
    /// "Är självanmälan stängd för att startlistan är publicerad?" — asked by the public competition
    /// page AND by <c>CompetitionController.RegisterForCompetition</c>, so this is the one place the
    /// question is answered.
    ///
    /// WHY THE GATE EXISTS. A start list places every registered shooter into a skjutlag/patrull with
    /// a start number. A registration that arrives AFTER that placement is not on the list, and the
    /// only affordance an organiser reliably finds on the Startlistor tab is "Skapa ny startlista" —
    /// which renumbers everyone. The correct path (Anmälningar → Åtgärder → Anmäl och betala, which
    /// appends to a chosen skjutlag via <c>AssignWalkInToStartListTeam</c> and touches nobody else's
    /// number) only helps if the late entry actually reaches the desk. Closing self-registration at
    /// publish is what routes it there.
    ///
    /// ⚠️ THE GATE IS DERIVED, NEVER MIRRORED. It is
    ///   (organiser's stored choice) AND (a start list is published right now)
    /// evaluated on every read. An earlier shape stored a single "registration is closed" boolean
    /// flipped at publish and cleared at unpublish; that is a mirror, and mirrors on this codebase go
    /// stale the moment one of the two writers is missed (see the scoringMode drift). Deriving it
    /// means unpublishing a list reopens registration with no second write to remember, and
    /// Springskytte's per-class publishing cannot leave the flag saying "open" while a list is still
    /// public.
    ///
    /// ⚠️ THE DEFAULT IS OFF, and that is a deployment property rather than an opinion:
    /// <see cref="PropertyAlias"/> is absent (or false) on every existing competition, so shipping
    /// this changes nothing for a competition whose start list is ALREADY published. The organiser
    /// opts in from the publish dialog, where the consequence is on screen. Making it default-on
    /// would have closed registration on live competitions at deploy time with nobody told.
    /// </summary>
    public sealed class StartListRegistrationGate
    {
        /// <summary>
        /// True/False on the <c>competition</c> doctype. ⚠️ Without the property <c>SetValue</c> is a
        /// silent no-op, so <see cref="PersistChoice"/> refuses and names it rather than reporting a
        /// save that did nothing.
        /// </summary>
        public const string PropertyAlias = "closeRegistrationOnStartList";

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IContentService _contentService;

        public StartListRegistrationGate(
            IUmbracoContextAccessor umbracoContextAccessor,
            IContentService contentService)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _contentService = contentService;
        }

        /// <summary>
        /// Is a start list published for this competition right now?
        ///
        /// ⚠️ Reads the PUBLISHED cache on purpose. Every publish endpoint does Save()+Publish()
        /// specifically so the public competition page sees the flag; a draft-only value means the
        /// list is not public, and a gate that fired on it would close registration for a list no
        /// shooter can see.
        /// </summary>
        public static bool HasPublishedStartList(IPublishedContent competition)
        {
            if (competition == null) return false;

            // Fältskytte / MagnumFält publish the patrol list as a whole, as a flag on the
            // competition itself — there is no per-list node to inspect.
            if (competition.Value<bool>("faltskyttePatrolsPublished")) return true;

            // Precision family AND Springskytte both store their lists as `precisionStartList`
            // children carrying `isOfficialStartList`. Springskytte publishes one node per weapon
            // class / day, so ANY published node closes the gate: the shooters on that list have
            // their numbers, and a regeneration is what we are protecting them from.
            var children = competition.Children()?.ToList() ?? new List<IPublishedContent>();
            if (children.Any(c => c.ContentType.Alias == "precisionStartList"
                                  && c.Value<bool>("isOfficialStartList")))
                return true;

            // Legacy layout: nested under a competitionStartListsHub. Kept because the public
            // competition page still falls back to it when resolving the "Visa startlista" button.
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
            if (hub?.Children()?.Any(sl => sl.Value<bool>("isOfficialStartList")) == true)
                return true;

            return false;
        }

        /// <summary>
        /// The gate itself, for a caller that already holds the published competition (the view).
        /// </summary>
        public static bool IsClosed(IPublishedContent competition)
        {
            if (competition == null) return false;
            if (!competition.Value<bool>(PropertyAlias)) return false;
            return HasPublishedStartList(competition);
        }

        /// <summary>
        /// The gate for a caller that only has the id (the registration endpoint). Resolves from the
        /// published cache — competitions are always published.
        ///
        /// Fails OPEN on any lookup problem. A shooter who cannot register because the cache hiccupped
        /// is a support call about a competition that looks broken; a shooter who slips through is one
        /// row the coverage panel already flags as oplacerad.
        /// </summary>
        public bool IsClosed(int competitionId)
        {
            try
            {
                if (competitionId <= 0) return false;
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return false;
                var competition = ctx.Content.GetById(competitionId);
                if (competition == null || competition.ContentType.Alias != "competition") return false;
                return IsClosed(competition);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Writes the organiser's choice onto an <c>IContent</c> the caller is about to save anyway
        /// (Fältskytte's PublishPatrolList already saves + publishes the competition node, and a
        /// second publish there would be a wasted version bump).
        /// </summary>
        /// <returns>False when the doctype property is missing — the caller must say so.</returns>
        public static bool SetChoice(IContent competition, bool? closeRegistration)
        {
            if (competition == null || closeRegistration == null) return true;   // nothing asked for
            if (!competition.HasProperty(PropertyAlias)) return false;
            competition.SetValue(PropertyAlias, closeRegistration.Value);
            return true;
        }

        /// <summary>
        /// Writes and persists the choice for callers whose publish does not otherwise touch the
        /// competition node (Precision, Springskytte).
        ///
        /// ⚠️ Publish() can fail on a competition with an empty mandatory doctype field. The flag then
        /// sits on the draft while the public page reads the published cache, i.e. the organiser is
        /// told registration is closed and it is not. That is reported, never swallowed — the same
        /// mistake PublishPatrolList was fixed for.
        /// </summary>
        public (bool Ok, string? Message) PersistChoice(int competitionId, bool? closeRegistration)
        {
            if (closeRegistration == null || competitionId <= 0) return (true, null);

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return (true, null);

            if (!SetChoice(competition, closeRegistration))
            {
                return (false, $"Startlistan publicerades, men anmälan kunde inte stängas: egenskapen '{PropertyAlias}' saknas på dokumenttypen competition. Lägg till den (True/False) i backoffice.");
            }

            _contentService.Save(competition);
            var pub = _contentService.Publish(competition, new[] { "*" }, -1);
            if (!pub.Success)
            {
                var invalid = pub.InvalidProperties != null && pub.InvalidProperties.Any()
                    ? " Ogiltiga/obligatoriska fält: " + string.Join(", ", pub.InvalidProperties.Select(p => p.Alias))
                    : "";
                return (false, $"Startlistan publicerades, men inställningen för anmälan kunde inte publiceras ({pub.Result}).{invalid} Åtgärda i tävlingens inställningar och publicera startlistan igen.");
            }

            return (true, null);
        }

        /// <summary>
        /// The shooter-facing explanation. Deliberately NOT "Anmälan stängd": a dead button with no
        /// reason is what makes people mail the organiser to ask, or decide not to turn up at all.
        /// The one thing they must learn is that showing up still works.
        /// </summary>
        public const string ShooterMessage =
            "Startlistan är publicerad, så självanmälan är stängd. Du kan fortfarande komma med — kontakta arrangören, eller anmäl dig på plats före start.";
    }
}
