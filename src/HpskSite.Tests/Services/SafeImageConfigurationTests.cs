using FluentAssertions;
using HpskSite.Services.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Den säkra ImageSharp-konfigurationen ska läsa vanliga bilder men vägra TIFF — även en TIFF som
    /// döpts till .jpg, eftersom ImageSharp känner igen formatet på innehållet.
    /// </summary>
    public class SafeImageConfigurationTests
    {
        private static DecoderOptions Safe => new() { Configuration = SafeImageConfiguration.Instance };

        private static MemoryStream Encode(Action<Image<Rgba32>, Stream> save)
        {
            using var img = new Image<Rgba32>(4, 4);
            var ms = new MemoryStream();
            save(img, ms);
            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void Tiff_vagras()
        {
            using var tiff = Encode((i, s) => i.SaveAsTiff(s));
            var act = () => Image.Load(Safe, tiff);
            act.Should().Throw<UnknownImageFormatException>();
        }

        [Fact]
        public void Kontrollprov_standardkonfigurationen_laser_tiff()
        {
            // Utan det här provet kunde Tiff_vagras vara grönt för att fixturen inte är en TIFF alls.
            using var tiff = Encode((i, s) => i.SaveAsTiff(s));
            using var img = Image.Load(tiff);
            img.Width.Should().Be(4);
        }

        [Fact]
        public void Jpeg_png_gif_webp_bmp_lases()
        {
            var savers = new Action<Image<Rgba32>, Stream>[]
            {
                (i, s) => i.SaveAsJpeg(s), (i, s) => i.SaveAsPng(s), (i, s) => i.SaveAsGif(s),
                (i, s) => i.SaveAsWebp(s), (i, s) => i.SaveAsBmp(s),
            };
            foreach (var save in savers)
            {
                using var ms = Encode(save);
                using var img = Image.Load(Safe, ms);
                img.Width.Should().Be(4);
            }
        }
    }
}
