using System.IO.Compression;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using ImageCrop.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- 路由 1：仅裁切 ---
app.MapPost("/api/image/crop", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    using var image = await Image.LoadAsync(file.OpenReadStream());
    var ratio = context.Request.Query["ratio"].ToString() ?? "1:1";
    var anchorStr = context.Request.Query["anchor"].ToString() ?? "Center";
    if (!Enum.TryParse<AnchorMode>(anchorStr, true, out var anchor))
        anchor = AnchorMode.Center;

    using var result = image.CropWithAnchor(ratio, anchor);
    return await ToFileResult(result, "jpg");
});

// --- 路由 2：等比缩放（目标最长边）---
app.MapPost("/api/image/scale", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    int.TryParse(context.Request.Query["scale"], out int targetSize);
    if (targetSize < 1) targetSize = 256;

    using var image = await Image.LoadAsync(file.OpenReadStream());
    using var result = image.ScaleToMaxSide(targetSize);

    // 关键修复：保留原始格式，而不是强制 jpg
    string outputFormat = GetFormatFromFileName(file.FileName) ?? "png";
    return await ToFileResult(result, outputFormat);
});

// 新增辅助方法（放到文件最后）
string GetFormatFromFileName(string fileName)
{
    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    return ext switch
    {
        ".png" => "png",
        ".webp" => "webp",
        ".bmp" => "bmp",
        _ => "jpg"
    };
}

// --- 路由 3：仅转换格式 ---
app.MapPost("/api/image/convert", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    using var image = await Image.LoadAsync(file.OpenReadStream());
    var format = context.Request.Query["format"].ToString() ?? "jpg";
    return await ToFileResult(image, format);
});

// 辅助方法
async Task<IResult> ToFileResult(Image img, string format)
{
    var (encoder, contentType) = ImageSharpExtensions.GetEncoder(format);
    var ms = new MemoryStream();
    await img.SaveAsync(ms, encoder);
    ms.Position = 0;
    return Results.File(ms, contentType, $"output.{format}");
}

// 二维码
app.MapGet("/api/image/qrcode", async (HttpContext context) => {
    var text = context.Request.Query["text"].ToString();
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest("缺少 text 参数");

    int size = 512;
    int.TryParse(context.Request.Query["size"].ToString(), out size);

    var eccStr = context.Request.Query["ecc"].ToString()?.ToUpper() ?? "Q";
    var eccLevel = eccStr switch
    {
        "L" => QRCodeGenerator.ECCLevel.L,
        "M" => QRCodeGenerator.ECCLevel.M,
        "Q" => QRCodeGenerator.ECCLevel.Q,
        "H" => QRCodeGenerator.ECCLevel.H,
        _ => QRCodeGenerator.ECCLevel.Q
    };

    using var qrImage = ImageSharpExtensions.GenerateQRCode(text, size, eccLevel);

    return await ToFileResult(qrImage, "png");
});

// --- 路由 5：网格切图（对齐 image-studio-node /api/process）---
// 自动识别网格 or 手动 rows/cols；trim 去边距让图标居中；可选去底色；可选等比缩放；输出 icons.zip
app.MapPost("/api/image/grid", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    // rows/cols 都传才走手动，否则自动识别
    int? forceRows = null, forceCols = null;
    if (int.TryParse(context.Request.Query["rows"].ToString(), out int r) && r > 0)
        forceRows = Math.Clamp(r, 1, 20);
    if (int.TryParse(context.Request.Query["cols"].ToString(), out int c) && c > 0)
        forceCols = Math.Clamp(c, 1, 20);

    int.TryParse(context.Request.Query["scale"].ToString(), out int targetSize);
    // scale<=0 → 不缩放

    bool doMatte = !string.Equals(context.Request.Query["matte"].ToString(), "false", StringComparison.OrdinalIgnoreCase);
    int.TryParse(context.Request.Query["threshold"].ToString(), out int threshold);
    if (threshold < 1) threshold = 24;

    // 自定义文件名前缀，默认 icon
    var namePrefix = context.Request.Query["name"].ToString();
    if (string.IsNullOrWhiteSpace(namePrefix)) namePrefix = "icon";
    // 去掉非法文件名字符
    foreach (var ch in Path.GetInvalidFileNameChars())
        namePrefix = namePrefix.Replace(ch.ToString(), "");
    if (string.IsNullOrWhiteSpace(namePrefix)) namePrefix = "icon";

    using var image = await Image.LoadAsync(file.OpenReadStream());

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
        return Results.BadRequest(ex.Message);
    }

    var zipMs = new MemoryStream();
    using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
    {
        int index = 1;
        foreach (var cell in cells)
        {
            // 1. 裁出 cell
            using var extracted = image.Clone(ctx => ctx.Crop(new Rectangle(cell.X, cell.Y, cell.Width, cell.Height)));

            // 2. trim：去掉 cell 内多余背景边距，让图标紧贴/居中
            using var trimmed = ImageSharpExtensions.TrimBackground(extracted, bg, threshold);

            // 3. 去底色（默认开）
            Image output = doMatte
                ? ImageSharpExtensions.RemoveBackground(trimmed, bg)
                : trimmed.Clone(_ => { });

            // 4. 可选等比缩放（最长边）
            if (targetSize > 0)
            {
                var scaled = output.ScaleToMaxSide(targetSize);
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

    zipMs.Position = 0;
    context.Response.Headers["X-Grid-Mode"] = mode;
    context.Response.Headers["X-Grid-Rows"] = detectedRows.ToString();
    context.Response.Headers["X-Grid-Cols"] = detectedCols.ToString();
    return Results.File(zipMs, "application/zip", "icons.zip");
});

app.Run();