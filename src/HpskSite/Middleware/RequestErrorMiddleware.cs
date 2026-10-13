using HpskSite.Services.Ops;

namespace HpskSite.Middleware
{
    /// <summary>
    /// C3-planen F6: räknar varje svar ≥ 400 i <see cref="RequestErrorCounter"/>. Skriver ingenting själv —
    /// <see cref="RequestErrorFlushService"/> gör det en gång i minuten — så en felstorm kostar inga
    /// databasanrop i själva begäran.
    ///
    /// <para>Ett undantag som går förbi räknas som 500 och kastas vidare oförändrat; den här middlewaren får
    /// aldrig ändra vad användaren ser.</para>
    /// </summary>
    public class RequestErrorMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly RequestErrorCounter _counter;

        public RequestErrorMiddleware(RequestDelegate next, RequestErrorCounter counter)
        {
            _next = next;
            _counter = counter;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch
            {
                Record(context, 500);
                throw;
            }
            if (context.Response.StatusCode >= 400) Record(context, context.Response.StatusCode);
        }

        private void Record(HttpContext context, int status)
        {
            try
            {
                var bot = VisitorTrackingMiddleware.IsBotUserAgent(context.Request.Headers.UserAgent.ToString());
                _counter.Record(status, context.Request.Path.Value, bot, DateTime.Now);
            }
            catch
            {
                // Räknaren får aldrig fälla en begäran.
            }
        }
    }
}
