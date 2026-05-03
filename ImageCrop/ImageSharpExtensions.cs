using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

public static class ImageSharpExtensions
{
    public static Image CropWithAnchor(this Image image, string ratio, AnchorMode anchor)
    {
        var parts = ratio.Split(':');
        if (parts.Length != 2 || !float.TryParse(parts[0], out float rw) || !float.TryParse(parts[1], out float rh))
            return image.Clone(_ => { });

        float targetRatio = rw / rh;

        return image.Clone(ctx =>
        {
            Size size = ctx.GetCurrentSize();
            int cropWidth, cropHeight;

            // 计算比例尺寸
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

            // 实现锚点偏好
            int x = anchor switch
            {
                AnchorMode.Left => 0,
                AnchorMode.Right => size.Width - cropWidth,
                _ => (size.Width - cropWidth) / 2 // Center[cite: 1]
            };
            int y = (size.Height - cropHeight) / 2;

            ctx.Crop(new Rectangle(x, y, cropWidth, cropHeight));
        });
    }
}

public enum AnchorMode { Left, Center, Right }