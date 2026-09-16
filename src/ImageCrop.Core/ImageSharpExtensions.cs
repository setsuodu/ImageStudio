using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using QRCoder;

namespace ImageCrop.Core;

public record GridCell(int X, int Y, int Width, int Height);

public static class ImageSharpExtensions
{
    public static Image CropWithAnchor(this Image image, string ratio, AnchorMode anchor)
    {
        // === 新增：左右平分 ===
        if (ratio == "half")
        {
            return image.Clone(ctx =>
            {
                var size = ctx.GetCurrentSize();
                int halfWidth = size.Width / 2;

                int x = anchor switch
                {
                    AnchorMode.Left => 0,
                    AnchorMode.Right => size.Width - halfWidth,
                    _ => (size.Width - halfWidth) / 2   // 居中
                };

                ctx.Crop(new Rectangle(x, 0, halfWidth, size.Height));
            });
        }

        var parts = ratio.Split(':');
        float targetRatio = float.Parse(parts[0]) / float.Parse(parts[1]);

        return image.Clone(ctx =>
        {
            Size size = ctx.GetCurrentSize();
            int cropWidth, cropHeight;

            if (targetRatio < (float)size.Width / size.Height)
            {
                cropHeight = size.Height;
                cropWidth = (int)(size.Height * targetRatio);
            }
            else
            {
                cropWidth = size.Width;
                cropHeight = (int)(size.Width / targetRatio);
            }

            int x = anchor switch
            {
                AnchorMode.Left => 0,
                AnchorMode.Right => size.Width - cropWidth,
                _ => (size.Width - cropWidth) / 2
            };
            int y = (size.Height - cropHeight) / 2;

            ctx.Crop(new Rectangle(x, y, cropWidth, cropHeight));
        });
    }

    // 新增：按最长边缩放（16/32/64...1024 专用）
    public static Image ScaleToMaxSide(this Image image, int maxSide)
    {
        return image.Clone(ctx =>
        {
            var original = ctx.GetCurrentSize();
            int originalMax = Math.Max(original.Width, original.Height);

            if (originalMax == 0)
            {
                ctx.Resize(maxSide, maxSide, KnownResamplers.Lanczos3);
                return;
            }

            // 使用 double 提高精度 + 四舍五入
            double ratio = (double)maxSide / originalMax;

            int newWidth = (int)Math.Round(original.Width * ratio);
            int newHeight = (int)Math.Round(original.Height * ratio);

            // 兜底：强制让最长边正好等于 maxSide
            if (newWidth > newHeight)
                newWidth = maxSide;
            else
                newHeight = maxSide;

            ctx.Resize(newWidth, newHeight, KnownResamplers.Lanczos3);
        });
    }

    public static (IImageEncoder encoder, string contentType) GetEncoder(string format)
    {
        return format.ToLower() switch
        {
            "png" => (new PngEncoder(), "image/png"),
            "bmp" => (new BmpEncoder(), "image/bmp"),
            "webp" => (new WebpEncoder(), "image/webp"),
            _ => (new JpegEncoder(), "image/jpeg")
        };
    }

    /// <summary>
    /// 生成二维码（返回 ImageSharp Image）
    /// </summary>
    public static Image GenerateQRCode(
    string text,
    int targetSize = 512,
    QRCodeGenerator.ECCLevel eccLevel = QRCodeGenerator.ECCLevel.Q,
    string darkColor = "#000000",
    string lightColor = "#FFFFFF")
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("QR Code text cannot be empty");

        targetSize = Math.Clamp(targetSize, 128, 2000);

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(text, eccLevel);
        using var qrCode = new QRCode(qrCodeData);

        // 关键：严格按目标尺寸生成
        int pixelsPerModule = Math.Max(1, targetSize / (qrCodeData.ModuleMatrix.Count + 8));

        var qrImage = qrCode.GetGraphic(pixelsPerModule, darkColor, lightColor, true);

        // 最终强制缩放到精确尺寸
        if (qrImage.Width != targetSize || qrImage.Height != targetSize)
        {
            qrImage = qrImage.Clone(x => x.Resize(targetSize, targetSize, KnownResamplers.Lanczos3));
        }

