using System.Text.Json;
using ImageCrop.Core;      // 必须引用 Core 才能认得 AnchorMode
using ImageCrop.Console;   // 必须引用才能找到 ImageProcessor

// 1. 读取配置
string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine("缺少 config.json！");
    return;
}

var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));

if (config != null)
{
    // 2. 执行逻辑
    if (Enum.TryParse<AnchorMode>(config.DefaultAnchor, out var anchor))
    {
        Console.WriteLine($"[Console] 开始批量处理，锚点: {anchor}");
        ImageProcessor.Run(config.SourcePath, config.OutputPath, anchor, config.Ratios);
        Console.WriteLine("[Console] 处理完成！");
    }
}

// 配置模型定义
public class AppConfig
{
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string DefaultAnchor { get; set; } = "Center";
    public Dictionary<string, string> Ratios { get; set; } = new();
}