using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Umbraco.Cms.Web.Common.Controllers;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Markerar en action som MEDVETET är öppen för anonyma anrop (säkerhetsplanen 2026-11-02, release 1c).
    /// Skälet är obligatoriskt: det är det en granskare läser. Samma betydelse som <c>[AllowAnonymous]</c>, men
    /// syns i baslinjetestet som ett beslut — och används på actions som annars bara saknar markering.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class PublicEndpointAttribute : Attribute
    {
        public PublicEndpointAttribute(string reason) => Reason = reason;
        public string Reason { get; }
    }

    /// <summary>
    /// Markerar en action som kontrollerar inloggning/behörighet i sin egen kod (det vanliga i sajten) — alltså inte
    /// öppen, men inte heller grindad av ett attribut. Skälet anger var kontrollen sitter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class InCodeAuthAttribute : Attribute
    {
        public InCodeAuthAttribute(string where) => Where = where;
        public string Where { get; }
    }

    public enum EndpointAuthKind
    {
        /// <summary>Ingen markering: ingen vet om anonyma får anropa den.</summary>
        Unclassified,
        Authorize,
        AllowAnonymous,
        PublicEndpoint,
        InCodeAuth,
        /// <summary>Sidrendering (RenderController) — öppen av konstruktion; åtkomst styrs av Umbracos publika åtkomst.</summary>
        PageRender
    }

    /// <summary>EN regel för hur en action klassas — används av filtret (körtid) och baslinjetestet (statiskt).</summary>
    public static class EndpointAuthClassifier
    {
        public static EndpointAuthKind Classify(MethodInfo action)
        {
            var type = action.DeclaringType!;
            if (typeof(RenderController).IsAssignableFrom(type)) return EndpointAuthKind.PageRender;
            if (Has<AllowAnonymousAttribute>(action) || Has<AllowAnonymousAttribute>(type)) return EndpointAuthKind.AllowAnonymous;
            if (Has<PublicEndpointAttribute>(action) || Has<PublicEndpointAttribute>(type)) return EndpointAuthKind.PublicEndpoint;
            if (Has<AuthorizeAttribute>(action) || Has<AuthorizeAttribute>(type)) return EndpointAuthKind.Authorize;
            if (Has<InCodeAuthAttribute>(action) || Has<InCodeAuthAttribute>(type)) return EndpointAuthKind.InCodeAuth;
            return EndpointAuthKind.Unclassified;
        }

        /// <summary>Alla publika actions i sajtens egna controllers (inte Umbracos).</summary>
        public static IEnumerable<MethodInfo> OwnActions(Assembly assembly)
        {
            foreach (var t in assembly.GetTypes())
            {
                if (!t.IsClass || t.IsAbstract || !t.IsPublic) continue;
                if (!typeof(ControllerBase).IsAssignableFrom(t)) continue;
                if (Has<NonControllerAttribute>(t)) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.IsSpecialName || Has<NonActionAttribute>(m)) continue;
                    yield return m;
                }
            }
        }

        public static string Key(MethodInfo m) => $"{m.DeclaringType!.Name}.{m.Name}";

        private static bool Has<T>(MemberInfo m) where T : Attribute => m.GetCustomAttributes(typeof(T), inherit: true).Length > 0;
    }
}
