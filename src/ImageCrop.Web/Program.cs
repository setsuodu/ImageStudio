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


// --- 路由 6：音视频转音频（MP4/视频/音频 → MP3/WAV/...）对齐 Convertio 风格 ---
app.MapPost("/api/audio/convert", async (HttpContext context) =>
{
    if (!AudioConvert.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg，无法进行音视频转换" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    var format = context.Request.Query["format"].ToString();
    if (string.IsNullOrWhiteSpace(format)) format = "mp3";

    int? bitrate = null;
    if (int.TryParse(context.Request.Query["bitrate"].ToString(), out int br) && br > 0)
        bitrate = Math.Clamp(br, 32, 320);

    int? sampleRate = null;
    if (int.TryParse(context.Request.Query["samplerate"].ToString(), out int sr) && sr > 0)
        sampleRate = sr;

    // 限制上传大小（默认 200MB，可在配置中调）
    const long maxBytes = 200L * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest($"文件过大，限制 {maxBytes / 1024 / 1024} MB");

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await AudioConvert.ConvertAsync(
            stream, file.FileName ?? "input.mp4", format, bitrate, sampleRate, context.RequestAborted);

        // 异步返回后删除临时文件
        var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath));
        try { File.Delete(outPath); } catch { }
        ms.Position = 0;
        return Results.File(ms, contentType, outName);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// 健康检查：是否具备 ffmpeg
app.MapGet("/api/audio/status", () => Results.Json(new
{
    ffmpeg = AudioConvert.IsFfmpegAvailable(),
    input = AudioConvert.SupportedInputExt.OrderBy(x => x).ToArray(),
    output = AudioConvert.SupportedOutput.OrderBy(x => x).ToArray()
}));

// ========== FFmpeg 视频工具 ==========

const long VideoMaxBytes = 500L * 1024 * 1024; // 500MB

// 视频裁剪：?start=46&duration=74  或  ?start=46&end=120  （秒，支持小数）
app.MapPost("/api/video/trim", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    double? start = null, duration = null, end = null;
    if (double.TryParse(context.Request.Query["start"], out var s) && s >= 0) start = s;
    if (double.TryParse(context.Request.Query["duration"], out var d) && d > 0) duration = d;
    if (double.TryParse(context.Request.Query["end"], out var e) && e > 0) end = e;
    bool copy = !string.Equals(context.Request.Query["reencode"], "1", StringComparison.OrdinalIgnoreCase);

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.TrimAsync(
            stream, file.FileName ?? "input.mp4", start, duration, end, copy, context.RequestAborted);
        var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath));
        try { File.Delete(outPath); } catch { }
        ms.Position = 0;
        return Results.File(ms, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// 修改视频码率：?bitrate=3000 （kbps）
app.MapPost("/api/video/bitrate", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    int bitrate = 3000;
    if (int.TryParse(context.Request.Query["bitrate"], out var b) && b > 0) bitrate = b;

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.SetBitrateAsync(
            stream, file.FileName ?? "input.mp4", bitrate, context.RequestAborted);
        var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath));
        try { File.Delete(outPath); } catch { }
        ms.Position = 0;
        return Results.File(ms, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// 视频抽帧 → zip：?fps=24&max=100
app.MapPost("/api/video/frames", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    int fps = 24;
    if (int.TryParse(context.Request.Query["fps"], out var f) && f > 0) fps = f;
    int? maxFrames = null;
    if (int.TryParse(context.Request.Query["max"], out var m) && m > 0) maxFrames = m;

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.ExtractFramesAsync(
            stream, file.FileName ?? "input.mp4", fps, maxFrames, context.RequestAborted);
        var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath));
        try { File.Delete(outPath); } catch { }
        ms.Position = 0;
        return Results.File(ms, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// 水平等分：?side=left|right|both
app.MapPost("/api/video/split", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    var side = context.Request.Query["side"].ToString();
    if (string.IsNullOrWhiteSpace(side)) side = "left";

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.SplitHorizontalAsync(
            stream, file.FileName ?? "input.mp4", side, context.RequestAborted);
        var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath));
        try { File.Delete(outPath); } catch { }
        ms.Position = 0;
        return Results.File(ms, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// FFmpeg 视频工具状态
app.MapGet("/api/video/status", () => Results.Json(new
{
    ffmpeg = VideoFfmpeg.IsFfmpegAvailable(),
    input = VideoFfmpeg.SupportedVideoExt.OrderBy(x => x).ToArray(),
    tools = new[] { "trim", "bitrate", "frames", "split" }
}));

app.Run();