using System.Diagnostics;
using System.Text;

namespace ImageCrop.Core;

/// <summary>
/// FFmpeg 封装：音视频 → MP3 / WAV（及扩展格式）
/// 依赖系统已安装 ffmpeg（Docker 镜像已包含）
/// </summary>
public static class AudioConvert
{
    public static readonly HashSet<string> SupportedInputExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".mpeg", ".mpg",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif"
    };

    public static readonly HashSet<string> SupportedOutput = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "aac", "ogg", "opus", "m4a"
    };

    public static bool IsFfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将输入文件转换为目标音频格式，返回输出文件路径（调用方负责删除临时文件）
    /// </summary>
    public static async Task<(string OutputPath, string ContentType, string FileName)> ConvertAsync(
        Stream inputStream,
        string originalFileName,
        string targetFormat,
        int? bitrateKbps = null,
        int? sampleRate = null,
        CancellationToken ct = default)
    {
        targetFormat = (targetFormat ?? "mp3").ToLowerInvariant().TrimStart('.');
        if (!SupportedOutput.Contains(targetFormat))
            throw new ArgumentException($"不支持的目标格式: {targetFormat}，支持: {string.Join(", ", SupportedOutput)}");

        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext) || !SupportedInputExt.Contains(ext))
            throw new ArgumentException($"不支持的输入格式: {ext}");

        var workDir = Path.Combine(Path.GetTempPath(), "imagestudio-av-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var inputPath = Path.Combine(workDir, "input" + ext);
        var outputPath = Path.Combine(workDir, "output." + targetFormat);

        try
        {
            await using (var fs = File.Create(inputPath))
            {
                await inputStream.CopyToAsync(fs, ct);
            }

            var args = BuildFfmpegArgs(inputPath, outputPath, targetFormat, bitrateKbps, sampleRate);
            var (exitCode, stderr) = await RunFfmpegAsync(args, ct);
            if (exitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"FFmpeg 转换失败 (code={exitCode}): {Truncate(stderr, 500)}");
            }

            var baseName = Path.GetFileNameWithoutExtension(originalFileName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "output";
            var outFileName = $"{baseName}.{targetFormat}";
            var contentType = GetContentType(targetFormat);

            // 把结果复制到独立临时文件，方便调用方直接返回后清理 workDir
            var finalPath = Path.Combine(Path.GetTempPath(), "imagestudio-out-" + Guid.NewGuid().ToString("N") + "." + targetFormat);
            File.Copy(outputPath, finalPath, true);
            return (finalPath, contentType, outFileName);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* ignore */ }
        }
    }

    static string BuildFfmpegArgs(string input, string output, string format, int? bitrate, int? sampleRate)
    {
        var sb = new StringBuilder();
        sb.Append("-y -i \"").Append(input).Append("\" ");
        sb.Append("-vn "); // 去掉视频轨，只保留音频

        switch (format)
        {
            case "mp3":
                sb.Append("-acodec libmp3lame ");
                sb.Append("-b:a ").Append(bitrate is > 0 ? bitrate.Value : 192).Append("k ");
                break;
            case "wav":
                sb.Append("-acodec pcm_s16le ");
                if (sampleRate is > 0) sb.Append("-ar ").Append(sampleRate.Value).Append(' ');
                else sb.Append("-ar 44100 ");
                break;
            case "flac":
                sb.Append("-acodec flac ");
                break;
            case "aac":
            case "m4a":
                sb.Append("-acodec aac ");
                sb.Append("-b:a ").Append(bitrate is > 0 ? bitrate.Value : 192).Append("k ");
                break;
            case "ogg":
                sb.Append("-acodec libvorbis ");
                sb.Append("-b:a ").Append(bitrate is > 0 ? bitrate.Value : 192).Append("k ");
                break;
            case "opus":
                sb.Append("-acodec libopus ");
                sb.Append("-b:a ").Append(bitrate is > 0 ? bitrate.Value : 128).Append("k ");
                break;
            default:
                sb.Append("-acodec copy ");
                break;
        }

        if (sampleRate is > 0 && format != "wav")
            sb.Append("-ar ").Append(sampleRate.Value).Append(' ');

        sb.Append('"').Append(output).Append('"');
        return sb.ToString();
    }

    static async Task<(int ExitCode, string Stderr)> RunFfmpegAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stderr.ToString());
    }

    public static string GetContentType(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "flac" => "audio/flac",
        "aac" => "audio/aac",
        "m4a" => "audio/mp4",
        "ogg" => "audio/ogg",
        "opus" => "audio/opus",
        _ => "application/octet-stream"
    };

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
