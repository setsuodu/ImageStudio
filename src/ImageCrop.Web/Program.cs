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


app.MapPost("/api/video/aspect", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    log.LogInformation(">>> [video/aspect] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    var ratio = context.Request.Query["ratio"].ToString();
    if (string.IsNullOrWhiteSpace(ratio)) ratio = "16:9";
    var mode = context.Request.Query["mode"].ToString();
    if (string.IsNullOrWhiteSpace(mode)) mode = "stretch"; // 默认缩放拉伸，不裁不加黑边

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.ChangeAspectAsync(
            stream, file.FileName ?? "input.mp4", ratio, mode, context.RequestAborted);

        log.LogInformation(">>> [video/aspect] 返回下载名=[{OutName}] ratio={Ratio} mode={Mode}", outName, ratio, mode);

        var bytes = await File.ReadAllBytesAsync(outPath);
        try { File.Delete(outPath); } catch { }
        return FileBytes(context, bytes, contentType, outName);
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/video/tomp4", async (HttpContext context) =>
{
    if (!VideoFfmpeg.IsFfmpegAvailable())
        return Results.Json(new { error = "服务器未安装 ffmpeg" }, statusCode: 503);

    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest("No file uploaded");
    if (file.Length > VideoMaxBytes) return Results.BadRequest($"文件过大，限制 {VideoMaxBytes / 1024 / 1024} MB");

    log.LogInformation(">>> [video/tomp4] 收到文件名=[{FileName}] 大小={Len}", file.FileName, file.Length);

    // 默认优先 -c copy（仅换容器）；?reencode=1 强制重新编码
    bool copyCodec = !string.Equals(context.Request.Query["reencode"], "1", StringComparison.OrdinalIgnoreCase);

    int crf = 23;
    if (int.TryParse(context.Request.Query["crf"], out var c) && c >= 10 && c <= 35) crf = c;

    try
    {
        await using var stream = file.OpenReadStream();
        var (outPath, contentType, outName) = await VideoFfmpeg.ConvertToMp4Async(
            stream, file.FileName ?? "input.flv", copyCodec, crf, context.RequestAborted);

        log.LogInformation(">>> [video/tomp4] 返回下载名=[{OutName}]", outName);

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
    tools = new[] { "trim", "bitrate", "frames", "split", "aspect", "tomp4" }
}));

// ---------- 从 ComfyUI workflow / prompt JSON 里抽出纯字符串 Prompt ----------
static List<string> ExtractPurePromptsFromComfyJson(string? jsonText)
{
    var list = new List<string>();
    if (string.IsNullOrWhiteSpace(jsonText)) return list;
    var t = jsonText.Trim();
    if (!(t.StartsWith('{') || t.StartsWith('['))) return list;

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(t);
        var root = doc.RootElement;
        // ComfyUI 常见两种：直接节点字典，或 { "prompt": {节点...}, "workflow": ... }
        System.Text.Json.JsonElement nodes = root;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (root.TryGetProperty("prompt", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Object)
                nodes = p;
            else if (root.TryGetProperty("workflow", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Object)
                nodes = w;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(System.Text.Json.JsonElement el, string? parentKey)
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // 节点 inputs
                if (el.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in inputs.EnumerateObject())
                    {
                        var k = prop.Name;
                        var v = prop.Value;
                        if (v.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        var s = v.GetString()?.Trim();
                        if (string.IsNullOrEmpty(s) || s.Length < 8) continue;
                        // 排除文件名 / 模型路径
                        var low = s.ToLowerInvariant();
                        if (low.EndsWith(".png") || low.EndsWith(".jpg") || low.EndsWith(".jpeg")
                            || low.EndsWith(".webp") || low.EndsWith(".safetensors") || low.EndsWith(".ckpt")
                            || low.EndsWith(".pt") || low.EndsWith(".bin")) continue;
                        // 只要 prompt 相关字段，或较长的 on_false/on_true/value 文本
                        var kl = k.ToLowerInvariant();
                        bool isPromptKey = kl is "prompt" or "text" or "value" or "on_false" or "on_true"
                            or "positive" or "negative" or "negative_prompt" or "string"
                            || kl.Contains("prompt");
                        // system prompt 很长也收，但标记由前端区分；这里全收纯字符串
                        if (!isPromptKey && s.Length < 40) continue;
                        if (seen.Add(s)) list.Add(s);
                    }
                }
                foreach (var prop in el.EnumerateObject())
                    Walk(prop.Value, prop.Name);
            }
            else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                    Walk(item, parentKey);
            }
        }
        Walk(nodes, null);
    }
    catch { /* 非合法 JSON 就忽略 */ }
    return list;
}

