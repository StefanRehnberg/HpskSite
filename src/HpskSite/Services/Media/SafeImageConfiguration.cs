using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Web.Middleware;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services.Media
{
    /// <summary>
    /// <b>ImageSharp utan TIFF</b> (2026-10-20). ImageSharp 3.1.12 har kända hål i TIFF-avkodaren — bl.a.
    /// GHSA-wmxv-xphr-5c9g, där en BigTIFF med en IFD-slinga får en tråd att hänga redan när filen läses — och de
    /// rättas bara i 4.x, som Umbraco 17 inte stödjer. Uppladdningarna godkänner bara .jpg/.png/.gif/.webp, men
    /// ImageSharp känner igen formatet på INNEHÅLLET: en BigTIFF som döpts till .jpg hade avkodats ändå.
    ///
    /// <para>Konfigurationen känner bara JPEG, PNG, GIF, WebP och BMP. Den används av Umbracos bildmiddleware
    /// (/media med storleksparametrar), av Umbracos måttläsning vid uppladdning (DI) och av
    /// <see cref="ImageResizeService"/>. En TIFF ger då "okänt format" i stället för att läsas.</para>
    /// </summary>
    public static class SafeImageConfiguration
    {
        public static Configuration Instance { get; } = new Configuration(
            new JpegConfigurationModule(),
            new PngConfigurationModule(),
            new GifConfigurationModule(),
            new WebpConfigurationModule(),
            new BmpConfigurationModule());
    }

    [ComposeAfter(typeof(Umbraco.Cms.Imaging.ImageSharp.ImageSharpComposer))]
    public class SafeImageConfigurationComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Den sista registreringen vinner när Configuration hämtas ur DI (Umbracos måttläsning).
            builder.Services.AddSingleton(SafeImageConfiguration.Instance);
            // Efter Umbracos egen inställning av middlewaren.
            builder.Services.PostConfigure<ImageSharpMiddlewareOptions>(o => o.Configuration = SafeImageConfiguration.Instance);
        }
    }
}
