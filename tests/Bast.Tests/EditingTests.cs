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
    public async Task Verticals_use_the_v1_profile_real_speech_captions_and_capcut_files()
    {
        using var env = new TestEnvironment();
        var work = Path.Combine(env.Root, "trabalho", "job");
        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(work, sub));
        var plan = Plan();
        plan["voz_abaixar"] = JsonNode.Parse("[[47.5, 48.5], [466, 468]]");
        plan["verticais"] = JsonNode.Parse("""
            [ { "inicio": 453, "fim": 471, "gancho": "A POLÍCIA CERCOU... E O CARRO *VOOU*", "titulo": "O supercarro VOOU", "legenda": "Só tinha um caminho: pra cima",
                "foco": [[453, 0.5], [460, 0.56]] },
              { "inicio": 600, "fim": 620, "gancho": "FUGA A PÉ", "falas": [ { "start": 605, "end": 606.5, "text": "*CORRE*!" } ] } ]
            """);
        File.WriteAllText(SelectionStep.PlanPath(work), plan.ToJsonString());
        // Fala real: "bora bora bora" no corte 1; "teste feito" cai na voz abaixada e não pode virar legenda.
        File.WriteAllText(Path.Combine(work, ProductionFolders.Analysis, "transcricao.json"), """
            {"segments":[{"start":457.8,"end":459.6,"text":"Bora, bora, bora!","words":[
              {"start":457.85,"end":458.3,"word":"Bora,","p":0.9},{"start":458.33,"end":458.8,"word":"bora,","p":0.9},{"start":458.85,"end":459.4,"word":"bora!","p":0.9}]},
             {"start":466.2,"end":467.5,"text":"teste feito","words":[{"start":466.2,"end":466.8,"word":"teste","p":0.8},{"start":466.9,"end":467.5,"word":"feito","p":0.9}]}]}
            """);
        var job = new ProductionJob
        {
            Id = Guid.NewGuid(), SourceFileName = "gravacao.mp4", SourcePath = Path.Combine(env.Root, "gravacao.mp4"),
            DurationSeconds = 698, AudioTracks = 4, Fps = 60, Width = 1920, Height = 1080,
        };
        var tools = new FakeProductionTools();
        var step = new VerticalEditStep(tools, new BastOptions());

        var summary = await step.RunAsync(new StepContext(job, work, (_, _, _, _) => { }), default);

        Assert.Contains("2 vertical(is)", summary);
        Assert.Equal(2, tools.RenderPlans.Count);
        var first = tools.RenderPlans[0];
        Assert.Equal("vertical", first["mode"]!.ToString());
        Assert.Equal("BAST_VERTICAL_V1", first["profile"]!.ToString());
        Assert.Equal("A POLÍCIA CERCOU... E O CARRO *VOOU*", first["hook"]!.ToString());
        Assert.Equal(2, first["focus"]!.AsArray().Count);
        var captions = first["captions"]!.AsArray().Select(c => c!["text"]!.ToString()).ToList();
        Assert.Equal(["Bora, bora, bora!"], captions);
        Assert.Equal("*CORRE*!", tools.RenderPlans[1]["captions"]![0]!["text"]!.ToString());
        Assert.Equal(2, tools.Calls.Count(c => c == "render.py --previa"));

        var vertical = Path.Combine(work, ProductionFolders.Vertical);
        Assert.Equal(2, Directory.GetFiles(vertical, "*- vertical.mp4").Length);
        Assert.Equal(2, Directory.GetFiles(vertical, "*- previa.png").Length);
        var projects = Path.Combine(work, ProductionFolders.Projects);
        Assert.Equal(2, Directory.GetFiles(projects, "*trecho original 16x9.mp4").Length);
        var srt = File.ReadAllText(Directory.GetFiles(projects, "01 -*legendas.srt").Single());
        Assert.Contains("00:00:04,850 --> 00:00:06,400", srt); // 457,85 - 453
        Assert.DoesNotContain("teste", srt);
        Assert.Contains("CORRE!", File.ReadAllText(Directory.GetFiles(projects, "02 -*legendas.srt").Single()));
        Assert.Contains("Só tinha um caminho", File.ReadAllText(Directory.GetFiles(vertical, "01 -*.txt").Single()));

        // Retomada: nada é renderizado de novo.
        tools.Calls.Clear();
        tools.RenderPlans.Clear();
        await step.RunAsync(new StepContext(job, work, (_, _, _, _) => { }), default);
        Assert.Empty(tools.RenderPlans);
    }

    /// <summary>PNG mínimo (assinatura + IHDR) com a largura e a altura pedidas.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    [Fact]
    public async Task Thumbnail_delivery_is_checked_against_youtube_and_the_profile()
    {
        using var env = new TestEnvironment();
        var work = Path.Combine(env.Root, "trabalho", "job");
        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(work, sub));
        var folder = Path.Combine(work, ProductionFolders.Thumbnails);
        var job = new ProductionJob { Id = Guid.NewGuid(), SourceFileName = "g.mp4", DurationSeconds = 698 };
        var context = new StepContext(job, work, (_, _, _, _) => { });
        var step = new ThumbnailStep();

        var missing = await step.CheckAsync(context, default);
        Assert.Contains(missing.Problems, p => p.Contains("thumbnail.png"));
        Assert.Contains(missing.Problems, p => p.Contains("thumbnail.json"));

        File.WriteAllBytes(Path.Combine(folder, "thumbnail.png"), Png(1920, 1080));
        File.WriteAllText(Path.Combine(folder, "thumbnail.json"), """
            { "conceito": "D", "frase": "UMA FRASE LONGA DEMAIS PRA THUMB", "segundo": 461.5, "motivo": "",
              "alternativas": [ { "conceito": "B", "arquivo": "conceito B.png" } ] }
            """);
        var bad = await step.CheckAsync(context, default);
        Assert.Contains(bad.Problems, p => p.Contains("1920x1080"));
        Assert.Contains(bad.Problems, p => p.Contains("palavra"));
        Assert.Contains(bad.Problems, p => p.Contains("conceito"));
        Assert.Contains(bad.Problems, p => p.Contains("motivo"));
        Assert.Contains(bad.Problems, p => p.Contains("conceito B.png"));

        File.WriteAllBytes(Path.Combine(folder, "thumbnail.png"), Png(1280, 720));
        File.WriteAllBytes(Path.Combine(folder, "conceito B.png"), Png(1280, 720));
        File.WriteAllText(Path.Combine(folder, "thumbnail.json"), """
            { "conceito": "A", "frase": "O CARRO VOOU!", "segundo": 461.5, "motivo": "Carro grande, reação no canto.",
              "alternativas": [ { "conceito": "B", "arquivo": "conceito B.png" } ] }
            """);
        var ok = await step.CheckAsync(context, default);
        Assert.Empty(ok.Problems);
        Assert.Null(ok.Review);
        Assert.Contains("conceito A", ok.Summary);
        Assert.Contains("1 alternativa", ok.Summary);
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
