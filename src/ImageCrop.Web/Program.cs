using System.IO.Compression;
using System.Text;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
using ImageCrop.Core;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// ========== 局域网大文件上传：放开 Kestrel / Form 限制 ==========
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

var app = builder.Build();
var log = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- 本地函数（必须全部在顶级语句区域） ----------

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

string BuildDownloadName(string? originalFileName, string format)
{
    format = (format ?? "jpg").TrimStart('.').ToLowerInvariant();
    var baseName = "output";
    if (!string.IsNullOrWhiteSpace(originalFileName))
    {
        baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "output";
        foreach (var ch in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(ch.ToString(), "");
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "output";
    }
    return $"{baseName}.{format}";
}

/// <summary>
/// 正确设置中文文件名下载头（RFC 5987 filename*）
/// </summary>
void SetDownloadFileName(HttpContext ctx, string downloadFileName)
{
    var safeName = string.IsNullOrWhiteSpace(downloadFileName) ? "download" : downloadFileName;
    foreach (var ch in Path.GetInvalidFileNameChars())
        safeName = safeName.Replace(ch, '_');

    // 用 ASP.NET Core 自带的 ContentDispositionHeaderValue，自动生成 filename + filename*
    var cd = new ContentDispositionHeaderValue("attachment");
    cd.SetHttpFileName(safeName); // 内部会正确处理 UTF-8
    ctx.Response.Headers.ContentDisposition = cd.ToString();

    log.LogInformation(">>> Download Content-Disposition = {CD}", ctx.Response.Headers.ContentDisposition.ToString());
    log.LogInformation(">>> Download file name (raw) = [{Name}]", safeName);
}

async Task<IResult> ToFileResult(HttpContext ctx, Image img, string format, string? originalFileName = null)
{
    var (encoder, contentType) = ImageSharpExtensions.GetEncoder(format);
    var ms = new MemoryStream();
    await img.SaveAsync(ms, encoder);
    ms.Position = 0;

    var downloadName = BuildDownloadName(originalFileName, format);
    SetDownloadFileName(ctx, downloadName);
    return Results.File(ms, contentType); // 不再传 fileDownloadName，避免覆盖我们设的头
}

IResult FileBytes(HttpContext ctx, byte[] bytes, string contentType, string downloadFileName)
{
    SetDownloadFileName(ctx, downloadFileName);
    return Results.File(bytes, contentType); // 不传第三个参数
}

// ---------- 路由 ----------

app.MapPost("/api/image/crop", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    log.LogInformation(">>> [crop] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    using var image = await Image.LoadAsync(file.OpenReadStream());
    var ratio = context.Request.Query["ratio"].ToString() ?? "1:1";
    var anchorStr = context.Request.Query["anchor"].ToString() ?? "Center";
    if (!Enum.TryParse<AnchorMode>(anchorStr, true, out var anchor))
        anchor = AnchorMode.Center;

    using var result = image.CropWithAnchor(ratio, anchor);
    return await ToFileResult(context, result, "jpg", file.FileName);
});

app.MapPost("/api/image/scale", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    log.LogInformation(">>> [scale] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    int.TryParse(context.Request.Query["scale"], out int targetSize);
    if (targetSize < 1) targetSize = 256;

    using var image = await Image.LoadAsync(file.OpenReadStream());
    using var result = image.ScaleToMaxSide(targetSize);

    string outputFormat = GetFormatFromFileName(file.FileName) ?? "png";
    return await ToFileResult(context, result, outputFormat, file.FileName);
});

app.MapPost("/api/image/convert", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    log.LogInformation(">>> [convert] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    using var image = await Image.LoadAsync(file.OpenReadStream());
    var format = context.Request.Query["format"].ToString() ?? "jpg";
    return await ToFileResult(context, image, format, file.FileName);
});

app.MapGet("/api/image/qrcode", async (HttpContext context) =>
{
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
    return await ToFileResult(context, qrImage, "png", "qrcode.png");
});

app.MapPost("/api/image/grid", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    log.LogInformation(">>> [grid] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    int? forceRows = null, forceCols = null;
    if (int.TryParse(context.Request.Query["rows"].ToString(), out int r) && r > 0)
        forceRows = Math.Clamp(r, 1, 20);
    if (int.TryParse(context.Request.Query["cols"].ToString(), out int c) && c > 0)
        forceCols = Math.Clamp(c, 1, 20);

    int.TryParse(context.Request.Query["scale"].ToString(), out int targetSize);

    bool doMatte = !string.Equals(context.Request.Query["matte"].ToString(), "false", StringComparison.OrdinalIgnoreCase);
    int.TryParse(context.Request.Query["threshold"].ToString(), out int threshold);
    if (threshold < 1) threshold = 24;

    var namePrefix = context.Request.Query["name"].ToString();
    if (string.IsNullOrWhiteSpace(namePrefix))
    {
        namePrefix = Path.GetFileNameWithoutExtension(file.FileName ?? "");
        if (string.IsNullOrWhiteSpace(namePrefix)) namePrefix = "icon";
    }
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
            using var extracted = image.Clone(ctx => ctx.Crop(new Rectangle(cell.X, cell.Y, cell.Width, cell.Height)));
            using var trimmed = ImageSharpExtensions.TrimBackground(extracted, bg, threshold);

            Image output = doMatte
                ? ImageSharpExtensions.RemoveBackground(trimmed, bg)
                : trimmed.Clone(_ => { });

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

    var zipBase = Path.GetFileNameWithoutExtension(file.FileName ?? "icons");
    if (string.IsNullOrWhiteSpace(zipBase)) zipBase = "icons";
    foreach (var ch in Path.GetInvalidFileNameChars())
        zipBase = zipBase.Replace(ch.ToString(), "");
    var zipName = $"{zipBase}_icons.zip";
    return FileBytes(context, zipMs.ToArray(), "application/zip", zipName);
});

app.MapPost("/api/audio/convert", async (HttpContext context) =>
{
    if (!AudioConvert.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg，无法进行音视频转换" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");

    log.LogInformation(">>> [audio/convert] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    var format = context.Request.Query["format"].ToString();
    if (string.IsNullOrWhiteSpace(format)) format = "mp3";

    int? bitrate = null;
    if (int.TryParse(context.Request.Query["bitrate"].ToString(), out int br) && br > 0)
        bitrate = Math.Clamp(br, 32, 320);

    int? sampleRate = null;
    if (int.TryParse(context.Request.Query["samplerate"].ToString(), out int sr) && sr > 0)
        sampleRate = sr;

    const long maxBytes = 2L * 1024 * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest($"文件过大，限制 {maxBytes / 1024 / 1024} MB");

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await AudioConvert.ConvertAsync(
            stream, file.FileName ?? "input.mp4", format, bitrate, sampleRate, context.RequestAborted);

        log.LogInformation(">>> [audio/convert] 返回下载名=[{OutName}]", outName);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
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

app.MapGet("/api/audio/status", () => Results.Json(new
{
    ffmpeg = AudioConvert.IsFfmpegAvailable(),
    input = AudioConvert.SupportedInputExt.OrderBy(x => x).ToArray(),
    output = AudioConvert.SupportedOutput.OrderBy(x => x).ToArray()
}));

const long VideoMaxBytes = 2L * 1024 * 1024 * 1024;

app.MapPost("/api/video/trim", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    log.LogInformation(">>> [video/trim] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

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

        log.LogInformation(">>> [video/trim] 返回下载名=[{OutName}]", outName);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/video/bitrate", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    log.LogInformation(">>> [video/bitrate] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    int bitrate = 3000;
    if (int.TryParse(context.Request.Query["bitrate"], out var b) && b > 0) bitrate = b;

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.SetBitrateAsync(
            stream, file.FileName ?? "input.mp4", bitrate, context.RequestAborted);

        log.LogInformation(">>> [video/bitrate] 返回下载名=[{OutName}]", outName);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/video/frames", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    log.LogInformation(">>> [video/frames] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    int fps = 24;
    if (int.TryParse(context.Request.Query["fps"], out var f) && f > 0) fps = f;
    int? maxFrames = null;
    if (int.TryParse(context.Request.Query["max"], out var m) && m > 0) maxFrames = m;

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.ExtractFramesAsync(
            stream, file.FileName ?? "input.mp4", fps, maxFrames, context.RequestAborted);

        log.LogInformation(">>> [video/frames] 返回下载名=[{OutName}]", outName);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/video/split", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    // ★ 关键日志：看后端到底收到什么文件名
    log.LogInformation(">>> [video/split] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);
    log.LogInformation(">>> [video/split] FileName 字节(UTF8)=[{Bytes}]",
        file.FileName == null ? "(null)" : BitConverter.ToString(Encoding.UTF8.GetBytes(file.FileName)));

    var side = context.Request.Query["side"].ToString();
    if (string.IsNullOrWhiteSpace(side)) side = "left";

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.SplitHorizontalAsync(
            stream, file.FileName ?? "input.mp4", side, context.RequestAborted);

        log.LogInformation(">>> [video/split] VideoFfmpeg 返回下载名=[{OutName}]", outName);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapGet("/api/video/status", () => Results.Json(new
{
    ffmpeg = VideoFfmpeg.IsFfmpegAvailable(),
    input = VideoFfmpeg.SupportedVideoExt.OrderBy(x => x).ToArray(),
    tools = new[] { "trim", "bitrate", "frames", "split" }
}));

app.Run();
