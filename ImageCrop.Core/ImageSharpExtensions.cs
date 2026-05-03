using SixLabors.ImageSharp;
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
}