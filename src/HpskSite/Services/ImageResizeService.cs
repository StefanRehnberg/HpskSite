using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace HpskSite.Services;

/// <summary>
/// Service for resizing images for target photo uploads.
/// Reads configuration from appsettings.json TargetPhotos section.
/// </summary>
public class ImageResizeService
{
    private readonly int _maxDimension;
    private readonly int _jpegQuality;
    private readonly ILogger<ImageResizeService> _logger;

    public ImageResizeService(IConfiguration configuration, ILogger<ImageResizeService> logger)
    {
        _maxDimension = configuration.GetValue("TargetPhotos:MaxDimensionPx", 1200);
        _jpegQuality = configuration.GetValue("TargetPhotos:JpegQuality", 80);
        _logger = logger;
    }

    /// <summary>
    /// Resizes an image to fit within the maximum dimension while maintaining aspect ratio.
    /// Outputs as JPEG with configured quality.
    /// </summary>
    /// <param name="inputStream">Input image stream (any format supported by ImageSharp)</param>
    /// <returns>Resized image as JPEG byte array</returns>
    public async Task<byte[]> ResizeImageAsync(Stream inputStream)
    {
        using var image = await Image.LoadAsync(inputStream);

        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Calculate new dimensions maintaining aspect ratio
        var (newWidth, newHeight) = CalculateDimensions(originalWidth, originalHeight);

        // Only resize if needed
        if (newWidth != originalWidth || newHeight != originalHeight)
        {
            _logger.LogInformation("Resizing image from {OriginalWidth}x{OriginalHeight} to {NewWidth}x{NewHeight}",
                originalWidth, originalHeight, newWidth, newHeight);

            image.Mutate(x => x.Resize(newWidth, newHeight));
        }
        else
        {
            _logger.LogInformation("Image {Width}x{Height} already within max dimension {MaxDimension}, no resize needed",
                originalWidth, originalHeight, _maxDimension);
        }

        // Encode as JPEG with configured quality
        using var outputStream = new MemoryStream();
        var encoder = new JpegEncoder
        {
            Quality = _jpegQuality
        };

        await image.SaveAsJpegAsync(outputStream, encoder);

        var result = outputStream.ToArray();
        _logger.LogInformation("Image processed: {InputSize} bytes -> {OutputSize} bytes",
            inputStream.Length, result.Length);

        return result;
    }

    /// <summary>
    /// Calculates new dimensions to fit within max dimension while maintaining aspect ratio.
    /// </summary>
    private (int width, int height) CalculateDimensions(int originalWidth, int originalHeight)
    {
        // If both dimensions are within limit, keep original
        if (originalWidth <= _maxDimension && originalHeight <= _maxDimension)
        {
            return (originalWidth, originalHeight);
        }

        // Calculate scale factor based on the larger dimension
        double ratio = Math.Min(
            (double)_maxDimension / originalWidth,
            (double)_maxDimension / originalHeight
        );

        var newWidth = (int)Math.Round(originalWidth * ratio);
        var newHeight = (int)Math.Round(originalHeight * ratio);

        return (newWidth, newHeight);
    }
}
