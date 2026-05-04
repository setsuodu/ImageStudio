using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ImageCrop.Core;

public static class ImageSharpExtensions
{
    // 1. 原有的裁剪功能
    public static Image CropWithAnchor(this Image image, string ratio, AnchorMode anchor)
    {
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

    // 2. 等比缩放功能
    // scale: 0.5 为缩小一半，2.0 为放大一倍
    public static Image Scale(this Image image, float scale)
    {
        return image.Clone(ctx =>
        {
            Size size = ctx.GetCurrentSize();
            int newWidth = (int)(size.Width * scale);
            int newHeight = (int)(size.Height * scale);

            // 使用 Resize 模式中的 LanczosResampler 以保持高质量
            ctx.Resize(newWidth, newHeight, KnownResamplers.Lanczos3);
        });
    }

    // 3. 格式转换导出功能
    // 支持直接保存到路径，并根据后缀自动识别格式
    public static async Task ConvertAndSaveAsync(this Image image, string outputPath)
    {
        string extension = Path.GetExtension(outputPath).ToLower();

        IImageEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" => new JpegEncoder(),
            ".png" => new PngEncoder(),
            ".bmp" => new BmpEncoder(),
            ".webp" => new WebpEncoder(),
            _ => throw new NotSupportedException($"不支持的格式: {extension}")
        };

        await image.SaveAsync(outputPath, encoder);
    }
}