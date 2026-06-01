using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using HpskSite.Services;
using HpskSite.Models.Configuration;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers admin-related services for dependency injection
    /// </summary>
    public class AdminServicesComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register AdminAuthorizationService as scoped (required because IMemberManager is scoped)
            builder.Services.AddScoped<AdminAuthorizationService>();

            // Register UnifiedResultsService as scoped for aggregating results from multiple sources
            builder.Services.AddScoped<UnifiedResultsService>();

            // Register MemberActivityService as scoped (static cache still shared, but avoids DI lifetime issues)
            builder.Services.AddScoped<MemberActivityService>();

            // Configure member activity options from appsettings.json
            builder.Services.Configure<MemberActivityOptions>(
                builder.Config.GetSection("MemberActivity"));

            // Register TrainingGroupService as scoped
            builder.Services.AddScoped<TrainingGroupService>();

            // Register DocumentService as scoped (uses IScopeProvider)
            builder.Services.AddScoped<DocumentService>();

            // Register SeriesCalculationService as scoped
            builder.Services.AddScoped<SeriesCalculationService>();

            // Register CompetitionTeamService as scoped
            builder.Services.AddScoped<CompetitionTeamService>();

            // Register BoardRoleService as scoped
            builder.Services.AddScoped<BoardRoleService>();

            // Register ClubComparisonService as scoped (snapshot is cached via IMemoryCache)
            builder.Services.AddScoped<ClubComparisonService>();

            // Certification authority + writer for instructor / control roles
            builder.Services.AddScoped<CertificationAuthorizationService>();
            builder.Services.AddScoped<CertificationService>();

            // Klubb- och kretsrekord (manual record entry, IsCurrent + history chain)
            builder.Services.AddScoped<CompetitionRecordsService>();

            // Standardmedaljer ledger (per-discipline klass 3 qualification + pooled Guldmedalj accounting)
            builder.Services.AddScoped<StandardMedalLedgerService>();

            // Standardmedalj proof files (PDF/image) stored under App_Data, served via authorized endpoint
            builder.Services.AddScoped<StandardMedalProofStorage>();

            // Materializes won Standard medals from our own competitions into the ledger on publish
            builder.Services.AddScoped<StandardMedalMaterializationService>();

            // Märken (marksmanship proficiency badges, SHB kap 5) — Phase 1: Pistolskyttemärket.
            // Ledger (badges + yearly Guldfodringar + årtalsmärke derivation) + candidate engine
            // (proposes Guldfodring parts from TrainingScores; never writes).
            builder.Services.AddScoped<MarkenLedgerService>();
            builder.Services.AddScoped<MarkenCandidateService>();
            // Phase 2: competition-driven discipline märken (Precision/Fält/Milsnabb/NatHelmatch) —
            // harvests hosted results live + merges verified self-reports; evaluates valör + årtalsmärke.
            builder.Services.AddScoped<MarkenCompetitionService>();
            // Phase 3: Stormästarmärket inteckningspoäng entries (career championship merits).
            builder.Services.AddScoped<MarkenStormastarService>();

            // Manual klubb-/kretsmästare entries (auto-compute approach abandoned —
            // many clubs don't run results through pistol.nu)
            builder.Services.AddScoped<CompetitionChampionsService>();

            // Fältskytte member-stats aggregator (powers /user-profile-page dashboard + Resultat tab)
            builder.Services.AddScoped<FaltskytteStatsService>();

            // Cheap "does member X have data in discipline Y" lookups for member-list dots and mini-dashboard tabs
            builder.Services.AddScoped<MemberDataPresenceService>();

            // Särskjutning (shoot-off) entries for tied medal positions in championship competitions
            builder.Services.AddScoped<ShootOffService>();

            // Fältskytte (Normal/Poäng/Magnumfält) Särskjutning — separate service since Fältskytte
            // uses a different result-entry shape (per-station hits/figures/poängmål)
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteShootOffService>();

            // Standalone Fältskytte station configurations (CRUD + sharing + secrecy gate)
            builder.Services.AddScoped<FaltskytteConfigurationService>();

            // Precision finals start list pipeline:
            //   QualificationService — ranks shooters and computes the 1/6+min10 cutoff (existed before, now DI-registered)
            //   QualifyingResultsService — snapshot the qualifying leaderboard before finals are built
            //   FinalsStartListBuilder — turn snapshot + per-class config into a finals StartListConfiguration
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsQualificationService>();
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionQualifyingResultsService>();
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsStartListBuilder>();

            // Register BrevoEmailService and named HttpClient
            builder.Services.AddHttpClient("Brevo");
            builder.Services.AddScoped<BrevoEmailService>();

            // Configure document archive options from appsettings.json
            builder.Services.Configure<DocumentArchiveOptions>(
                builder.Config.GetSection("DocumentArchive"));
        }
    }
}
