using HpskSite.Services;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registrerar startkontrollen för ekonomifrågorna (<c>/ekonomifragor</c>).
    ///
    /// <para>Sidan har ingen egen tjänst — controllern skriver direkt via <c>IScopeProvider</c> —
    /// så det enda som behöver registreras är guarden. Se
    /// <see cref="EkonomiEnkatSchemaGuardHostedService"/> för varför den finns.</para>
    /// </summary>
    public class EkonomiEnkatComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddHostedService<EkonomiEnkatSchemaGuardHostedService>();
        }
    }
}