// ---------- PNG / MP4 Prompt 提取（读取 AI 生成图/视频内嵌的 prompt / parameters） ----------
app.MapPost("/api/image/prompt", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    // 也支持直接贴 workflow JSON 文本（字段名 json）
    var jsonBody = form["json"].ToString();

    if (file == null && string.IsNullOrWhiteSpace(jsonBody))
        return Results.BadRequest("请上传 PNG 或 MP4，或粘贴 ComfyUI workflow JSON");

    log.LogInformation(">>> [prompt] 文件=[{FileName}] 大小={Len} jsonLen={JL}",
        file?.FileName, file?.Length ?? 0, jsonBody?.Length ?? 0);

    try
    {
        var texts = new List<object>();
        string? mainPrompt = null;
        string? parameters = null;
        string? negativePrompt = null;
        string? workflow = null;
        int width = 0, height = 0;
        string? fileName = file?.FileName;

        // 判断是否为视频
        static bool IsVideoFile(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var e = Path.GetExtension(name).ToLowerInvariant();
            return e is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v" or ".flv";
        }

        void ApplyTag(string key, string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            texts.Add(new { keyword = key, value = val });

            var keyLower = key.ToLowerInvariant();
            // 去掉 stream[0]. 前缀再判断
            var pureKey = keyLower;
            var idx = pureKey.LastIndexOf('.');
            if (idx >= 0) pureKey = pureKey[(idx + 1)..];

            if (pureKey is "parameters" or "sd-metadata")
                parameters ??= val;
            else if (pureKey is "prompt" or "positive_prompt" or "positive")
                mainPrompt ??= val;
            else if (pureKey is "negative_prompt" or "negative prompt" or "negative")
                negativePrompt ??= val;
            else if (pureKey is "workflow" or "comfyui")
                workflow ??= val;
            else if (pureKey is "comment" or "description" or "title" or "synopsis" or "handler_name" or "copyright")
            {
                if (val.TrimStart().StartsWith('{') && parameters == null)
                    parameters = val;
                else if (mainPrompt == null && val.Length > 20) // 太短的可能是编码器名
                    mainPrompt = val;
            }
            // 任意看起来像长文本的也收进 mainPrompt 候选
            else if (mainPrompt == null && val.Length > 40 &&
                     (val.Contains(',') || val.Contains('\n') || val.TrimStart().StartsWith('{')))
            {
                mainPrompt = val;
            }
        }

        if (file != null)
        {
            if (IsVideoFile(file.FileName))
            {
                // ---------- 视频：用 ffprobe 提取元数据标签 ----------
                await using var stream = file.OpenReadStream();
                var tags = await VideoFfmpeg.ExtractMetadataTagsAsync(stream, file.FileName);
                foreach (var (key, val) in tags)
                    ApplyTag(key, val);

                log.LogInformation(">>> [prompt] 视频元数据标签数={Count}", tags.Count);
            }
            else
            {
                // ---------- 图片：ImageSharp 读 PNG 文本块 + EXIF ----------
                await using var stream = file.OpenReadStream();
                using var image = await Image.LoadAsync(stream);
                width = image.Width;
                height = image.Height;

                var pngMeta = image.Metadata.GetPngMetadata();
                foreach (var t in pngMeta.TextData)
                    ApplyTag(t.Keyword ?? "", t.Value ?? "");

                var exif = image.Metadata.ExifProfile;
                if (exif != null)
                {
                    void AddExifString(string label, string? v)
                    {
                        if (string.IsNullOrWhiteSpace(v)) return;
                        ApplyTag("Exif:" + label, v);
                    }
                    if (exif.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ImageDescription, out var desc) && desc?.Value != null)
                        AddExifString("ImageDescription", desc.Value);
                    if (exif.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software, out var soft) && soft?.Value != null)
                        AddExifString("Software", soft.Value);
                    if (exif.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.XPComment, out var xpc) && xpc?.Value != null)
                        AddExifString("XPComment", xpc.Value);
                    if (exif.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.UserComment, out var uc) && uc?.Value != null)
                        AddExifString("UserComment", uc.Value.Text);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(jsonBody))
        {
            // 用户直接粘贴的 workflow / prompt JSON
            mainPrompt ??= jsonBody;
            texts.Add(new { keyword = "pasted_json", value = jsonBody });
        }

        // 从 A1111 风格 parameters 拆 prompt
        if (!string.IsNullOrEmpty(parameters) && (string.IsNullOrEmpty(mainPrompt) || mainPrompt.TrimStart().StartsWith('{')))
        {
            var lines = parameters.Replace("\r\n", "\n").Split('\n');
            var promptLines = new List<string>();
            var negLines = new List<string>();
            bool inNeg = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase))
                {
                    inNeg = true;
                    var rest = line["Negative prompt:".Length..].Trim();
                    if (!string.IsNullOrEmpty(rest)) negLines.Add(rest);
                    continue;
                }
                if (inNeg && (line.StartsWith("Steps:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Sampler:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("CFG scale:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Seed:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Size:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)))
                    break;
                if (inNeg) negLines.Add(line);
                else promptLines.Add(line);
            }
            if (promptLines.Count > 0 && !string.Join("\n", promptLines).TrimStart().StartsWith('{'))
                mainPrompt = string.Join("\n", promptLines).Trim();
            if (negLines.Count > 0 && string.IsNullOrEmpty(negativePrompt))
                negativePrompt = string.Join("\n", negLines).Trim();
        }

        // ★ ComfyUI：prompt / workflow 若是 JSON，解析出全部纯字符串 Prompt
        var pureList = new List<string>();
        pureList.AddRange(ExtractPurePromptsFromComfyJson(mainPrompt));
        pureList.AddRange(ExtractPurePromptsFromComfyJson(workflow));
        pureList.AddRange(ExtractPurePromptsFromComfyJson(parameters));
        pureList.AddRange(ExtractPurePromptsFromComfyJson(jsonBody));
        // 去重保序
        var purePrompts = pureList.Distinct(StringComparer.Ordinal).ToList();

        // 从 purePrompts 里挑最像「自然语言用户 Prompt」的一条当主显示
        // 规则：排除 system(#开头)、公式/代码、极短串；优先长描述、含中文或常见 prompt 结构
        static string? PickBestUserPrompt(IReadOnlyList<string> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;

            static bool LooksLikeFormulaOrCode(string s)
            {
                var t = s.Trim();
                if (t.Length < 12) return true;
                // 数学表达式 / 函数调用感很重
                if (t.Contains("round(") || t.Contains("max(") || t.Contains("min(")
                    || t.Contains("% 17") || t.Contains("math.") || t.StartsWith("return ")
                    || (t.Count(c => c is '(' or ')') >= 4 && t.Count(c => c == '*') >= 1))
                    return true;
                // 几乎全是符号/数字
                int letter = t.Count(char.IsLetter);
                return letter < t.Length * 0.25;
            }

            static bool LooksLikeSystem(string s) =>
                s.TrimStart().StartsWith('#') || s.StartsWith("You are ", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("System:", StringComparison.OrdinalIgnoreCase);

            var scored = candidates
                .Select((s, i) =>
                {
                    int score = 0;
                    var t = s.Trim();
                    if (LooksLikeSystem(t)) score -= 1000;
                    if (LooksLikeFormulaOrCode(t)) score -= 500;
                    if (t.Length < 20) score -= 200;
                    if (t.Length > 40) score += 50;
                    if (t.Length > 80) score += 80;
                    if (t.Length > 150) score += 40;
                    // 含中文加分（很多视频工具中文 prompt）
                    if (t.Any(c => c >= 0x4E00 && c <= 0x9FFF)) score += 120;
                    // 常见自然语言特征
                    if (t.Contains(' ') || t.Contains('，') || t.Contains('。') || t.Contains(',')) score += 30;
                    // 原始顺序靠前略加分（保序倾向）
                    score += Math.Max(0, 20 - i);
                    if (t.Length > 2500) score -= 100;
                    return (s, score);
                })
                .OrderByDescending(x => x.score)
                .ToList();

            return scored.FirstOrDefault().s;
        }

        // mainPrompt 是 JSON / 公式 / 过短时，用 purePrompts 里最好的一条覆盖
        bool mainIsJson = !string.IsNullOrEmpty(mainPrompt) && mainPrompt.TrimStart().StartsWith('{');
        bool mainLooksBad = string.IsNullOrEmpty(mainPrompt)
            || mainIsJson
            || (mainPrompt!.Length < 30 && purePrompts.Count > 0)
            || (purePrompts.Count > 0 && purePrompts.Any(p => p.Length > mainPrompt.Length * 2));

        if (mainLooksBad && purePrompts.Count > 0)
        {
            var best = PickBestUserPrompt(purePrompts);
            if (!string.IsNullOrEmpty(best))
                mainPrompt = best;
        }

        var hasAny = texts.Count > 0 || purePrompts.Count > 0;
        return Results.Json(new
        {
            ok = true,
            fileName,
            width,
            height,
            hasMetadata = hasAny,
            prompt = mainPrompt,
            negativePrompt,
            parameters,
            workflow,
            purePrompts, // ★ 前端主显示：完整纯 Prompt 列表
            texts
        });
    }
    catch (UnknownImageFormatException)
    {
        return Results.BadRequest("无法识别为图片，请上传 PNG / MP4，或粘贴 ComfyUI workflow JSON");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[prompt] 提取失败");
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.Run();
