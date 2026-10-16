using System.Text.Json;
using ImageCrop.Core;
using ImageCrop.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ========== 模式切换：--mcp 启动 MCP 服务器，否则走原来的 CLI ==========
if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
{
    await RunMcpAsync();
    return;
}

// ---------- 原有 CLI 逻辑（保持不变） ----------
string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine("缺少 config.json！");
    Console.WriteLine("用法：");
    Console.WriteLine("  ImageCrop.Console              # 按 config.json 批量裁切");
    Console.WriteLine("  ImageCrop.Console --mcp        # 以 MCP 服务器模式运行（给 Claude/Cursor 用）");
    return;
}

var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath));

if (config != null)
{
    if (Enum.TryParse<AnchorMode>(config.DefaultAnchor, out var anchor))
    {
        Console.WriteLine($"[Console] 开始批量处理，锚点: {anchor}");
        ImageProcessor.Run(config.SourcePath, config.OutputPath, anchor, config.Ratios);
        Console.WriteLine("[Console] 处理完成！");
    }
}

// ---------- MCP 启动 ----------
static async Task RunMcpAsync()
{
    var builder = Host.CreateApplicationBuilder();

    builder.Logging.AddConsole(options =>
    {
        // MCP 协议要求 stdout 只传 JSON-RPC，日志必须走 stderr
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

// 配置模型定义
public class AppConfig
{
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string DefaultAnchor { get; set; } = "Center";
    public Dictionary<string, string> Ratios { get; set; } = new();
}
