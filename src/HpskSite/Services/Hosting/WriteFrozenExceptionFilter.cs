using HpskSite.Services.Events;
using HpskSite.Services.Registrations;
using HpskSite.Services.Results;
using HpskSite.Services.StartLists;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Gör om ett frysundantag från lagringarna (underhållsfönstret för anmälningar, händelser,
    /// resultatlistor och startlistor) till ett vanligt svar med meddelandet om underhåll.
    ///
    /// <para>Lagringarna kastar när skrivningarna är frysta. Många endpoints saknar egen felhantering, och
    /// utan filtret blev frysningen ett 500-svar — som läses som att sajten gått sönder, inte som att den
    /// uppdateras några minuter. Svaret bär båda skiftlägena eftersom klienterna läser olika.</para>
    /// </summary>
    public class WriteFrozenExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            if (context.ExceptionHandled) return;
            if (context.Exception is not (StartListWriteFrozenException or CompetitionResultWriteFrozenException
                or RegistrationWriteFrozenException or ClubEventWriteFrozenException)) return;

            var message = context.Exception.Message;
            // En ordbok, inte ett anonymt objekt: MVC gör egenskapsnamn till camelCase, och då krockar
            // "success" och "Success" (500). Ordbokens nycklar behålls som de står, så båda läsarna får sitt.
            context.Result = new JsonResult(new Dictionary<string, object>
            {
                ["success"] = false, ["Success"] = false, ["message"] = message, ["Message"] = message, ["maintenance"] = true
            });
            context.ExceptionHandled = true;
        }
    }
}