        return qrImage;
    }

    /// <summary>
    /// 自动或手动识别网格。返回 cells + 行列数 + 模式 + 背景色。
    /// forceRows/forceCols 都有值时走手动等分，否则自动扫描沟槽。
    /// </summary>
    public static (List<GridCell> Cells, int Rows, int Cols, string Mode, Rgba32 Background) DetectGrid(
        Image image,
        int? forceRows = null,
        int? forceCols = null,
        int threshold = 24,
        float ratio = 0.97f)
    {
        using var rgba = image.CloneAs<Rgba32>();
        int width = rgba.Width;
        int height = rgba.Height;

        // 采样左上角背景色
        var bg = rgba[0, 0];

        if (forceRows is > 0 && forceCols is > 0)
        {
            int rows = forceRows.Value;
            int cols = forceCols.Value;
            int cellW = width / cols;
            int cellH = height / rows;
            var cells = new List<GridCell>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    cells.Add(new GridCell(c * cellW, r * cellH, cellW, cellH));
            return (cells, rows, cols, "manual", bg);
        }

        // ---- 自动模式：扫描背景沟槽 ----
        bool IsGutterRow(int y)
        {
            int match = 0;
            for (int x = 0; x < width; x++)
            {
                var p = rgba[x, y];
                if (ColorDistance(p, bg) <= threshold) match++;
            }
            return match / (float)width >= ratio;
        }

        bool IsGutterCol(int x)
        {
            int match = 0;
            for (int y = 0; y < height; y++)
            {
                var p = rgba[x, y];
                if (ColorDistance(p, bg) <= threshold) match++;
            }
            return match / (float)height >= ratio;
        }

        var rowGutter = new bool[height];
        for (int y = 0; y < height; y++) rowGutter[y] = IsGutterRow(y);

        var colGutter = new bool[width];
        for (int x = 0; x < width; x++) colGutter[x] = IsGutterCol(x);

        var rowBands = ExtractBands(rowGutter);
        var colBands = ExtractBands(colGutter);

        if (rowBands.Count == 0 || colBands.Count == 0)
            throw new InvalidOperationException(
                "未能自动识别出网格，可能背景不是纯色或间距不规则。请手动指定 rows/cols。");

        var autoCells = new List<GridCell>();
        foreach (var (ry0, ry1) in rowBands)
            foreach (var (cx0, cx1) in colBands)
                autoCells.Add(new GridCell(cx0, ry0, cx1 - cx0, ry1 - ry0));

        return (autoCells, rowBands.Count, colBands.Count, "auto", bg);
    }

    static double ColorDistance(Rgba32 a, Rgba32 b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    static List<(int start, int end)> ExtractBands(bool[] gutterFlags)
    {
        var bands = new List<(int, int)>();
        int start = -1;
        for (int i = 0; i < gutterFlags.Length; i++)
        {
            bool isContent = !gutterFlags[i];
            if (isContent && start == -1) start = i;
            else if (!isContent && start != -1)
            {
                bands.Add((start, i));
                start = -1;
            }
        }
        if (start != -1) bands.Add((start, gutterFlags.Length));
        return bands;
    }

    /// <summary>
    /// 去掉 cell 内多余的背景边距，让图标紧贴边界（类似 sharp.trim）。
    /// </summary>
    public static Image TrimBackground(Image image, Rgba32 bg, int threshold = 24)
    {
        using var rgba = image.CloneAs<Rgba32>();
        int w = rgba.Width, h = rgba.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (ColorDistance(rgba[x, y], bg) > threshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        // 整格都是背景，返回原图
        if (maxX < minX || maxY < minY)
            return image.Clone(_ => { });

        int tw = maxX - minX + 1;
        int th = maxY - minY + 1;
        return image.Clone(ctx => ctx.Crop(new Rectangle(minX, minY, tw, th)));
    }

    /// <summary>
    /// 软抠底色：inner 内全透明，outer 外保留，中间线性插值 alpha。
    /// 返回带透明通道的新图。
    /// </summary>
    public static Image RemoveBackground(Image image, Rgba32 bg, int innerThreshold = 20, int outerThreshold = 45)
    {
        var rgba = image.CloneAs<Rgba32>();
        rgba.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    double dist = ColorDistance(p, bg);
                    byte originalAlpha = p.A;
                    byte newAlpha;
                    if (dist <= innerThreshold)
                        newAlpha = 0;
                    else if (dist >= outerThreshold)
                        newAlpha = originalAlpha;
                    else
                    {
                        double t = (dist - innerThreshold) / (outerThreshold - innerThreshold);
                        newAlpha = (byte)Math.Round(t * originalAlpha);
                    }
                    p.A = newAlpha;
                }
            }
        });
        return rgba;
    }
}
