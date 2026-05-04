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
}