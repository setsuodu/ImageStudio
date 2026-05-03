using System.IO;
using System.Text.Json;

// 1. 加载配置
string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine("Config missing!");
    return;
}

var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));

if (config != null)
{
    // 2. 转换枚举并执行
    AnchorMode anchor = Enum.Parse<AnchorMode>(config.DefaultAnchor);
    ImageProcessor.Run(config.SourcePath, config.OutputPath, anchor, config.Ratios);
}

// 定义配置模型
public class AppConfig
{
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string DefaultAnchor { get; set; } = "Center";
    public Dictionary<string, string> Ratios { get; set; } = new();
}