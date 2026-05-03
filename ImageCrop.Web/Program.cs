using ImageCrop.Core;
using SixLabors.ImageSharp;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/crop", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var ratio = context.Request.Query["ratio"].ToString() ?? "1:1";
    var anchorStr = context.Request.Query["anchor"].ToString() ?? "Center";

    if (file == null) return Results.BadRequest();
    Enum.TryParse<AnchorMode>(anchorStr, out var anchor);

    using var input = file.OpenReadStream();
    using var image = await Image.LoadAsync(input);
    using var cropped = image.CropWithAnchor(ratio, anchor);

    var ms = new MemoryStream();
    await cropped.SaveAsJpegAsync(ms);
    return Results.File(ms.ToArray(), "image/jpeg");
});

app.Run();