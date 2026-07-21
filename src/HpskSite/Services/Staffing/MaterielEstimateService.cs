using HpskSite.Models;
using HpskSite.Models.Staffing;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Discipline-aware materiel-quantity estimate ("Beställningslista") derived from the competition's
    /// participant / class / series counts — the general counterpart to the Fältskytte figure-BOM. These are
    /// planning ESTIMATES (clearly labelled), not exact orders; a materielansvarig adjusts before ordering.
    /// </summary>
    public class MaterielEstimateService
    {
        private readonly IContentService _contentService;

        public MaterielEstimateService(IContentService contentService)
        {
            _contentService = contentService;
        }

        private static readonly string[] PrecisionFamily =
            { "Precision", "MagnumPrecision", "Milsnabb", "Duell", "NationellHelmatch", "Standardpistol", "Sportpistol" };

        public MaterielEstimateResponse Estimate(int competitionId)
        {
            var comp = _contentService.GetById(competitionId);
            if (comp == null) return new MaterielEstimateResponse { Success = false, Message = "Tävlingen hittades inte." };

            var discipline = comp.GetValue<string>("competitionType") ?? "";
            var series = comp.GetValue<int>("numberOfSeriesOrStations");

            // Registrations (unpublished nodes) live under the competitionRegistrationsHub child.
            int participants = 0, starts = 0;
            var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var children = _contentService.GetPagedChildren(competitionId, 0, 200, out _).ToList();
                var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                if (hub != null)
                {
                    var regs = _contentService.GetPagedChildren(hub.Id, 0, 5000, out _)
                        .Where(c => c.ContentType.Alias == "competitionRegistration").ToList();
                    foreach (var reg in regs)
                    {
                        var json = reg.GetValue<string>("shootingClasses");
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var entries = CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                        if (entries.Count == 0) continue;
                        participants++;
                        starts += entries.Count;
                        foreach (var e in entries) if (!string.IsNullOrWhiteSpace(e.Class)) classes.Add(e.Class);
                    }
                }
            }
            catch { /* counts stay 0 → estimate still renders with formulas */ }

            var resp = new MaterielEstimateResponse
            {
                Discipline = discipline,
                ParticipantCount = participants,
                StartCount = starts,
                ClassCount = classes.Count,
                Series = series,
            };

            int classCount = Math.Max(classes.Count, 0);
            int Ceil(double v) => (int)Math.Ceiling(v);

            bool isPrecision = PrecisionFamily.Contains(discipline, StringComparer.OrdinalIgnoreCase);
            bool isSpring = string.Equals(discipline, "Springskytte", StringComparison.OrdinalIgnoreCase);

            if (isPrecision)
            {
                var seriesEff = series > 0 ? series : 1;
                resp.Rows.Add(new MaterielEstimateRow { Category = "Tavlor", Item = "Tävlingstavlor", Quantity = Ceil(starts * seriesEff * 1.1), Unit = "st", Basis = $"{starts} starter × {seriesEff} serier + 10 % reserv" });
                resp.Rows.Add(new MaterielEstimateRow { Category = "Tavlor", Item = "Provtavlor", Quantity = starts, Unit = "st", Basis = "1 provserie per start" });
                resp.Rows.Add(new MaterielEstimateRow { Category = "Markering", Item = "Markeringsklister / tejp", Quantity = null, Unit = "", Basis = "efter behov" });
            }
            else if (isSpring)
            {
                resp.Rows.Add(new MaterielEstimateRow { Category = "Bana", Item = "Nummervästar", Quantity = participants, Unit = "st", Basis = "1 per deltagare" });
                resp.Rows.Add(new MaterielEstimateRow { Category = "Sekretariat", Item = "Varvräkningsblad", Quantity = participants, Unit = "st", Basis = "1 per deltagare" });
                resp.Rows.Add(new MaterielEstimateRow { Category = "Sjukvård", Item = "Vätska (portioner)", Quantity = participants, Unit = "st", Basis = "1 per deltagare" });
                resp.Rows.Add(new MaterielEstimateRow { Category = "Bana", Item = "Straffrundemarkeringar / koner", Quantity = null, Unit = "", Basis = "efter bana" });
            }

            // Common to all disciplines.
            resp.Rows.Add(new MaterielEstimateRow { Category = "Priser", Item = "Medaljer (1:a/2:a/3:e per klass)", Quantity = classCount > 0 ? classCount * 3 : null, Unit = "st", Basis = classCount > 0 ? $"3 × {classCount} klasser" : "3 per klass" });
            resp.Rows.Add(new MaterielEstimateRow { Category = "Priser", Item = "Diplom", Quantity = participants > 0 ? participants : null, Unit = "st", Basis = "1 per deltagare" });

            return resp;
        }
    }
}
