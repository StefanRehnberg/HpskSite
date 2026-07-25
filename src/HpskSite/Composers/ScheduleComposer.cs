using HpskSite.Services.Schedule;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers the personal-itinerary ("Mitt schema") stack: the fan-out service every surface reads
    /// from, the home-page summary, the day-programme CRUD, the calendar export, and the background
    /// sweep that sends start-time reminders.
    /// </summary>
    public class ScheduleComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Scoped — reads registrations via the scoped ParticipantAudienceResolver.
            builder.Services.AddScoped<MyScheduleService>();
            builder.Services.AddScoped<ScheduleHubService>();
            builder.Services.AddScoped<CompetitionAgendaService>();
            // Stateless string building.
            builder.Services.AddSingleton<ScheduleIcsBuilder>();

            builder.Services.AddHostedService<ScheduleReminderHostedService>();
        }
    }
}
