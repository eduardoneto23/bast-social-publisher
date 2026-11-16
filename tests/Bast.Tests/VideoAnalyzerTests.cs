using System.Diagnostics;
using Bast.Application;
using Bast.Application.Import;
using Bast.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace Bast.Tests;

/// <summary>Testes com ffprobe/ffmpeg reais. Exigem os executáveis no PATH.</summary>
public class VideoAnalyzerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bast-tests", "media-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = new(Options.Create(new BastOptions()));
    private readonly FfprobeVideoAnalyzer _analyzer;

    public VideoAnalyzerTests()
    {
        Directory.CreateDirectory(_dir);
        _analyzer = new FfprobeVideoAnalyzer(_tools);
        Assert.True(_tools.Ffmpeg is not null && _tools.Ffprobe is not null, "ffmpeg e ffprobe precisam estar no PATH para estes testes.");
    }

    [Fact]
    public async Task Valid_vertical_video_is_accepted_with_metadata()
    {
        var path = Generate("ok.mp4", seconds: 8, fastStart: true);

        var result = await _analyzer.AnalyzeAsync(path, default);

        Assert.Equal(VideoAnalysisStatus.Ok, result.Status);
        var a = result.Analysis!;
        Assert.Equal(540, a.Width);
        Assert.Equal(960, a.Height);
        Assert.Equal("h264", a.VideoCodec);
        Assert.Equal("aac", a.AudioCodec);
        Assert.InRange(a.DurationSeconds, 7.5, 8.5);
        Assert.InRange(a.FrameRate, 29, 31);
    }

    [Fact]
    public async Task Short_video_is_accepted()
    {
        var path = Generate("curto.mp4", seconds: 2, fastStart: false);
        Assert.Equal(VideoAnalysisStatus.Ok, (await _analyzer.AnalyzeAsync(path, default)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Truncated_copy_is_rejected(bool fastStart)
    {
        var path = Generate("inteiro.mp4", seconds: 8, fastStart);
        var truncated = Path.Combine(_dir, "truncado.mp4");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length * 6 / 10)]);

        var result = await _analyzer.AnalyzeAsync(truncated, default);

        Assert.Equal(VideoAnalysisStatus.Invalid, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task File_that_is_not_a_video_is_rejected()
    {
        var path = Path.Combine(_dir, "falso.mp4");
        File.WriteAllText(path, "isto não é um vídeo");
        Assert.Equal(VideoAnalysisStatus.Invalid, (await _analyzer.AnalyzeAsync(path, default)).Status);
    }

    [Fact]
    public async Task Missing_tools_are_reported_as_unavailable_not_as_invalid_video()
    {
        var path = Generate("ok.mp4", seconds: 2, fastStart: false);
        var analyzer = new FfprobeVideoAnalyzer(new MediaTools(Options.Create(new BastOptions { FfprobePath = @"C:\nao-existe\ffprobe.exe" })));
        Assert.Equal(VideoAnalysisStatus.ToolUnavailable, (await analyzer.AnalyzeAsync(path, default)).Status);
    }

    private string Generate(string name, int seconds, bool fastStart)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(_tools.Ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        string[] args =
        [
            "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=540x960:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
        ];
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (fastStart)
        {
            psi.ArgumentList.Add("-movflags");
            psi.ArgumentList.Add("+faststart");
        }

        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
