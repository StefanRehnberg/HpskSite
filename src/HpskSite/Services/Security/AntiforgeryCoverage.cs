using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vilka actions som skriver (POST/PUT/PATCH/DELETE) utan att något prövar en antiforgery-token. EN regel för filtret
    /// (<see cref="AntiforgeryLogFilter"/>) och baslinjetestet (<c>AntiforgeryCoverageTests</c>).
    /// </summary>
    public static class AntiforgeryCoverage
    {
        /// <summary>
        /// Actionen KAN anropas med ett skrivande verb: antingen anges ett uttryckligen, eller så anges inget alls — då svarar den på
        /// alla verb, POST inräknat (så är de flesta av sajtens Surface-actions skrivna).
        /// </summary>
        public static bool IsUnsafeVerb(MethodInfo m)
        {
            var providers = m.GetCustomAttributes(true).OfType<IActionHttpMethodProvider>().ToList();
            if (providers.Count == 0) return true;
            return providers.Any(p => p.HttpMethods.Any(v => v is "POST" or "PUT" or "PATCH" or "DELETE"));
        }

        /// <summary>Actionen (eller klassen) har ett attribut som prövar tokenen, eller en uttrycklig befrielse.</summary>
        public static bool HasDecision(MethodInfo m)
        {
            var t = m.DeclaringType!;
            return Has<ValidateAntiForgeryTokenAttribute>(m) || Has<ValidateAntiForgeryTokenAttribute>(t)
                || Has<AutoValidateAntiforgeryTokenAttribute>(m) || Has<AutoValidateAntiforgeryTokenAttribute>(t)
                || Has<IgnoreAntiforgeryTokenAttribute>(m) || Has<IgnoreAntiforgeryTokenAttribute>(t);
        }

        /// <summary>Skrivande actions i sajtens egna controllers utan beslut om antiforgery.</summary>
        public static IEnumerable<MethodInfo> Uncovered(Assembly assembly) =>
            EndpointAuthClassifier.OwnActions(assembly).Where(m => IsUnsafeVerb(m) && !HasDecision(m));

        private static bool Has<T>(MemberInfo m) where T : Attribute => m.GetCustomAttributes(typeof(T), true).Length > 0;
    }
}
