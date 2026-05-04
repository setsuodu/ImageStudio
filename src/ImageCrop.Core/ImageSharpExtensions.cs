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
    // 找回你原来的所有逻辑：裁切、缩放、多格式支持
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

    public static Image Scale(this Image image, float scale)
    {
        return image.Clone(ctx => {
            Size size = ctx.GetCurrentSize();
            ctx.Resize((int)(size.Width * scale), (int)(size.Height * scale), KnownResamplers.Lanczos3);
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
}