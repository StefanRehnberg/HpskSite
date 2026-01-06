using Microsoft.Maui.Graphics.Platform;
using MauiGraphics = Microsoft.Maui.Graphics;

namespace HpskSite.Mobile.Services;

/// <summary>
/// Service for compressing images before upload.
/// Resizes large camera photos (10-20MB) to a reasonable size (~300KB).
/// </summary>
public class ImageCompressionService
{
    private const int MaxDimension = 1200;
    private const float JpegQuality = 0.80f;

    /// <summary>
    /// Compresses an image file to a smaller size suitable for upload.
    /// </summary>
    /// <param name="filePath">Path to the original image file</param>
    /// <returns>Compressed image as byte array (JPEG format)</returns>
    public async Task<byte[]> CompressImageAsync(string filePath)
    {
        try
        {
            // Read the original file
            using var fileStream = File.OpenRead(filePath);

            // Load the image using platform-specific loader
            var image = await LoadImageAsync(fileStream);
            if (image == null)
            {
                // Fallback: just return the original file bytes
                return await File.ReadAllBytesAsync(filePath);
            }

            // Calculate new dimensions
            var (newWidth, newHeight) = CalculateDimensions((int)image.Width, (int)image.Height);

            // If already small enough, just encode to JPEG
            MauiGraphics.IImage resizedImage;
            if (newWidth == (int)image.Width && newHeight == (int)image.Height)
            {
                resizedImage = image;
            }
            else
            {
                // Resize the image
                resizedImage = image.Downsize(newWidth, newHeight);
            }

            // Encode to JPEG
            using var outputStream = new MemoryStream();
            await resizedImage.SaveAsync(outputStream, MauiGraphics.ImageFormat.Jpeg, JpegQuality);

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image compression failed: {ex.Message}");
            // Fallback: return original file
            return await File.ReadAllBytesAsync(filePath);
        }
    }

    /// <summary>
    /// Compresses an image from a stream.
    /// </summary>
    /// <param name="stream">Stream containing the image data</param>
    /// <returns>Compressed image as byte array (JPEG format)</returns>
    public async Task<byte[]> CompressImageAsync(Stream stream)
    {
        try
        {
            var image = await LoadImageAsync(stream);
            if (image == null)
            {
                // Fallback: just return the stream bytes
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }

            // Calculate new dimensions
            var (newWidth, newHeight) = CalculateDimensions((int)image.Width, (int)image.Height);

            // Resize if needed
            MauiGraphics.IImage resizedImage;
            if (newWidth == (int)image.Width && newHeight == (int)image.Height)
            {
                resizedImage = image;
            }
            else
            {
                resizedImage = image.Downsize(newWidth, newHeight);
            }

            // Encode to JPEG
            using var outputStream = new MemoryStream();
            await resizedImage.SaveAsync(outputStream, MauiGraphics.ImageFormat.Jpeg, JpegQuality);

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image compression failed: {ex.Message}");

            // Fallback: return stream bytes
            stream.Position = 0;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    private async Task<MauiGraphics.IImage?> LoadImageAsync(Stream stream)
    {
        try
        {
            // Use platform-specific image loading
            return await Task.Run(() => PlatformImage.FromStream(stream));
        }
        catch
        {
            return null;
        }
    }

    private (int width, int height) CalculateDimensions(int originalWidth, int originalHeight)
    {
        // If both dimensions are within limit, keep original
        if (originalWidth <= MaxDimension && originalHeight <= MaxDimension)
        {
            return (originalWidth, originalHeight);
        }

        // Calculate scale factor based on the larger dimension
        double ratio = Math.Min(
            (double)MaxDimension / originalWidth,
            (double)MaxDimension / originalHeight
        );

        var newWidth = (int)Math.Round(originalWidth * ratio);
        var newHeight = (int)Math.Round(originalHeight * ratio);

        return (newWidth, newHeight);
    }
}
