using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ImageCrop.Core;

/// <summary>
/// FFmpeg 视频工具：裁剪、改码率、抽帧、水平切割等
/// 依赖系统已安装 ffmpeg（Docker 镜像已包含）
/// </summary>
public static class VideoFfmpeg
{
    public static readonly HashSet<string> SupportedVideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".mpeg", ".mpg", ".ts"
    };

    public static bool IsFfmpegAvailable() => AudioConvert.IsFfmpegAvailable();

    /// <summary>
    /// 视频裁剪：从 startSec 开始，持续 durationSec 秒（或到 endSec）。
    /// 优先 -c copy 快速剪切；若失败可后续扩展 reencode。
    /// </summary>
    public static async Task<(string OutputPath, string ContentType, string FileName)> TrimAsync(
        Stream inputStream,
        string originalFileName,
        double? startSec,
        double? durationSec,
        double? endSec,
        bool copyCodec = true,
        CancellationToken ct = default)
    {
        var (workDir, inputPath, baseName, ext) = await PrepareInputAsync(inputStream, originalFileName, ct);
        var outExt = string.IsNullOrEmpty(ext) ? ".mp4" : ext;
        var outputPath = Path.Combine(workDir, "output" + outExt);

        try
        {
            var sb = new StringBuilder("-y ");
            if (startSec is > 0)
                sb.Append("-ss ").Append(FormatTime(startSec.Value)).Append(' ');
            sb.Append("-i \"").Append(inputPath).Append("\" ");

            if (durationSec is > 0)
                sb.Append("-t ").Append(FormatTime(durationSec.Value)).Append(' ');
            else if (endSec is > 0)
                sb.Append("-to ").Append(FormatTime(endSec.Value)).Append(' ');

            if (copyCodec)
                sb.Append("-c copy ");
            else
                sb.Append("-c:v libx264 -c:a aac ");

            sb.Append('"').Append(outputPath).Append('"');

            var (exitCode, stderr) = await RunFfmpegAsync(sb.ToString(), ct);
            if (exitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"FFmpeg 裁剪失败 (code={exitCode}): {Truncate(stderr, 500)}");

            var finalPath = CopyToFinal(outputPath, outExt);
            var outName = $"{baseName}_trim{outExt}";
            return (finalPath, "video/mp4", outName);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>
    /// 修改视频码率（默认 3000k），视频用 libx264，音频 copy
    /// </summary>
    public static async Task<(string OutputPath, string ContentType, string FileName)> SetBitrateAsync(
        Stream inputStream,
        string originalFileName,
        int bitrateKbps = 3000,
        CancellationToken ct = default)
    {
        if (bitrateKbps < 100) bitrateKbps = 100;
        if (bitrateKbps > 50000) bitrateKbps = 50000;

        var (workDir, inputPath, baseName, ext) = await PrepareInputAsync(inputStream, originalFileName, ct);
        var outExt = ".mp4";
        var outputPath = Path.Combine(workDir, "output" + outExt);

        try
        {
            var args = $"-y -i \"{inputPath}\" -b:v {bitrateKbps}k -c:v libx264 -c:a copy \"{outputPath}\"";
            var (exitCode, stderr) = await RunFfmpegAsync(args, ct);
            if (exitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"FFmpeg 改码率失败 (code={exitCode}): {Truncate(stderr, 500)}");

            var finalPath = CopyToFinal(outputPath, outExt);
            var outName = $"{baseName}_{bitrateKbps}k{outExt}";
            return (finalPath, "video/mp4", outName);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>
    /// 视频转序列帧，打包成 zip。fps 默认 24。
    /// </summary>
    public static async Task<(string OutputPath, string ContentType, string FileName)> ExtractFramesAsync(
        Stream inputStream,
        string originalFileName,
        int fps = 24,
        int? maxFrames = null,
        CancellationToken ct = default)
    {
        if (fps < 1) fps = 1;
        if (fps > 60) fps = 60;

        var (workDir, inputPath, baseName, _) = await PrepareInputAsync(inputStream, originalFileName, ct);
        var framesDir = Path.Combine(workDir, "frames");
        Directory.CreateDirectory(framesDir);
        var pattern = Path.Combine(framesDir, "frame_%04d.png");

        try
        {
            var sb = new StringBuilder("-y -i \"").Append(inputPath).Append("\" -r ").Append(fps).Append(' ');
            if (maxFrames is > 0)
                sb.Append("-frames:v ").Append(maxFrames.Value).Append(' ');
            sb.Append('"').Append(pattern).Append('"');

            var (exitCode, stderr) = await RunFfmpegAsync(sb.ToString(), ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"FFmpeg 抽帧失败 (code={exitCode}): {Truncate(stderr, 500)}");

            var files = Directory.GetFiles(framesDir, "*.png").OrderBy(f => f).ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException("未生成任何帧");

            var zipPath = Path.Combine(Path.GetTempPath(), "imagestudio-frames-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                    zip.CreateEntryFromFile(f, Path.GetFileName(f));
            }

            var outName = $"{baseName}_frames.zip";
            return (zipPath, "application/zip", outName);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>
    /// 水平等分：left / right / both（both 返回 zip）
    /// </summary>
    public static async Task<(string OutputPath, string ContentType, string FileName)> SplitHorizontalAsync(
        Stream inputStream,
        string originalFileName,
        string side = "left", // left | right | both
        CancellationToken ct = default)
    {
        side = (side ?? "left").ToLowerInvariant();
        var (workDir, inputPath, baseName, ext) = await PrepareInputAsync(inputStream, originalFileName, ct);
        var outExt = string.IsNullOrEmpty(ext) ? ".mp4" : ext;

        try
        {
            if (side == "both")
            {
                var leftPath = Path.Combine(workDir, "left" + outExt);
                var rightPath = Path.Combine(workDir, "right" + outExt);

                var argsLeft = $"-y -i \"{inputPath}\" -vf \"crop=iw/2:ih:0:0\" -c:a copy \"{leftPath}\"";
                var (c1, e1) = await RunFfmpegAsync(argsLeft, ct);
                if (c1 != 0 || !File.Exists(leftPath))
                    throw new InvalidOperationException($"左侧切割失败: {Truncate(e1, 300)}");

                var argsRight = $"-y -i \"{inputPath}\" -vf \"crop=iw/2:ih:iw/2:0\" -c:a copy \"{rightPath}\"";
                var (c2, e2) = await RunFfmpegAsync(argsRight, ct);
                if (c2 != 0 || !File.Exists(rightPath))
                    throw new InvalidOperationException($"右侧切割失败: {Truncate(e2, 300)}");

                var zipPath = Path.Combine(Path.GetTempPath(), "imagestudio-split-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(leftPath, $"{baseName}_left{outExt}");
                    zip.CreateEntryFromFile(rightPath, $"{baseName}_right{outExt}");
                }
                return (zipPath, "application/zip", $"{baseName}_split.zip");
            }
            else
            {
                var crop = side == "right" ? "crop=iw/2:ih:iw/2:0" : "crop=iw/2:ih:0:0";
                var outputPath = Path.Combine(workDir, "output" + outExt);
                var args = $"-y -i \"{inputPath}\" -vf \"{crop}\" -c:a copy \"{outputPath}\"";
                var (exitCode, stderr) = await RunFfmpegAsync(args, ct);
                if (exitCode != 0 || !File.Exists(outputPath))
                    throw new InvalidOperationException($"水平切割失败 (code={exitCode}): {Truncate(stderr, 500)}");

                var finalPath = CopyToFinal(outputPath, outExt);
                var outName = $"{baseName}_{side}{outExt}";
                return (finalPath, "video/mp4", outName);
            }
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    // ---------- helpers ----------

    static async Task<(string WorkDir, string InputPath, string BaseName, string Ext)> PrepareInputAsync(
        Stream inputStream, string originalFileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext) || !SupportedVideoExt.Contains(ext))
            throw new ArgumentException($"不支持的视频格式: {ext}，支持: {string.Join(", ", SupportedVideoExt)}");

        var workDir = Path.Combine(Path.GetTempPath(), "imagestudio-vf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var inputPath = Path.Combine(workDir, "input" + ext);
        await using (var fs = File.Create(inputPath))
        {
            await inputStream.CopyToAsync(fs, ct);
        }

        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";
        return (workDir, inputPath, baseName, ext);
    }

    static string CopyToFinal(string outputPath, string ext)
    {
        var finalPath = Path.Combine(Path.GetTempPath(), "imagestudio-vout-" + Guid.NewGuid().ToString("N") + ext);
        File.Copy(outputPath, finalPath, true);
        return finalPath;
    }

    static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    static string FormatTime(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.ToString(@"hh\:mm\:ss\.fff");
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

    static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
