using System.ComponentModel;
using System.IO.Compression;
using ImageCrop.Core;
using ModelContextProtocol.Server;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using QRCoder;

namespace ImageCrop.Console;

/// <summary>
/// MCP 工具集。Claude / Cursor 等客户端通过 stdio 调用这些方法。
/// </summary>
[McpServerToolType]
public static class ImageTools
{
    static string EnsureOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ImageStudio-MCP");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string UniquePath(string dir, string ext)
    {
        return Path.Combine(dir, $"{Guid.NewGuid():N}.{ext.TrimStart('.')}");
    }

    [McpServerTool, Description("Crop an image to a given aspect ratio (e.g. 1:1, 16:9, 4:3) or 'half' for left/right half. Anchor: Center/Left/Right.")]
    public static async Task<string> CropImage(
        [Description("Absolute path to the input image file")] string inputPath,
        [Description("Aspect ratio like 1:1, 16:9, 4:3, or 'half'")] string ratio = "1:1",
        [Description("Anchor mode: Center, Left, or Right")] string anchor = "Center",
        [Description("Output format: jpg, png, webp, bmp")] string format = "jpg")
    {
        if (!File.Exists(inputPath))
            return $"Error: file not found: {inputPath}";

        if (!Enum.TryParse<AnchorMode>(anchor, true, out var anchorMode))
            anchorMode = AnchorMode.Center;

        using var image = await Image.LoadAsync(inputPath);
        using var result = image.CropWithAnchor(ratio, anchorMode);

        var dir = EnsureOutputDir();
        var outPath = UniquePath(dir, format);
        var (encoder, _) = ImageSharpExtensions.GetEncoder(format);
        await result.SaveAsync(outPath, encoder);
        return outPath;
    }

    [McpServerTool, Description("Scale an image so its longest side equals the given size (e.g. 256, 512, 1024).")]
    public static async Task<string> ScaleImage(
        [Description("Absolute path to the input image file")] string inputPath,
        [Description("Target max side length in pixels")] int maxSide = 512,
        [Description("Output format: jpg, png, webp, bmp. Empty = keep original")] string format = "")
    {
        if (!File.Exists(inputPath))
            return $"Error: file not found: {inputPath}";

        if (maxSide < 1) maxSide = 512;

        using var image = await Image.LoadAsync(inputPath);
        using var result = image.ScaleToMaxSide(maxSide);

        if (string.IsNullOrWhiteSpace(format))
            format = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant() switch
            {
                "png" => "png",
                "webp" => "webp",
                "bmp" => "bmp",
                _ => "jpg"
            };

        var dir = EnsureOutputDir();
        var outPath = UniquePath(dir, format);
        var (encoder, _) = ImageSharpExtensions.GetEncoder(format);
        await result.SaveAsync(outPath, encoder);
        return outPath;
    }

    [McpServerTool, Description("Convert an image to another format (png/jpg/webp/bmp).")]
    public static async Task<string> ConvertImage(
        [Description("Absolute path to the input image file")] string inputPath,
        [Description("Target format: png, jpg, webp, bmp")] string format = "png")
    {
        if (!File.Exists(inputPath))
            return $"Error: file not found: {inputPath}";

        using var image = await Image.LoadAsync(inputPath);
        var dir = EnsureOutputDir();
        var outPath = UniquePath(dir, format);
        var (encoder, _) = ImageSharpExtensions.GetEncoder(format);
        await image.SaveAsync(outPath, encoder);
        return outPath;
    }

    [McpServerTool, Description("Generate a QR code image from text.")]
    public static async Task<string> GenerateQrCode(
        [Description("Text or URL to encode")] string text,
        [Description("Output size in pixels (128-2000)")] int size = 512,
        [Description("ECC level: L, M, Q, H")] string ecc = "Q")
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text is required";

        var eccLevel = ecc.ToUpperInvariant() switch
        {
            "L" => QRCodeGenerator.ECCLevel.L,
            "M" => QRCodeGenerator.ECCLevel.M,
            "H" => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.Q
        };

        using var qr = ImageSharpExtensions.GenerateQRCode(text, size, eccLevel);
        var dir = EnsureOutputDir();
        var outPath = UniquePath(dir, "png");
        await qr.SaveAsPngAsync(outPath);
        return outPath;
    }

    [McpServerTool, Description("Split a grid/sprite sheet image into individual cells, optionally remove background, and return a zip path.")]
    public static async Task<string> GridSplit(
        [Description("Absolute path to the input image (sprite sheet / grid)")] string inputPath,
        [Description("Force number of rows (0 = auto detect)")] int rows = 0,
        [Description("Force number of columns (0 = auto detect)")] int cols = 0,
        [Description("Scale each cell so max side = this value (0 = no scale)")] int scale = 0,
        [Description("Remove background (matte)")] bool matte = true,
        [Description("Background detection threshold")] int threshold = 24,
        [Description("Name prefix for files inside the zip")] string namePrefix = "icon")
    {
        if (!File.Exists(inputPath))
            return $"Error: file not found: {inputPath}";

        int? forceRows = rows > 0 ? Math.Clamp(rows, 1, 20) : null;
        int? forceCols = cols > 0 ? Math.Clamp(cols, 1, 20) : null;

        using var image = await Image.LoadAsync(inputPath);
        List<GridCell> cells;
        int detectedRows, detectedCols;
        string mode;
        Rgba32 bg;
        try
        {
            (cells, detectedRows, detectedCols, mode, bg) =
                ImageSharpExtensions.DetectGrid(image, forceRows, forceCols, threshold);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }

        var dir = EnsureOutputDir();
        var zipPath = UniquePath(dir, "zip");

        await using (var zipFs = File.Create(zipPath))
        using (var archive = new ZipArchive(zipFs, ZipArchiveMode.Create))
        {
            int index = 1;
            foreach (var cell in cells)
            {
                using var extracted = image.Clone(ctx => ctx.Crop(new Rectangle(cell.X, cell.Y, cell.Width, cell.Height)));
                using var trimmed = ImageSharpExtensions.TrimBackground(extracted, bg, threshold);

                Image output = matte
                    ? ImageSharpExtensions.RemoveBackground(trimmed, bg)
                    : trimmed.Clone(_ => { });

                if (scale > 0)
                {
                    var scaled = output.ScaleToMaxSide(scale);
                    if (output != trimmed) output.Dispose();
                    output = scaled;
                }

                var entry = archive.CreateEntry($"{namePrefix}_{index:D2}.png", CompressionLevel.Optimal);
                await using (var entryStream = entry.Open())
                {
                    await output.SaveAsPngAsync(entryStream);
                }

                if (output != trimmed) output.Dispose();
                index++;
            }
        }

        return $"zip={zipPath}; mode={mode}; rows={detectedRows}; cols={detectedCols}; cells={cells.Count}";
    }
}
