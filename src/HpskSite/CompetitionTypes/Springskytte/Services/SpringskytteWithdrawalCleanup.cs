using HpskSite.CompetitionTypes.Springskytte.Controllers;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.Models.StartLists;
using HpskSite.Services.StartLists;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Städar en springskytt som avanmäler sig SJÄLV: tar skytten ur startlistorna (platsen blir en ledig lucka,
    /// ingen annans nummer eller tid ändras), publicerar om de officiella listorna och tar bort skyttens
    /// resultat- och tidsjusteringsrader.
    ///
    /// <para><b>Varför en egen klass och inte <c>StartListCleanupService</c>:</b> arrangörens borttagning av en
    /// springanmälan städar via ett klientanrop (<c>Springskytte/CleanupDeletedRegistration</c>, som kräver
    /// funktionärsbehörighet). En skytt som tar bort sin egen anmälan har ingen sådan behörighet och inget
    /// klientanrop, så städningen måste göras på servern. Lägg inte till den här som källa i
    /// <c>StartListCleanupService</c> utan att ta bort klientanropet — då körs båda och den andra rapporterar
    /// "0 frigjorda", vilket ser ut som ett fel.</para>
    ///
    /// <para>Samma regler som <c>CleanupDeletedRegistration</c>; byt inte den ena utan den andra.</para>
    /// </summary>
    public sealed class SpringskytteWithdrawalCleanup
    {
        private readonly IStartListStore _startLists;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<SpringskytteWithdrawalCleanup> _logger;

        public SpringskytteWithdrawalCleanup(IStartListStore startLists, IUmbracoDatabaseFactory databaseFactory,
            ILogger<SpringskytteWithdrawalCleanup> logger)
        {
            _startLists = startLists;
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>Antal platser som frigjordes, och om någon publicerad lista ändrades. Kastar aldrig.</summary>
        public (int SlotsFreed, bool PublishedChanged) Clean(int competitionId, int memberId)
        {
            int freed = 0;
            bool published = false;
            try
            {
                foreach (var node in _startLists.List(competitionId, StartListKind.Qualifying).Where(r => !r.LegacyHub))
                {
                    var cfgJson = node.ConfigurationData;
                    if (string.IsNullOrEmpty(cfgJson)) continue;
                    SpringskytteStartListConfig? cfg = null;
                    try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(cfgJson); } catch { }
                    if (cfg?.Starters == null || cfg.Starters.Count == 0) continue;

                    var removed = cfg.Starters.Where(s => s.MemberId == memberId).ToList();
                    if (removed.Count == 0) continue;

                    cfg.Starters = cfg.Starters.Where(s => s.MemberId != memberId).ToList();
                    bool official = node.IsOfficial;
                    _startLists.Update(node.Id, r =>
                    {
                        r.ConfigurationData = JsonConvert.SerializeObject(cfg);
                        r.StartListContent = SpringskytteController.BuildStartListHtml(cfg.Starters, cfg);
                    }, official ? StartListPublish.Always : StartListPublish.Never);
                    freed += removed.Count;
                    published |= official;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Springskytte: startlistorna kunde inte städas efter avanmälan (tävling {CompetitionId}, medlem {MemberId}).", competitionId, memberId);
            }

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.Execute("DELETE FROM SpringskytteResultEntry WHERE CompetitionId=@0 AND MemberId=@1", competitionId, memberId);
                db.Execute("DELETE FROM SpringskytteTimeAdjustment WHERE CompetitionId=@0 AND MemberId=@1", competitionId, memberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Springskytte: resultatrader kunde inte tas bort efter avanmälan (tävling {CompetitionId}, medlem {MemberId}).", competitionId, memberId);
            }
            return (freed, published);
        }
    }
}
