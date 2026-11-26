using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 4a: seleção dos melhores momentos e plano de edição, feita pelo Claude (tarefa agendada de produção)
/// com base na análise automática (quadros, folhas, transcrição, eventos de áudio, câmera, sinais). O Claude grava
/// "1 - analise\plano_edicao.json" e entrega a etapa pela API; aqui o plano é conferido antes de valer:
/// tempos dentro da gravação, trechos coerentes, e nenhuma cena sensível dentro do que será usado sem corte
/// (blur ainda não é aplicado automaticamente: cena que pediria blur vai para revisão humana).
/// </summary>
public sealed class SelectionStep : IExternalStep
{
    public const string StepName = "selecao";
    public const string PlanFile = "plano_edicao.json";
    public const double MinHorizontalSeconds = 60;

    public string Name => StepName;
    public string Title => "Seleção dos melhores momentos e plano de edição";
    public ProductionStatus Status => ProductionStatus.SelectingHighlights;

    public string WaitingText =>
        "Aguardando a seleção dos momentos pelo Claude (tarefa \"BAST – Produção\", com o app do Claude aberto).";

    public Task<string> RunAsync(StepContext context, CancellationToken ct) =>
        throw new InvalidOperationException("A seleção é feita pelo Claude e entregue pela API; o executor não a roda.");

    public static string PlanPath(string workFolder) => Path.Combine(workFolder, ProductionFolders.Analysis, PlanFile);

    public async Task<ExternalStepCheck> CheckAsync(StepContext context, CancellationToken ct)
    {
        var path = PlanPath(context.WorkFolder);
        if (!File.Exists(path)) return new([$"{PlanFile} não encontrado em 1 - analise."], "");

        JsonNode? plan;
        try
        {
            plan = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (JsonException ex)
        {
            return new([$"{PlanFile} não é um JSON válido: {ex.Message}"], "");
        }

        return Check(plan, context.Job.DurationSeconds);
    }

    /// <summary>Confere o plano de edição. Separado para os testes.</summary>
    public static ExternalStepCheck Check(JsonNode? plan, double duration)
    {
        var problems = new List<string>();
        var reviews = new List<string>();
        if (plan is not JsonObject root) return new(["O plano de edição precisa ser um objeto JSON."], "");

        var trechos = Ranges(root["horizontal"]?["trechos"], "horizontal.trechos", duration, problems);
        if (trechos.Count == 0) problems.Add("horizontal.trechos está vazio.");
        var total = trechos.Sum(r => r.End - r.Start);
        if (trechos.Count > 0 && total < MinHorizontalSeconds) problems.Add($"O horizontal teria só {total:0} s (mínimo {MinHorizontalSeconds:0} s).");

        (double Start, double End)? teaser = null;
        if (root["horizontal"]?["teaser"] is JsonArray teaserNode)
        {
            var list = Ranges(new JsonArray(teaserNode.DeepClone()), "horizontal.teaser", duration, problems);
            if (list.Count == 1)
            {
                teaser = list[0];
                if (list[0].End - list[0].Start > 15) problems.Add("O teaser passa de 15 s.");
            }
        }

        Ranges(root["webcam_oculta"], "webcam_oculta", duration, problems);
        Ranges(root["voz_abaixar"], "voz_abaixar", duration, problems);

        var verticals = new List<(double Start, double End)>();
        if (root["verticais"] is JsonArray verticalNodes)
        {
            foreach (var (node, i) in verticalNodes.Select((n, i) => (n, i + 1)))
            {
                var start = Number(node?["inicio"]);
                var end = Number(node?["fim"]);
                if (start is null || end is null || start < 0 || end > duration + 0.5 || end <= start)
                {
                    problems.Add($"verticais[{i}]: início/fim inválidos.");
                    continue;
                }

                if (end - start is < 8 or > 90) problems.Add($"verticais[{i}]: duração de {end - start:0} s (entre 8 e 90 s).");
                if (string.IsNullOrWhiteSpace(node?["gancho"]?.ToString())) problems.Add($"verticais[{i}]: sem gancho.");
                if (node?["foco"] is JsonArray focus && focus.Any(k => k is not JsonArray { Count: 2 } pair
                        || Number(pair[1]) is not (>= 0 and <= 1)))
                {
                    problems.Add($"verticais[{i}]: foco deve ser [[segundo, x de 0 a 1], ...].");
                }

                verticals.Add((start.Value, end.Value));
            }
        }

        if (root["thumbnail"]?["segundo"] is { } thumb && Number(thumb) is not { } second)
        {
            problems.Add("thumbnail.segundo inválido.");
        }
        else if (Number(root["thumbnail"]?["segundo"]) is { } ts && (ts < 0 || ts > duration))
        {
            problems.Add("thumbnail.segundo fora da gravação.");
        }

        // Cenas sensíveis: "cortar" não pode estar no que será usado; "blur" e "revisao_humana" dentro do que será
        // usado param o job para revisão humana (o blur automático com rastreamento ainda não existe).
        var used = trechos.Concat(verticals).Concat(teaser is { } t ? [t] : []).ToList();
        if (root["sensivel"] is JsonArray sensitive)
        {
            foreach (var (node, i) in sensitive.Select((n, i) => (n, i + 1)))
            {
                var start = Number(node?["inicio"]);
                var end = Number(node?["fim"]);
                var action = (node?["acao"]?.ToString() ?? "").ToLowerInvariant();
                var what = node?["descricao"]?.ToString() ?? node?["tipo"]?.ToString() ?? "cena sensível";
                if (start is null || end is null || end <= start)
                {
                    problems.Add($"sensivel[{i}]: início/fim inválidos.");
                    continue;
                }

                var inUse = used.Any(u => Math.Max(u.Start, start.Value) < Math.Min(u.End, end.Value));
                if (!inUse) continue;
                if (action == "cortar") problems.Add($"sensivel[{i}] ({what}) está marcada para cortar, mas continua dentro dos trechos usados.");
                else reviews.Add($"{what} em {Clock(start.Value)}–{Clock(end.Value)} ({action}) está dentro do que será usado.");
            }
        }

        if (root["revisao_humana"]?.GetValue<bool>() == true)
        {
            var alerts = root["alertas"] is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString())) : "";
            reviews.Add("O Claude pediu revisão humana. " + alerts);
        }

