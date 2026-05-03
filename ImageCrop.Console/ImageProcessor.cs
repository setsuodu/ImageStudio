using ImageCrop.Core;
using SixLabors.ImageSharp;

namespace ImageCrop.Console;

public static class ImageProcessor
{
    public static void Run(string src, string dst, AnchorMode anchor, Dictionary<string, string> ratios)
    {
        if (!Directory.Exists(src)) return;

        foreach (var kvp in ratios)
        {
            string fullSrc = Path.Combine(src, kvp.Key);
            string fullDst = Path.Combine(dst, kvp.Key);

            if (!Directory.Exists(fullSrc)) continue;
            Directory.CreateDirectory(fullDst);

            foreach (var file in Directory.EnumerateFiles(fullSrc, "*.*"))
            {
                using var image = Image.Load(file);
                using var cropped = image.CropWithAnchor(kvp.Value, anchor);
                cropped.Save(Path.Combine(fullDst, Path.GetFileName(file)));
            }
        }
    }
}