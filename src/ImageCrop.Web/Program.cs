using ImageCrop.Core;
using QRCoder;
using SixLabors.ImageSharp;

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

app.Run();