using System.Text.Json.Nodes;
using Bast.Application;
using Bast.Application.Production;
using Bast.Domain;

namespace Bast.Tests;

/// <summary>Ferramentas falsas: criam os arquivos que o ffmpeg e os scripts criariam.</summary>
public sealed class FakeProductionTools : IProductionTools
{
    public string? Ffmpeg { get; set; } = "ffmpeg.exe";
    public string? Python { get; set; } = "python.exe";
    public string ScriptsFolder => "scripts";
    public int Frames { get; set; } = 20;
    public List<string> Calls { get; } = [];
    public bool Nudity { get; set; }
    public string? FailScript { get; set; }

    public Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Calls.Add("ffmpeg " + string.Join(' ', arguments.Where(a => a.Contains('.') && !a.StartsWith('-')).Select(Path.GetFileName)));
        foreach (var arg in arguments)
        {
            if (arg.EndsWith(".part.wav")) File.WriteAllText(arg, "wav");
            else if (arg.Contains("%04d.jpg"))
            {
                for (var i = 1; i <= Frames; i++) File.WriteAllText(arg.Replace("%04d", i.ToString("D4")), "jpg");
            }
            else if (arg.Contains("%02d.jpg")) File.WriteAllText(arg.Replace("%02d", "01"), "jpg");
        }

        return Task.FromResult(new ToolResult(0, "", ""));
    }

    public Task<ToolResult> RunScriptAsync(string script, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        Calls.Add(script);
        if (script == FailScript) return Task.FromResult(new ToolResult(1, "", "Traceback\nRuntimeError: falhou de propósito"));
        var (output, json) = script switch
        {
            "transcrever.py" => (arguments[1], """{"model":"small","device":"cuda","segments":[{"start":1,"end":2,"text":"bora","words":[{"start":1,"end":2,"word":"bora","p":0.9}]}]}"""),
            "eventos_audio.py" => (arguments[1], """{"events":[{"start":5,"end":6,"label":"tosse","score":0.8,"db":-20}]}"""),
            "presenca_camera.py" => (arguments[1], """{"seconds":[{"t":0,"face":true},{"t":1,"face":false}],"absent":[{"start":10,"end":14}]}"""),
            "nudez.py" => (arguments[1], Nudity ? """{"flags":[{"start":7,"end":9,"classes":["FEMALE_BREAST_EXPOSED"],"score":0.7}]}""" : """{"flags":[]}"""),
            "sinais.py" => (arguments[3], """{"black":[],"freeze":[],"scene_changes":[3.2,8.1],"voice_peaks":[{"t":4.0,"lufs":-12}]}"""),
            _ => throw new InvalidOperationException(script),
        };
        File.WriteAllText(output, json);
        return Task.FromResult(new ToolResult(0, "ok", ""));
    }
}

public class MediaAnalysisTests
{
    private static (StepContext Context, List<string> Logs, string Work) Context(TestEnvironment env, int audioTracks = 4)
    {
        var work = Path.Combine(env.Root, "trabalho", "job");
        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(work, sub));
        var job = new ProductionJob
        {
            Id = Guid.NewGuid(), SourceFileName = "gravacao.mp4", SourcePath = Path.Combine(env.Root, "gravacao.mp4"),
            DurationSeconds = 20, AudioTracks = audioTracks,
        };
        var logs = new List<string>();
        return (new StepContext(job, work, (_, message, _, _) => logs.Add(message)), logs, work);
    }

    [Fact]
    public async Task Analysis_produces_every_output_and_a_summary_with_alerts()
    {
        using var env = new TestEnvironment();
        var tools = new FakeProductionTools { Nudity = true };
        var step = new MediaAnalysisStep(tools, new BastOptions());
        var (context, _, work) = Context(env);

        var summary = await step.RunAsync(context, default);

        var dir = Path.Combine(work, ProductionFolders.Analysis);
        foreach (var file in new[] { "game.wav", "mic.wav", "mic16.wav", "transcricao.json", "eventos_audio.json", "camera.json", "nudez.json", "sinais.json", "analise.json" })
        {
            Assert.True(File.Exists(Path.Combine(dir, file)), file);
        }

        Assert.Equal(20, Directory.GetFiles(Path.Combine(dir, "frames")).Length);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(dir, "cam")).Length);
        Assert.Equal(100, context.Progress);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "analise.json")))!;
        Assert.Equal(1, manifest["fala"]!["trechos"]!.GetValue<int>());
        Assert.Equal(1, manifest["eventos_audio"]!["tosse"]!.GetValue<int>());
        var alerts = manifest["alertas"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains(alerts, a => a.Contains("conteúdo explícito"));
        Assert.Contains(alerts, a => a.Contains("fora da câmera"));
        Assert.Contains("suspeita(s) de conteúdo explícito", summary);
        Assert.Contains(tools.Calls, c => c.Contains("game.wav.part.wav"));
    }

    [Fact]
    public async Task Resuming_keeps_what_was_already_done()
    {
        using var env = new TestEnvironment();
        var tools = new FakeProductionTools { FailScript = "nudez.py" };
        var step = new MediaAnalysisStep(tools, new BastOptions());
        var (context, _, _) = Context(env);

        var error = await Assert.ThrowsAsync<ProductionStepException>(() => step.RunAsync(context, default));
        Assert.True(error.Retry);
        Assert.Contains("falhou de propósito", error.Message);
        Assert.Contains("transcrever.py", tools.Calls);

        tools.Calls.Clear();
        tools.FailScript = null;
        var (resumed, logs, _) = Context(env);
        await step.RunAsync(resumed, default);

        // Só o que faltava rodou: nada de extrair áudio, quadros ou transcrever de novo.
        Assert.Equal(["nudez.py", "sinais.py"], tools.Calls);
        Assert.Contains(logs, l => l.Contains("já feito, mantido"));
    }

    [Fact]
    public async Task Missing_python_environment_stops_with_instructions()
    {
        using var env = new TestEnvironment();
        var step = new MediaAnalysisStep(new FakeProductionTools { Python = null }, new BastOptions());
        var (context, _, _) = Context(env);

        var error = await Assert.ThrowsAsync<ProductionStepException>(() => step.RunAsync(context, default));
        Assert.False(error.Retry);
        Assert.Contains("instalar-producao.ps1", error.Message);
    }

    [Fact]
    public async Task Recording_without_separate_tracks_uses_the_mix_and_warns()
    {
        using var env = new TestEnvironment();
        var tools = new FakeProductionTools();
        var step = new MediaAnalysisStep(tools, new BastOptions());
        var (context, logs, work) = Context(env, audioTracks: 1);

        await step.RunAsync(context, default);

        Assert.Contains(logs, l => l.Contains("sem faixas separadas"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(work, ProductionFolders.Analysis, "analise.json")))!;
        Assert.Equal(0, manifest["faixas"]!["microfone"]!.GetValue<int>());
    }
}
