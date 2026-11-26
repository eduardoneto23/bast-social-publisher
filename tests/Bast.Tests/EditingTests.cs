using System.Text.Json.Nodes;
using System.Xml.Linq;
using Bast.Application;
using Bast.Application.Production;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public class EditingTests
{
    private static JsonObject Plan(string extra = "") => JsonNode.Parse($$"""
    {
      "titulo": "GTA 6 – Roubei um SUPERCARRO",
      "descricao": "Fuga com salto.",
      "hashtags": ["#gta6", "#gtavi"],
      "horizontal": { "teaser": [461, 471], "trechos": [[11, 90], [134, 141], [268, 300], [330, 384]] },
      "webcam_oculta": [[116, 201]],
      "voz_abaixar": [[47.5, 48.5]],
      "verticais": [ { "inicio": 453, "fim": 471, "gancho": "A POLÍCIA CERCOU...", "foco": [[453, 0.5], [460, 0.56]] } ],
      "thumbnail": { "segundo": 461.5, "frase": "O CARRO VOOU!" },
      "sensivel": [] {{extra}}
    }
    """)!.AsObject();

    [Fact]
    public void Valid_plan_passes_with_a_summary()
    {
        var check = SelectionStep.Check(Plan(), 698);
        Assert.Empty(check.Problems);
        Assert.Null(check.Review);
        Assert.Contains("4 trechos", check.Summary);
        Assert.Contains("teaser de 10 s", check.Summary);
        Assert.Contains("1 corte(s) vertical(is)", check.Summary);
    }

    [Fact]
    public void Bad_times_and_too_short_cuts_are_refused()
    {
        var plan = Plan();
        plan["horizontal"]!["trechos"] = JsonNode.Parse("[[10, 5], [690, 720], [20, 30]]");
        plan["verticais"] = JsonNode.Parse("""[{ "inicio": 100, "fim": 103, "gancho": "" }]""");
        plan["thumbnail"]!["segundo"] = 900;

        var check = SelectionStep.Check(plan, 698);

        Assert.Contains(check.Problems, p => p.Contains("trechos[1]"));
        Assert.Contains(check.Problems, p => p.Contains("trechos[2]"));
        Assert.Contains(check.Problems, p => p.Contains("mínimo 60 s"));
        Assert.Contains(check.Problems, p => p.Contains("verticais[1]: duração"));
        Assert.Contains(check.Problems, p => p.Contains("sem gancho"));
        Assert.Contains(check.Problems, p => p.Contains("thumbnail.segundo"));
    }

    [Fact]
    public void Sensitive_scene_inside_the_cut_stops_for_review_and_cut_marking_must_be_honored()
    {
        var plan = Plan();
        // Dentro do trecho 330-384: o blur automático não existe, então para em revisão humana.
        plan["sensivel"] = JsonNode.Parse("""[ { "inicio": 340, "fim": 350, "tipo": "nudez", "acao": "blur", "descricao": "dançarina" } ]""");
        var blur = SelectionStep.Check(plan, 698);
        Assert.Empty(blur.Problems);
        Assert.Contains("dançarina", blur.Review);

        plan["sensivel"] = JsonNode.Parse("""[ { "inicio": 20, "fim": 25, "acao": "cortar", "descricao": "sangue" } ]""");
        Assert.Contains(SelectionStep.Check(plan, 698).Problems, p => p.Contains("sangue"));

        plan["sensivel"] = JsonNode.Parse("""[ { "inicio": 600, "fim": 610, "acao": "cortar" } ]""");
        Assert.Empty(SelectionStep.Check(plan, 698).Problems); // fora do que é usado: ok
    }

    [Fact]
    public async Task Delivery_is_checked_refused_with_the_reason_or_stopped_for_review()
    {
        var blocking = new BlockingStep("analise-midia");
        using var h = new ProductionHarness(blocking);
        var pipeline = new ProductionPipeline([new IntegrityStep(h.Env.Analyzer), new SelectionStep()]);
        var runner = new ProductionRunner(h.Env.DbFactory, pipeline, h.Options, h.Disk, h.Env.Time, NullLogger<ProductionRunner>.Instance);
        var commands = new ProductionCommands(h.Env.DbFactory, runner, pipeline, h.Env.Time);
        h.Env.Analyzer.Handler = _ => Bast.Application.Import.VideoAnalysisResult.Ok(new Bast.Application.Import.VideoAnalysis(698, 1920, 1080, "hevc", "aac", 60, null, "mp4", 4));
        h.Record("2026-11-26 20-00-00.mp4");
        await h.ScanUntilStableAsync();
        var id = h.Job("2026-11-26 20-00-00.mp4").Id;
        await commands.AuthorizeAsync(id, default);
        await runner.RunCycleAsync(default); // integridade
        // A análise é marcada como feita (o teste cobre só a seleção).
        using (var db = h.Env.DbFactory.CreateDbContext())
        {
            foreach (var s in db.ProductionSteps.Where(s => s.JobId == id && s.Name == "analise-midia")) s.State = ProductionStepState.Done;
            db.SaveChanges();
        }

        await runner.RunCycleAsync(default);
        var job = h.Job("2026-11-26 20-00-00.mp4");
        Assert.Equal(ProductionStatus.SelectingHighlights, job.Status);
        Assert.Contains("Claude", job.CurrentStep);

        // Sem o arquivo: recusado, continua esperando.
        var (refused, _, problems) = await commands.CompleteExternalAsync(id, SelectionStep.StepName, default);
        Assert.Equal(CommandResult.NotAllowed, refused);
        Assert.Contains("plano_edicao.json", problems.Single());

        // Plano com cena que pediria blur dentro do corte: entregue, mas parado para revisão humana.
        var plan = Plan();
        plan["sensivel"] = JsonNode.Parse("""[ { "inicio": 340, "fim": 350, "acao": "blur", "descricao": "nudez no jogo" } ]""");
        File.WriteAllText(SelectionStep.PlanPath(job.WorkFolder!), plan.ToJsonString());
        var (done, message, _) = await commands.CompleteExternalAsync(id, SelectionStep.StepName, default);
        Assert.Equal(CommandResult.Done, done);
        Assert.Contains("revisão", message);
        job = h.Job("2026-11-26 20-00-00.mp4");
        Assert.Equal(ProductionStatus.NeedsHumanReview, job.Status);
        Assert.Equal(ProductionStepState.Done, job.Steps.Single(s => s.Name == SelectionStep.StepName).State);
        Assert.False(await runner.RunCycleAsync(default)); // parado

        Assert.Equal(CommandResult.Done, (await commands.ContinueAfterReviewAsync(id, default)).Result);
        Assert.NotEqual(ProductionStatus.NeedsHumanReview, h.Job("2026-11-26 20-00-00.mp4").Status);
    }

    [Fact]
    public async Task Horizontal_edit_renders_from_the_plan_and_writes_project_and_metadata()
    {
        using var env = new TestEnvironment();
        var work = Path.Combine(env.Root, "trabalho", "job");
        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(work, sub));
        File.WriteAllText(SelectionStep.PlanPath(work), Plan().ToJsonString());
        var job = new ProductionJob
        {
            Id = Guid.NewGuid(), SourceFileName = "gravacao.mp4", SourcePath = Path.Combine(env.Root, "gravacao.mp4"),
            DurationSeconds = 698, AudioTracks = 4, Fps = 60, Width = 1920, Height = 1080,
        };
        var tools = new FakeProductionTools();
        var step = new HorizontalEditStep(tools, new BastOptions());

        var summary = await step.RunAsync(new StepContext(job, work, (_, _, _, _) => { }), default);

        var plan = tools.LastRenderPlan!;
        Assert.Equal("horizontal", plan["mode"]!.ToString());
        Assert.Equal(5, plan["segments"]!.AsArray().Count); // teaser + 4 trechos
        Assert.Equal(461, plan["segments"]![0]![0]!.GetValue<double>());
        Assert.Equal(2, plan["mic_track"]!.GetValue<int>());
        Assert.Contains("1 com a webcam oculta", summary);

        var horizontal = Path.Combine(work, ProductionFolders.Horizontal);
        var mp4 = Directory.GetFiles(horizontal, "*.mp4").Single();
        Assert.Contains("GTA 6", Path.GetFileName(mp4));
        Assert.Contains("#gta6", File.ReadAllText(Path.ChangeExtension(mp4, null) + ".txt"));

        var fcpxml = Directory.GetFiles(Path.Combine(work, ProductionFolders.Projects), "*.fcpxml").Single();
        var xml = XDocument.Load(fcpxml);
        var clips = xml.Descendants("asset-clip").ToList();
        Assert.Equal(5, clips.Count);
        Assert.Equal("27660/60s", clips[0].Attribute("start")!.Value); // 461 s
        Assert.Equal("600/60s", clips[1].Attribute("offset")!.Value);  // depois dos 10 s do teaser
        Assert.Single(xml.Descendants("marker"));
        Assert.StartsWith("file:///", xml.Descendants("media-rep").Single().Attribute("src")!.Value);

        // Retomada: vídeo já renderizado não é renderizado de novo.
        tools.Calls.Clear();
        await step.RunAsync(new StepContext(job, work, (_, _, _, _) => { }), default);
        Assert.DoesNotContain("render.py", tools.Calls);
    }
}
