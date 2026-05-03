using System.IO;
using SixLabors.ImageSharp;

public static class ImageProcessor
{
    public static void Run(string src, string dst, AnchorMode anchor, Dictionary<string, string> ratios)
    {
        if (!Directory.Exists(src)) return;

        foreach (var kvp in ratios)
        {
            string subFolder = kvp.Key;   // 例如 "9x16"
            string ratioStr = kvp.Value;  // 例如 "9:16"

            string fullSrcPath = Path.Combine(src, subFolder);
            string fullDstPath = Path.Combine(dst, subFolder);

            if (!Directory.Exists(fullSrcPath)) continue;
            Directory.CreateDirectory(fullDstPath);

            // 遍历并裁切[cite: 1, 2]
            foreach (var file in Directory.EnumerateFiles(fullSrcPath, "*.*"))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext != ".jpg" && ext != ".png" && ext != ".jpeg") continue;

                using var image = Image.Load(file);
                using var cropped = image.CropWithAnchor(ratioStr, anchor);
                cropped.Save(Path.Combine(fullDstPath, Path.GetFileName(file)));

                Console.WriteLine($"[OK] {subFolder} -> {Path.GetFileName(file)}");
            }
        }
    }
}