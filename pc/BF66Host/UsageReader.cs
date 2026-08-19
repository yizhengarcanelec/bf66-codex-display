using System.Globalization;
using System.Text.Json;

namespace BF66Host;

internal sealed record UsageSnapshot(
    long Input,
    long Cached,
    long Output,
    long Reasoning,
    long Total,
    bool Partial,
    bool HasWeekly,
    double UsedPercent,
    double RemainingPercent,
    DateTimeOffset? ResetAt,
    string Plan,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset GeneratedAt);

internal static class UsageReader
{
    public static UsageSnapshot Read()
    {
        long input = 0, cached = 0, output = 0, reasoning = 0, total = 0;
        var generatedAt = DateTimeOffset.Now;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexRoot = string.IsNullOrWhiteSpace(configured) ? Path.Combine(profile, ".codex") : Path.GetFullPath(configured);
        var roots = new[] { Path.Combine(codexRoot, "sessions"), Path.Combine(codexRoot, "archived_sessions") };
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        DateTimeOffset? earliest = null;
        DateTimeOffset? lastActivityAt = null;
        DateTimeOffset? latestWeeklyStamp = null;
        JsonElement latestWeekly = default;
        var hasLatestWeekly = false;
        var latestPlan = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToArray(); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var record = document.RootElement;
                            if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                            if (!payload.TryGetProperty("type", out var type) || type.GetString() != "token_count") continue;
                            if (!record.TryGetProperty("timestamp", out var timestampElement) ||
                                !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;

                            if (!earliest.HasValue || stamp < earliest.Value) earliest = stamp;
                            if (!lastActivityAt.HasValue || stamp > lastActivityAt.Value) lastActivityAt = stamp;
                            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                                info.TryGetProperty("last_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            {
                                var local = stamp.LocalDateTime;
                                if (local >= today && local < tomorrow)
                                {
                                    var eventKey = Path.GetFileName(file) + "|" + stamp.ToString("O") + "|" + Long(usage, "total_tokens") + "|" + Long(usage, "input_tokens");
                                    if (seen.Add(eventKey))
                                    {
                                        input += Long(usage, "input_tokens");
                                        cached += Long(usage, "cached_input_tokens");
                                        output += Long(usage, "output_tokens");
                                        reasoning += Long(usage, "reasoning_output_tokens");
                                        total += Long(usage, "total_tokens");
                                    }
                                }
                            }

                            if (!payload.TryGetProperty("rate_limits", out var rate) || rate.ValueKind != JsonValueKind.Object) continue;
                            JsonElement selected = default;
                            var hasSelected = false;
                            foreach (var key in new[] { "primary", "secondary" })
                            {
                                if (!rate.TryGetProperty(key, out var candidate) || candidate.ValueKind != JsonValueKind.Object) continue;
                                if (Long(candidate, "window_minutes") < 10000) continue;
                                if (!hasSelected || Long(candidate, "window_minutes") > Long(selected, "window_minutes"))
                                {
                                    selected = candidate;
                                    hasSelected = true;
                                }
                            }

                            if (hasSelected && (!latestWeeklyStamp.HasValue || stamp > latestWeeklyStamp.Value))
                            {
                                latestWeeklyStamp = stamp;
                                latestWeekly = selected.Clone();
                                hasLatestWeekly = true;
                                latestPlan = rate.TryGetProperty("plan_type", out var plan) ? plan.GetString() ?? "" : "";
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        var used = hasLatestWeekly ? Math.Round(Double(latestWeekly, "used_percent"), 1) : 0;
        DateTimeOffset? resetAt = null;
        if (hasLatestWeekly)
        {
            var seconds = Long(latestWeekly, "resets_at");
            if (seconds > 0) resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }

        return new UsageSnapshot(
            input, cached, output, reasoning, total,
            earliest.HasValue && earliest.Value.LocalDateTime > today.AddMinutes(5),
            hasLatestWeekly, used, Math.Round(Math.Max(0, 100 - used), 1), resetAt, latestPlan, lastActivityAt, generatedAt);
    }

    private static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static double Double(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
}