        var summary = $"{trechos.Count} trechos no horizontal ({total / 60:0.0} min)" +
                      (teaser is { } tz ? $", teaser de {tz.End - tz.Start:0} s" : ", sem teaser") +
                      $", {verticals.Count} corte(s) vertical(is)" +
                      $", webcam oculta em {(root["webcam_oculta"] as JsonArray)?.Count ?? 0} trecho(s)" +
                      $", voz abaixada em {(root["voz_abaixar"] as JsonArray)?.Count ?? 0} ponto(s).";
        return new(problems, summary, reviews.Count > 0 ? string.Join(" ", reviews) : null);
    }

    private static List<(double Start, double End)> Ranges(JsonNode? node, string field, double duration, List<string> problems)
    {
        var list = new List<(double, double)>();
        if (node is null) return list;
        if (node is not JsonArray array)
        {
            problems.Add($"{field} deve ser uma lista de [início, fim].");
            return list;
        }

        // Aceita uma lista de pares ou um par só (o teaser).
        IEnumerable<JsonNode?> items = array.Count == 2 && array[0] is JsonValue && array[1] is JsonValue ? [array] : array;
        foreach (var (item, i) in items.Select((x, i) => (x, i + 1)))
        {
            if (item is not JsonArray { Count: 2 } pair || Number(pair[0]) is not { } a || Number(pair[1]) is not { } b)
            {
                problems.Add($"{field}[{i}] deve ser [início, fim] em segundos.");
                continue;
            }

            if (a < 0 || b > duration + 0.5 || b <= a)
            {
                problems.Add($"{field}[{i}] = [{a:0.##}, {b:0.##}] fora da gravação ({duration:0} s) ou com fim antes do início.");
                continue;
            }

            list.Add((a, Math.Min(b, duration)));
        }

        return list;
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
