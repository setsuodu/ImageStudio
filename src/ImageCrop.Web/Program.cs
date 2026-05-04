using ImageCrop.Core;
using SixLabors.ImageSharp;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- 路由 1：仅裁切 ---
app.MapPost("/api/image/crop", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest();
    using var image = await Image.LoadAsync(file.OpenReadStream());
    using var result = image.CropWithAnchor(context.Request.Query["ratio"], Enum.Parse<AnchorMode>(context.Request.Query["anchor"]));
    return await ToFileResult(result, "jpg");
});

// --- 路由 2：仅缩放 ---
app.MapPost("/api/image/scale", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest();
    float.TryParse(context.Request.Query["scale"], out float scale);
    using var image = await Image.LoadAsync(file.OpenReadStream());
    using var result = image.Scale(scale);
    return await ToFileResult(result, "jpg");
});

// --- 路由 3：仅转换格式 ---
app.MapPost("/api/image/convert", async (HttpContext context) => {
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.BadRequest();
    using var image = await Image.LoadAsync(file.OpenReadStream());
    return await ToFileResult(image, context.Request.Query["format"]);
});

// 辅助方法：统一处理导出
async Task<IResult> ToFileResult(Image img, string format)
{
    var (encoder, contentType) = ImageSharpExtensions.GetEncoder(format);
    var ms = new MemoryStream();
    await img.SaveAsync(ms, encoder);
    return Results.File(ms.ToArray(), contentType);
}

app.Run();