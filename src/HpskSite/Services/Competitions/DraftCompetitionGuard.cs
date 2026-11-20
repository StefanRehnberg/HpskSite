using System.Reflection;
using HpskSite.Models.Competitions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Markerar ett åtgärdsargument som bär ETT TÄVLINGS-id fast det inte heter <c>competitionId</c>
    /// (<c>id</c>, <c>compId</c>, <c>cid</c> …). <see cref="DraftCompetitionGuardFilter"/> prövar då argumentet.
    /// ⚠️ Gäller bara argument som ÄR en tävling; en <c>id</c> för något annat märks aldrig.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class CompetitionIdArgAttribute : Attribute { }

    /// <summary>
    /// Säger att åtgärdens tävlings-id medvetet inte prövas mot utkast, med skälet (spärrtestet
    /// <c>DraftCompetitionGuardTests</c> kräver antingen filtrets täckning eller den här markeringen).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DraftGuardOkAttribute : Attribute
    {
        public DraftGuardOkAttribute(string reason) => Reason = reason;
        public string Reason { get; }
    }

    /// <summary>
    /// De rena reglerna för vilka tävlings-id:n en åtgärd bär (kretsutlysningen F1, planen 4.2). Ett argument räknas
    /// som ett tävlings-id när det (1) heter <c>competitionId</c>, (2) är märkt <see cref="CompetitionIdArgAttribute"/>,
    /// eller (3) är ett objekt (en JSON-kropp) med en heltalsegenskap <c>CompetitionId</c>. Ruttvärdet och
    /// frågesträngen <c>competitionId</c> räknas också, för åtgärder som läser det ur <c>Request</c>.
    /// </summary>
    public static class DraftCompetitionGuardRules
    {
        public const string ArgName = "competitionId";

        public static bool IsCompetitionIdName(string? name) => string.Equals(name, ArgName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Har åtgärdens signatur något argument som ett tävlings-id kan läsas ur? (Spärrtestet och filtret delar regeln.)</summary>
        public static bool CarriesCompetitionId(MethodInfo action)
        {
            foreach (var p in action.GetParameters())
                if (IsIdParameter(p) || ObjectIdProperty(p.ParameterType) != null) return true;
            return false;
        }

        public static bool IsIdParameter(ParameterInfo p) =>
            (p.ParameterType == typeof(int) || p.ParameterType == typeof(int?))
            && (IsCompetitionIdName(p.Name) || p.GetCustomAttribute<CompetitionIdArgAttribute>() != null);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> PropertyCache = new();

        /// <summary>Egenskapen <c>CompetitionId</c> (int / int?) på en kroppstyp, eller null. Cachas per typ (reflektion vid varje förfrågan vore onödigt).</summary>
        public static PropertyInfo? ObjectIdProperty(Type type) => PropertyCache.GetOrAdd(type, FindObjectIdProperty);

        private static PropertyInfo? FindObjectIdProperty(Type type)
        {
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum || type.IsValueType && Nullable.GetUnderlyingType(type) != null) return null;
            if (type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return null;
            var prop = type.GetProperty("CompetitionId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0
                   && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(int?)) ? prop : null;
        }

        /// <summary>Tävlings-id:n som åtgärdens argument bär (positiva, utan dubbletter).</summary>
        public static IReadOnlyList<int> IdsOf(ControllerActionDescriptor descriptor, IDictionary<string, object?> args,
            IReadOnlyDictionary<string, string?>? routeAndQuery = null)
        {
            var ids = new HashSet<int>();
            var parameters = descriptor.MethodInfo.GetParameters();
            foreach (var p in parameters)
            {
                if (p.Name == null || !args.TryGetValue(p.Name, out var value) || value == null) continue;
                if (IsIdParameter(p)) { Add(ids, value); continue; }
                var prop = ObjectIdProperty(p.ParameterType);
                if (prop != null) Add(ids, prop.GetValue(value));
            }
            if (routeAndQuery != null && !parameters.Any(IsIdParameter)
                && routeAndQuery.TryGetValue(ArgName, out var raw) && int.TryParse(raw, out var fromRequest))
                ids.Add(fromRequest);
            ids.RemoveWhere(i => i <= 0);
            return ids.ToList();
        }

        private static void Add(HashSet<int> ids, object? value)
        {
            if (value is int i) ids.Add(i);
        }
    }

    /// <summary>
    /// <b>Vem får se en dold tävling?</b> Arrangörens personal (<c>HasCompetitionStaffAccessAsync</c>: sajtadmin,
    /// tävlingens egen ledning, värdklubbens admin och skjutledare, värdkretsens admin, bemanningens appbehörighet).
    /// Alla andra — anonyma, skyttar, andra klubbars admin, andra kretsar — ska få svaret "finns inte".
    /// </summary>
    public class DraftCompetitionAccess
    {
        private readonly ICompetitionStore _competitions;
        private readonly AdminAuthorizationService _auth;

        public DraftCompetitionAccess(ICompetitionStore competitions, AdminAuthorizationService auth)
        {
            _competitions = competitions;
            _auth = auth;
        }

        /// <summary>Sant när tävlingen är dold OCH den som frågar inte tillhör personalen: svara som om tävlingen inte fanns.</summary>
        public async Task<bool> MustHideFromViewerAsync(int competitionId)
        {
            if (competitionId <= 0 || !_competitions.IsDraft(competitionId)) return false;
            return !await _auth.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved);
        }
    }

    /// <summary>
    /// <b>Den centrala grinden</b> för dolda tävlingar (kretsutlysningen F1, planen 4.2): ETT globalt åtgärdsfilter i
    /// stället för ~100 enskilda kontroller. Varje åtgärd som bär ett tävlings-id (se
    /// <see cref="DraftCompetitionGuardRules"/>) svarar "Tävlingen finns inte" (404) på ett utkast, om inte den som frågar
    /// tillhör arrangörens personal. Ett nytt namn som glöms fäller spärrtestet <c>DraftCompetitionGuardTests</c>.
    /// </summary>
    public class DraftCompetitionGuardFilter : IAsyncActionFilter
    {
        public const string NotFoundMessage = "Tävlingen finns inte.";

        private readonly DraftCompetitionAccess _access;

        public DraftCompetitionGuardFilter(DraftCompetitionAccess access) => _access = access;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.ActionDescriptor is ControllerActionDescriptor descriptor
                && !IsMarkedOk(descriptor))
            {
                var request = context.HttpContext.Request;
                var fromRequest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (context.RouteData.Values.TryGetValue(DraftCompetitionGuardRules.ArgName, out var routed))
                    fromRequest[DraftCompetitionGuardRules.ArgName] = routed?.ToString();
                else if (request.Query.TryGetValue(DraftCompetitionGuardRules.ArgName, out var queried))
                    fromRequest[DraftCompetitionGuardRules.ArgName] = queried.ToString();

                foreach (var id in DraftCompetitionGuardRules.IdsOf(descriptor, context.ActionArguments, fromRequest))
                {
                    if (!await _access.MustHideFromViewerAsync(id)) continue;
                    context.Result = WantsJson(request)
                        ? new NotFoundObjectResult(new Dictionary<string, object>
                        {
                            ["success"] = false, ["Success"] = false, ["message"] = NotFoundMessage, ["Message"] = NotFoundMessage,
                        })
                        : new NotFoundResult();
                    return;
                }
            }
            await next();
        }

        /// <summary>
        /// En åtgärd (eller en hel controller) märkt <see cref="DraftGuardOkAttribute"/> prövar själv vem som får röra tävlingen och släpps förbi grinden —
        /// det är vad märkningen säger. Klubbprogrammet (F7) behöver det: medlemmar i klubben önskar tävlingsledare till en DOLD tävling.
        /// </summary>
        private static bool IsMarkedOk(ControllerActionDescriptor d) =>
            d.MethodInfo.GetCustomAttribute<DraftGuardOkAttribute>() != null
            || d.ControllerTypeInfo.GetCustomAttribute<DraftGuardOkAttribute>() != null;

        private static bool WantsJson(Microsoft.AspNetCore.Http.HttpRequest request) =>
            string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)
            || (request.Headers.Accept.ToString().Contains("json", StringComparison.OrdinalIgnoreCase)
                && !request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }
}
