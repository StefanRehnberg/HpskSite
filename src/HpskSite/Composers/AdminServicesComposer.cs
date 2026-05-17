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

            // Manual klubb-/kretsmästare entries (auto-compute approach abandoned —
            // many clubs don't run results through pistol.nu)
            builder.Services.AddScoped<CompetitionChampionsService>();

            // Fältskytte member-stats aggregator (powers /user-profile-page dashboard + Resultat tab)
            builder.Services.AddScoped<FaltskytteStatsService>();

            // Cheap "does member X have data in discipline Y" lookups for member-list dots and mini-dashboard tabs
            builder.Services.AddScoped<MemberDataPresenceService>();

            // Register BrevoEmailService and named HttpClient
            builder.Services.AddHttpClient("Brevo");
            builder.Services.AddScoped<BrevoEmailService>();

            // Configure document archive options from appsettings.json
            builder.Services.Configure<DocumentArchiveOptions>(
                builder.Config.GetSection("DocumentArchive"));
        }
    }
}
