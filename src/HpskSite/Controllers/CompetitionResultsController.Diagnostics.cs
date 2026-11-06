#if DEBUG
using System.Security.Cryptography;
using System.Text;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Golden-master-endpoint för "Utanför mästerskapet" (arbetsplan v3, S0). Räknar om resultat
    /// SKRIVSKYDDAT och lämnar ett facit utan namn som kan jämföras före/efter varje ändring.
    ///
    /// ⚠️ Kompileras bara i DEBUG — Release/prod innehåller inte koden. Anropar ALDRIG GetResultsList,
    /// Refresh*Async, _results.Update, Create/Ensure, Materialize*, ResultReviewHooks, CacheSignals eller e-post
    /// (de skriver eller självläker). Vägrar allt utom en databas som står i Verify:AllowedDatabases.
    /// </summary>
    public partial class CompetitionResultsController
    {
        private static readonly string[] WriteProofTables =
        {
            "CompetitionResultList", "StandardMedalAward", "CompetitionRegistration", "CompetitionRegistrationClass",
            "CompetitionStartList" // finalvägen läser startlistor; endpointen får aldrig skriva dem (FreezeClassResultsAsync gör det)
        };

        // Egenskaper som bär tid (maskas — tal maskas ALDRIG) respektive personuppgifter (hashas/maskas).
        private static bool IsTimeProperty(string n) =>
            n.EndsWith("At", StringComparison.Ordinal) || n.EndsWith("Date", StringComparison.Ordinal)
            || n.EndsWith("Utc", StringComparison.Ordinal) || n.Equals("Timestamp", StringComparison.OrdinalIgnoreCase);

        private static bool IsNameProperty(string n) =>
            n is "Name" or "ShooterName" or "MemberName" or "Club" or "ClubName" or "Email" or "MemberEmail"
              or "FirstName" or "LastName" or "Phone" or "ClubShort";

        private static bool IsMemberIdProperty(string n) =>
            n is "MemberId" or "memberId" or "ShooterId" or "SetByMemberId";

        /// <summary>
        /// GET /umbraco/surface/competitionresults/verifyrecompute?confirm=golden-master&amp;ids=1,2,3&amp;mode=off
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("VerifyGuard: 404 utom i Development med HPSK_VERIFY_ENDPOINTS, tillåten databas och hash-nyckel; finns bara i DEBUG-bygget")]
        public async Task<IActionResult> VerifyRecompute(string? confirm, string? ids, string? mode, bool full = false)
        {
            var refuse = VerifyGuard(confirm);
            if (refuse != null) return refuse;

            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var hmacKey = cfg["Verify:HashKey"];
            if (string.IsNullOrWhiteSpace(hmacKey))
                return NotFound(); // Utan nyckel i user-secrets hashas inget — och inget lämnas ut.

            var idList = (ids ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().OrderBy(v => v).ToList();
            if (idList.Count == 0)
                return Json(new { error = "Ange ?ids=1,2,3 (tävlings-id)." });

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hmacKey));

            var before = ReadWriteProof();
            var fieldBuilder = HttpContext.RequestServices.GetRequiredService<FaltskytteResultsBuilder>();

            var perCompetition = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var id in idList)
            {
                var comp = _competitions.GetSaved(id);
                object? payload;
                string kind;
                try
                {
                    if (comp == null) { kind = "saknas"; payload = null; }
                    else if (string.Equals(comp.TypeId, "Faltskytte", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = "faltskytte";
                        var built = await fieldBuilder.BuildAsync(id);
                        payload = built.Error != null ? new { error = built.Error } : (object?)built.Results;
                    }
                    else
                    {
                        kind = "precision";
                        var rows = await GetCompetitionResultsInternal(id);
                        payload = rows.Count == 0 ? null : await CalculateFinalResults(rows, id);
                    }
                }
                catch (Exception ex)
                {
                    kind = "fel";
                    payload = new { exception = ex.GetType().Name };
                }

                var token = payload == null ? JValue.CreateNull() : JToken.FromObject(payload, JsonSerializer.CreateDefault());
                var canonical = Canonicalise(token, hmac);
                var json = canonical.ToString(Formatting.None);
                perCompetition[id.ToString()] = new
                {
                    kind,
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
                    json = full ? canonical : null
                };
            }

            var after = ReadWriteProof();
            var wroteSomething = !before.SequenceEqual(after);

            return Content(JsonConvert.SerializeObject(new
            {
                header = new
                {
                    commit = cfg["Verify:Commit"] ?? Environment.GetEnvironmentVariable("HPSK_COMMIT"),
                    database = before.Count > 0 ? ReadDbName() : null,
                    mode = mode ?? "(växeln som den står)",
                    competitions = idList.Count,
                    writeProofBefore = before,
                    writeProofAfter = after,
                    WROTE_SOMETHING = wroteSomething
                },
                competitions = perCompetition
            }, Formatting.Indented), "application/json");
        }

        /// <summary>
        /// GET /umbraco/surface/competitionresults/verifyfinals?confirm=golden-master&amp;ids=1,2,3 — FINALVÄGENS golden-master
        /// (expertgruppens K5: VerifyRecompute kör bara CalculateFinalResults, aldrig kvalrankingen eller finalstartlisteberäkningen).
        /// Per tävling med final: kvalrankingen (alla skyttar per grupp), finalistgränsen (kortets "X av N går vidare"), den
        /// genererade finalstartlistan (ur en ögonblicksbild i minnet, utan sparade inställningar) och de LAGRADE finalisternas
        /// starter. SKRIVSKYDDAT: anropar aldrig FreezeClassResultsAsync/UnfreezeClassAsync, Generate*-endpoints eller
        /// startlistelagrets Update — ögonblicksbilden byggs bara i minnet. Skrivbeviset (CompetitionStartList m.fl.) bevisar det.
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("VerifyGuard: 404 utom i Development med HPSK_VERIFY_ENDPOINTS, tillåten databas och hash-nyckel; finns bara i DEBUG-bygget")]
        public async Task<IActionResult> VerifyFinals(string? confirm, string? ids, bool full = false)
        {
            var refuse = VerifyGuard(confirm);
            if (refuse != null) return refuse;

            var cfg = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var hmacKey = cfg["Verify:HashKey"];
            if (string.IsNullOrWhiteSpace(hmacKey)) return NotFound();

            var idList = (ids ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().OrderBy(v => v).ToList();
            if (idList.Count == 0) return Json(new { error = "Ange ?ids=1,2,3 (tävlings-id)." });

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hmacKey));
            var sp = HttpContext.RequestServices;
            var qualifying = sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionQualifyingResultsService>();
            var qualification = sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsQualificationService>();
            var startListBuilder = sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsStartListBuilder>();

            var before = ReadWriteProof();
            var perCompetition = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var id in idList)
            {
                var comp = _competitions.GetSaved(id);
                object? payload;
                string kind;
                try
                {
                    if (comp == null) { kind = "saknas"; payload = null; }
                    else if (CompetitionFinals.Effective(comp.TypeId, comp.NumberOfFinalSeries) <= 0) { kind = "ingen-final"; payload = null; }
                    else
                    {
                        kind = "final";
                        var rankings = await qualifying.GetAvailableClassRankingsAsync(id);

                        // Ögonblicksbild i MINNET av "alla grupper låsta" — samma form som FreezeClassResultsAsync skriver, men aldrig sparad.
                        var snapshot = new HpskSite.CompetitionTypes.Precision.Models.QualifyingResultsSnapshot { CompetitionId = id };
                        foreach (var r in rankings)
                            snapshot.ClassSnapshots[r.ChampionshipClass] = new HpskSite.CompetitionTypes.Precision.Models.ClassResultsSnapshot
                            {
                                ChampionshipClass = r.ChampionshipClass,
                                QualifiedShooters = r.QualifiedShooters
                            };
                        var noConfig = new Dictionary<string, HpskSite.CompetitionTypes.Precision.Models.FinalsClassConfig>();
                        var preview = startListBuilder.PreviewBuckets(snapshot, noConfig);
                        var built = startListBuilder.Build(snapshot, noConfig, new HpskSite.CompetitionTypes.Precision.Models.FinalsStartListSettings());

                        // Kortets "X av N går vidare" per grupp (samma två anrop som PrecisionStartListController.BuildQualifyingSnapshotResponse).
                        var card = rankings.Select(r =>
                        {
                            var cutoff = qualification.CalculateQualificationCutoff(r.QualifiedShooters.Count);
                            return new
                            {
                                Group = r.ChampionshipClass,
                                Total = r.QualifiedShooters.Count,
                                Cutoff = cutoff,
                                Finalists = HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsStartListBuilder.FinalistsWithTies(
                                    r.QualifiedShooters.Select(s => s.QualificationScore).ToList(), cutoff)
                            };
                        }).ToList();

                        // De LAGRADE finalisternas starter (det medaljlogiken läser) — nycklarna är "medlem|klass", så de görs om till objekt
                        // med MemberId (som Canonicalise hashar) i stället för en rå sträng med ett id i.
                        var stored = _precisionResults.GetFinalistStarts(id)
                            .Select(k => k.Split('|', 2))
                            .Select(p => new { MemberId = int.TryParse(p[0], out var m) ? m : 0, Class = p.Length > 1 ? p[1] : "" })
                            .OrderBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.MemberId)
                            .ToList();

                        payload = new
                        {
                            Rankings = rankings,
                            Preview = preview,
                            Card = card,
                            BuildMessage = built.Message,
                            BuiltTeams = built.Ok && built.Configuration?.Teams != null
                                ? built.Configuration.Teams.Select(t => new
                                {
                                    t.Label,
                                    Shooters = (t.Shooters ?? new()).Select(s => new { s.Position, s.MemberId, s.WeaponClass, s.QualificationRank, s.QualificationScore }).ToList()
                                }).ToList()
                                : null,
                            StoredFinalists = stored
                        };
                    }
                }
                catch (Exception ex)
                {
                    kind = "fel";
                    payload = new { exception = ex.GetType().Name };
                }

                var token = payload == null ? JValue.CreateNull() : JToken.FromObject(payload, JsonSerializer.CreateDefault());
                var canonical = Canonicalise(token, hmac);
                var json = canonical.ToString(Formatting.None);
                perCompetition[id.ToString()] = new
                {
                    kind,
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
                    json = full ? canonical : null
                };
            }

            var after = ReadWriteProof();
            return Content(JsonConvert.SerializeObject(new
            {
                header = new
                {
                    commit = cfg["Verify:Commit"] ?? Environment.GetEnvironmentVariable("HPSK_COMMIT"),
                    database = before.Count > 0 ? ReadDbName() : null,
                    mode = "finals",
                    competitions = idList.Count,
                    writeProofBefore = before,
                    writeProofAfter = after,
                    WROTE_SOMETHING = !before.SequenceEqual(after)
                },
                competitions = perCompetition
            }, Formatting.Indented), "application/json");
        }

        private IActionResult? VerifyGuard(string? confirm)
        {
            var sp = HttpContext.RequestServices;
            var cfg = sp.GetRequiredService<IConfiguration>();
            var env = sp.GetRequiredService<IWebHostEnvironment>();

            // Alla avslag = 404 utan förklaring i svaret; orsaken loggas (bara i DEBUG-bygget).
            IActionResult Refuse(string why)
            {
                _logger.LogWarning("VerifyRecompute avvisad: {Why}", why);
                return NotFound();
            }

            if (!string.Equals(confirm, "golden-master", StringComparison.Ordinal)) return Refuse("confirm saknas/fel");
            if (!env.IsDevelopment()) return Refuse("inte Development");
            if (cfg["HPSK_VERIFY_ENDPOINTS"] != "1") return Refuse("HPSK_VERIFY_ENDPOINTS != 1");

            var allowed = (cfg["Verify:AllowedDatabases"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowed.Length == 0) return Refuse("Verify:AllowedDatabases tom");

            string catalog;
            try { catalog = new SqlConnectionStringBuilder(cfg.GetConnectionString("umbracoDbDSN") ?? "").InitialCatalog; }
            catch { return Refuse("anslutningssträngen gick inte att läsa"); }
            if (!allowed.Contains(catalog, StringComparer.OrdinalIgnoreCase)) return Refuse($"katalogen '{catalog}' ej tillåten");

            var actual = ReadDbName();
            if (!allowed.Contains(actual, StringComparer.OrdinalIgnoreCase)) return Refuse($"DB_NAME() '{actual}' ej tillåten");
            if (string.IsNullOrWhiteSpace(cfg["Verify:HashKey"])) return Refuse("Verify:HashKey saknas (user-secrets)");
            return null;
        }

        private string ReadDbName()
        {
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            return db.ExecuteScalar<string>("SELECT DB_NAME()") ?? "";
        }

        /// <summary>COUNT + CHECKSUM_AGG per tabell — en skrivning syns även när antalet står still.</summary>
        private List<string> ReadWriteProof()
        {
            var list = new List<string>();
            using var db = _umbracoDatabaseFactory.CreateDatabase();
            foreach (var t in WriteProofTables)
            {
                try
                {
                    var row = db.Fetch<dynamic>($"SELECT COUNT(*) AS N, CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS C FROM dbo.[{t}]").First();
                    list.Add($"{t}: {row.N} / {row.C}");
                }
                catch { list.Add($"{t}: (saknas)"); }
            }
            return list;
        }

        /// <summary>Sorterar nycklar, maskar tid, hashar medlems-id (HMAC) och tar bort namn — tal rörs aldrig.</summary>
        private static JToken Canonicalise(JToken t, HMACSHA256 hmac)
        {
            switch (t)
            {
                case JObject o:
                    var res = new JObject();
                    foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (IsNameProperty(p.Name)) { res[p.Name] = "<namn>"; continue; }
                        if (IsTimeProperty(p.Name) && p.Value.Type is JTokenType.Date or JTokenType.String) { res[p.Name] = "<tid>"; continue; }
                        if (IsMemberIdProperty(p.Name) && p.Value.Type == JTokenType.Integer)
                        {
                            var h = hmac.ComputeHash(Encoding.UTF8.GetBytes(p.Value.ToString()));
                            res[p.Name] = "m:" + Convert.ToHexString(h, 0, 6);
                            continue;
                        }
                        res[p.Name] = Canonicalise(p.Value, hmac);
                    }
                    return res;
                case JArray a:
                    return new JArray(a.Select(x => Canonicalise(x, hmac)));
                default:
                    return t.DeepClone();
            }
        }
    }
}
#endif
