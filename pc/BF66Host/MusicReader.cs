using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace BF66Host;

internal sealed record LyricLine(long AtMs, string Text);

internal sealed record LyricsPayload(IReadOnlyList<LyricLine> Lines, string Source, long DurationMs);

internal sealed record MusicCover(byte[] Bytes, string ContentType, string Version);

internal sealed record MusicSnapshot(
    bool Available,
    string Source,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    long PositionMs,
    long DurationMs,
    bool HasCover,
    string CoverVersion,
    string PreviousLyric,
    string CurrentLyric,
    string NextLyric,
    long CurrentLyricEndMs,
    string LyricSource,
    string Accent,
    string Status,
    DateTimeOffset GeneratedAt);

internal static class MusicReader
{
    private const int MaxCoverBytes = 8 * 1024 * 1024;
    private static readonly byte[] KrcKey = { 0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69 };
    private static readonly HttpClient LyricsClient = CreateLyricsClient();
    private static readonly SemaphoreSlim LyricsGate = new(1, 1);
    private static readonly SemaphoreSlim CoverGate = new(1, 1);
    private static readonly object CoverSync = new();
    private static readonly object PositionSync = new();
    private static string _lyricKey = "";
    private static LyricsPayload _lyrics = EmptyLyrics();
    private static string _coverKey = "";
    private static MusicCover? _cover;
    private static string _estimatedTrackKey = "";
    private static long _estimatedPositionMs;
    private static DateTimeOffset _estimatedAt;
    private static bool _estimatedPlaying;
    private static volatile bool _onlineLyricsEnabled;

    public static void SetOnlineLyricsEnabled(bool enabled) => _onlineLyricsEnabled = enabled;

    public static bool TryGetCover(out MusicCover? cover)
    {
        lock (CoverSync)
        {
            cover = _cover;
            return cover is not null;
        }
    }

    public static async Task<MusicSnapshot> ReadAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().FirstOrDefault(IsKuGou);
            if (session is null) return Empty("未发现酷狗播放会话");

            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var title = Clean(properties.Title, "未知歌曲");
            var artist = Clean(properties.Artist, "未知歌手");
            var album = Clean(properties.AlbumTitle, "酷狗音乐");
            var trackKey = title + "" + artist + "" + album;
            var reportedPosition = Math.Max(0, (long)timeline.Position.TotalMilliseconds);
            var reportedDuration = Math.Max(
                Math.Max(0, (long)timeline.EndTime.TotalMilliseconds),
                Math.Max(0, (long)timeline.MaxSeekTime.TotalMilliseconds));
            var cover = await GetCoverAsync(trackKey, properties.Thumbnail);
            var lyrics = await GetLyricsAsync(title, artist, reportedDuration);
            var duration = reportedDuration > 0 ? reportedDuration : lyrics.DurationMs;
            if (duration <= 0 && lyrics.Lines.Count > 0) duration = lyrics.Lines[^1].AtMs + 5000;
            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var missingTimeline = reportedPosition == 0 && reportedDuration == 0;
            var usesEstimatedPosition = missingTimeline && duration > 0;
            var position = usesEstimatedPosition
                ? EstimatePosition(trackKey, isPlaying, duration)
                : SyncReportedPosition(trackKey, isPlaying, reportedPosition, duration);
            var (previous, current, next, currentLyricEndMs) = SelectLyrics(lyrics.Lines, position, duration, lyrics.Source);
            var timeStatus = missingTimeline
                ? usesEstimatedPosition ? "酷狗未提供时间轴，已从识别到曲目起连续计时" : "酷狗未提供时间轴，等待歌词补齐总时长"
                : "播放进度已同步";

            return new MusicSnapshot(
                true, "酷狗音乐", title, artist, album, isPlaying, position, duration,
                cover is not null, cover?.Version ?? "", previous, current, next, currentLyricEndMs, lyrics.Source,
                AccentFor(title + artist),
                (cover is null ? "未提供专辑封面" : "专辑封面已同步") + " · " + lyrics.Source + " · " + timeStatus,
                DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return Empty("读取酷狗状态失败：" + ShortMessage(ex.Message));
        }
    }

    public static async Task<bool> ExecuteAsync(string command)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().FirstOrDefault(IsKuGou);
            if (session is null) return false;
            return command switch
            {
                "toggle" => await session.TryTogglePlayPauseAsync(),
                "previous" => await session.TrySkipPreviousAsync(),
                "next" => await session.TrySkipNextAsync(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> SeekAsync(long positionMs)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions().FirstOrDefault(IsKuGou);
            if (session is null) return false;
            var accepted = await session.TryChangePlaybackPositionAsync(Math.Max(0, positionMs) * TimeSpan.TicksPerMillisecond);
            if (accepted)
            {
                lock (PositionSync)
                {
                    _estimatedPositionMs = Math.Max(0, positionMs);
                    _estimatedAt = DateTimeOffset.UtcNow;
                }
            }
            return accepted;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKuGou(GlobalSystemMediaTransportControlsSession session) =>
        session.SourceAppUserModelId.Contains("kugou", StringComparison.OrdinalIgnoreCase) ||
        session.SourceAppUserModelId.Contains("kgmusic", StringComparison.OrdinalIgnoreCase);

    private static long EstimatePosition(string trackKey, bool isPlaying, long durationMs)
    {
        lock (PositionSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (trackKey != _estimatedTrackKey)
            {
                _estimatedTrackKey = trackKey;
                _estimatedPositionMs = 0;
                _estimatedAt = now;
            }
            else if (_estimatedPlaying)
            {
                _estimatedPositionMs += Math.Max(0, (long)(now - _estimatedAt).TotalMilliseconds);
            }

            _estimatedPositionMs = Math.Clamp(_estimatedPositionMs, 0, durationMs);
            _estimatedAt = now;
            _estimatedPlaying = isPlaying;
            return _estimatedPositionMs;
        }
    }

    private static long SyncReportedPosition(string trackKey, bool isPlaying, long positionMs, long durationMs)
    {
        lock (PositionSync)
        {
            _estimatedTrackKey = trackKey;
            _estimatedPositionMs = durationMs > 0 ? Math.Clamp(positionMs, 0, durationMs) : Math.Max(0, positionMs);
            _estimatedAt = DateTimeOffset.UtcNow;
            _estimatedPlaying = isPlaying;
            return _estimatedPositionMs;
        }
    }

    private static async Task<MusicCover?> GetCoverAsync(string trackKey, IRandomAccessStreamReference? thumbnail)
    {
        await CoverGate.WaitAsync();
        try
        {
            if (trackKey == _coverKey)
            {
                lock (CoverSync) return _cover;
            }

            _coverKey = trackKey;
            lock (CoverSync) _cover = null;
            if (thumbnail is null) return null;

            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > MaxCoverBytes) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            var cover = new MusicCover(
                bytes,
                CoverContentType(stream.ContentType, bytes),
                Convert.ToHexString(SHA256.HashData(bytes))[..16]);
            lock (CoverSync) _cover = cover;
            return cover;
        }
        catch
        {
            return null;
        }
        finally
        {
            CoverGate.Release();
        }
    }

    private static async Task<LyricsPayload> GetLyricsAsync(string title, string artist, long durationMs)
    {
        var key = title + "" + artist + "" + durationMs + "" + _onlineLyricsEnabled;
        await LyricsGate.WaitAsync();
        try
        {
            if (key == _lyricKey) return _lyrics;

            _lyricKey = key;
            _lyrics = LoadLocalLyrics(title, artist, durationMs);
            if (_lyrics.Lines.Count == 0 && _onlineLyricsEnabled)
            {
                var online = await TryFetchOnlineLyricsAsync(title, artist, durationMs);
                if (online.Lines.Count > 0)
                {
                    _lyrics = online;
                    SaveLyricsCache(title, artist, online.DurationMs, online.Lines);
                }
                else
                {
                    _lyrics = EmptyLyrics("在线未找到同步歌词");
                }
            }

            return _lyrics;
        }
        finally
        {
            LyricsGate.Release();
        }
    }

    private static LyricsPayload LoadLocalLyrics(string title, string artist, long durationMs)
    {
        var cache = LyricsCachePath(title, artist, durationMs);
        if (File.Exists(cache))
        {
            var fromCache = ParseLrcFile(cache, "本地 LRC 歌词");
            if (fromCache.Lines.Count > 0) return fromCache;
        }

        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Lyrics"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Lyrics"),
            @"D:\Kugou\Lyric"
        };
        var lrcCandidates = new List<string>();
        var krcCandidates = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                lrcCandidates.AddRange(Directory.EnumerateFiles(root, "*.lrc", SearchOption.TopDirectoryOnly));
                krcCandidates.AddRange(Directory.EnumerateFiles(root, "*.krc", SearchOption.TopDirectoryOnly));
            }
            catch
            {
            }
        }

        var lrc = BestLyricCandidate(lrcCandidates, title, artist);
        if (lrc is not null)
        {
            var local = ParseLrcFile(lrc, "本地 LRC 歌词");
            if (local.Lines.Count > 0) return local;
        }

        var krc = BestLyricCandidate(krcCandidates, title, artist);
        if (krc is null) return EmptyLyrics();
        var converted = ParseKrcFile(krc);
        if (converted.Lines.Count > 0)
        {
            SaveConvertedKrc(krc, converted.DurationMs, converted.Lines);
        }

        return converted;
    }

    private static string? BestLyricCandidate(IEnumerable<string> candidates, string title, string artist)
    {
        var normalizedTitle = Normalize(title);
        var normalizedArtist = Normalize(artist);
        return candidates
            .Select(path => new
            {
                Path = path,
                Score = LyricCandidateScore(path, normalizedTitle, normalizedArtist),
                Updated = File.GetLastWriteTimeUtc(path)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Updated)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static int LyricCandidateScore(string path, string normalizedTitle, string normalizedArtist)
    {
        var stem = Regex.Replace(
            Path.GetFileNameWithoutExtension(path),
            @"-[0-9a-f]{32}-\d+-\d+$",
            "",
            RegexOptions.IgnoreCase);
        var normalizedStem = Normalize(stem);
        if (normalizedTitle.Length == 0 || !normalizedStem.Contains(normalizedTitle, StringComparison.Ordinal)) return 0;

        var hasArtist = normalizedArtist.Length > 0 && normalizedStem.Contains(normalizedArtist, StringComparison.Ordinal);
        var score = 10_000 - Math.Min(9000, Math.Abs(normalizedStem.Length - normalizedTitle.Length));
        if (hasArtist) score += 50_000;
        if (hasArtist && (normalizedStem == normalizedArtist + normalizedTitle || normalizedStem == normalizedTitle + normalizedArtist)) score += 100_000;
        if (normalizedStem.EndsWith(normalizedTitle, StringComparison.Ordinal)) score += 5000;
        if (normalizedTitle.Length <= 3 && !hasArtist) score -= 20_000;
        return score;
    }

    private static LyricsPayload ParseLrcFile(string path, string source)
    {
        try
        {
            return ParseLrcText(File.ReadAllText(path, Encoding.UTF8), source);
        }
        catch
        {
            return EmptyLyrics();
        }
    }

    private static LyricsPayload ParseKrcFile(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            if (raw.Length <= 4 || raw[0] != (byte)'k' || raw[1] != (byte)'r' || raw[2] != (byte)'c' || raw[3] != (byte)'1')
            {
                return EmptyLyrics("KRC 文件格式不正确");
            }

            var encrypted = raw[4..];
            for (var i = 0; i < encrypted.Length; i++) encrypted[i] ^= KrcKey[i % KrcKey.Length];
            using var input = new MemoryStream(encrypted);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ParseKrcText(reader.ReadToEnd());
        }
        catch
        {
            return EmptyLyrics("KRC 解码失败");
        }
    }

    private static LyricsPayload ParseKrcText(string content)
    {
        var lines = new List<LyricLine>();
        var offsetMs = TaggedOffset(content);
        var linePattern = new Regex(@"^\[(?<at>\d+),(?<duration>\d+)\](?<body>.*)$", RegexOptions.Compiled);
        var wordPattern = new Regex(@"<\d+,\d+,\d+>(?<text>[^<]*)", RegexOptions.Compiled);
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var match = linePattern.Match(raw.Trim());
            if (!match.Success) continue;
            var body = match.Groups["body"].Value;
            var text = string.Concat(wordPattern.Matches(body).Select(x => x.Groups["text"].Value)).Trim();
            if (string.IsNullOrWhiteSpace(text)) text = Regex.Replace(body, @"<[^>]+>", "").Trim();
            if (long.TryParse(match.Groups["at"].Value, out var atMs) && !string.IsNullOrWhiteSpace(text))
            {
                lines.Add(new LyricLine(Math.Max(0, atMs + offsetMs), text));
            }
        }

        var duration = TaggedDuration(content);
        if (duration <= 0 && lines.Count > 0) duration = lines[^1].AtMs + 5000;
        return lines.Count > 0
            ? new LyricsPayload(lines.OrderBy(x => x.AtMs).ToArray(), "本地 KRC（已转换为 LRC）", duration)
            : EmptyLyrics("KRC 中没有可显示的同步歌词");
    }

    private static LyricsPayload ParseLrcText(string content, string source)
    {
        var parsed = new List<LyricLine>();
        var offsetMs = TaggedOffset(content);
        var stamp = new Regex(@"\[(?<m>\d{1,3}):(?<s>\d{2})(?:[.:](?<f>\d{1,3}))?\]", RegexOptions.Compiled);
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var text = stamp.Replace(raw, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match match in stamp.Matches(raw))
            {
                var minutes = int.Parse(match.Groups["m"].Value);
                var seconds = int.Parse(match.Groups["s"].Value);
                var fraction = match.Groups["f"].Success ? match.Groups["f"].Value.PadRight(3, '0')[..3] : "0";
                parsed.Add(new LyricLine(Math.Max(0, (minutes * 60L + seconds) * 1000 + int.Parse(fraction) + offsetMs), text));
            }
        }

        var duration = TaggedDuration(content);
        if (duration <= 0 && parsed.Count > 0) duration = parsed.Max(x => x.AtMs) + 5000;
        return parsed.Count > 0
            ? new LyricsPayload(parsed.OrderBy(x => x.AtMs).ToArray(), source, duration)
            : EmptyLyrics();
    }

    private static async Task<LyricsPayload> TryFetchOnlineLyricsAsync(string title, string artist, long durationMs)
    {
        try
        {
            var query = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
            using var response = await LyricsClient.GetAsync(query);
            if (!response.IsSuccessStatusCode) return EmptyLyrics();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var best = document.RootElement.EnumerateArray()
                .Where(x => x.TryGetProperty("syncedLyrics", out var lyric) && lyric.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lyric.GetString()))
                .OrderBy(x => DurationDistance(x, durationMs))
                .FirstOrDefault();
            if (best.ValueKind == JsonValueKind.Undefined || !best.TryGetProperty("syncedLyrics", out var synced)) return EmptyLyrics();
            var payload = ParseLrcText(synced.GetString() ?? "", "在线同步歌词");
            var onlineDuration = JsonDuration(best);
            return payload with { DurationMs = onlineDuration > 0 ? onlineDuration : payload.DurationMs };
        }
        catch
        {
            return EmptyLyrics();
        }
    }

    private static long DurationDistance(JsonElement item, long durationMs)
    {
        var candidate = JsonDuration(item);
        return candidate > 0 ? Math.Abs(candidate - durationMs) : long.MaxValue / 2;
    }

    private static long JsonDuration(JsonElement item) =>
        item.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds)
            ? Math.Max(0, (long)(seconds * 1000))
            : 0;

    private static long TaggedDuration(string content)
    {
        var total = Regex.Match(content, @"\[total:(?<ms>\d+)\]", RegexOptions.IgnoreCase);
        if (total.Success && long.TryParse(total.Groups["ms"].Value, out var totalMs)) return totalMs;
        var length = Regex.Match(content, @"\[length:(?<m>\d{1,3}):(?<s>\d{2})(?:[.:](?<f>\d{1,3}))?\]", RegexOptions.IgnoreCase);
        if (!length.Success) return 0;
        var fraction = length.Groups["f"].Success ? length.Groups["f"].Value.PadRight(3, '0')[..3] : "0";
        return (long.Parse(length.Groups["m"].Value) * 60L + long.Parse(length.Groups["s"].Value)) * 1000 + long.Parse(fraction);
    }

    private static long TaggedOffset(string content)
    {
        var offset = Regex.Match(content, @"\[offset:(?<ms>[+-]?\d+)\]", RegexOptions.IgnoreCase);
        return offset.Success && long.TryParse(offset.Groups["ms"].Value, out var offsetMs) ? offsetMs : 0;
    }

    private static void SaveConvertedKrc(string sourcePath, long durationMs, IReadOnlyList<LyricLine> lines)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath + File.GetLastWriteTimeUtc(sourcePath).Ticks)))[..24];
        SaveLrc(Path.Combine(AppContext.BaseDirectory, "Lyrics", "Cache", name + ".lrc"), durationMs, lines);
    }

    private static void SaveLyricsCache(string title, string artist, long durationMs, IReadOnlyList<LyricLine> lines) =>
        SaveLrc(LyricsCachePath(title, artist, durationMs), durationMs, lines);

    private static void SaveLrc(string path, long durationMs, IReadOnlyList<LyricLine> lines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var head = durationMs > 0 ? $"[total:{durationMs}]" + Environment.NewLine : "";
            var data = head + string.Join(Environment.NewLine, lines.Select(x => $"[{x.AtMs / 60000:00}:{x.AtMs / 1000 % 60:00}.{x.AtMs % 1000:000}]{x.Text}"));
            File.WriteAllText(path, data, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string LyricsCachePath(string title, string artist, long durationMs) =>
        Path.Combine(AppContext.BaseDirectory, "Lyrics", "Cache", "v2", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title + "" + artist + "" + durationMs)))[..24] + ".lrc");

    private static (string Previous, string Current, string Next, long CurrentEndMs) SelectLyrics(IReadOnlyList<LyricLine> lyrics, long positionMs, long durationMs, string source)
    {
        if (lyrics.Count == 0)
        {
            return ("", "暂未找到同步歌词", _onlineLyricsEnabled ? source : "可放入 .lrc 或 .krc，或启用在线匹配", 0);
        }

        var index = 0;
        for (var i = 0; i < lyrics.Count; i++)
        {
            if (lyrics[i].AtMs > positionMs) break;
            index = i;
        }

        return (
            index > 0 ? lyrics[index - 1].Text : "",
            lyrics[index].Text,
            index + 1 < lyrics.Count ? lyrics[index + 1].Text : "",
            index + 1 < lyrics.Count ? lyrics[index + 1].AtMs : Math.Max(durationMs, lyrics[index].AtMs + 3000));
    }

    private static LyricsPayload EmptyLyrics(string source = "未找到同步歌词") => new(Array.Empty<LyricLine>(), source, 0);

    private static MusicSnapshot Empty(string status) => new(
        false, "酷狗音乐", "等待酷狗播放", "请在电脑上打开酷狗并播放歌曲", "", false, 0, 0,
        false, "", "", "正在等待媒体会话", "", 0, "", "#8B5CF6", status, DateTimeOffset.Now);

    private static HttpClient CreateLyricsClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BF66CodexDisplay", "2.4"));
        return client;
    }

    private static string CoverContentType(string? declared, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(declared) && declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return declared;
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length > 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
        return "image/jpeg";
    }

    private static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string ShortMessage(string message) => message.Length > 46 ? message[..46] + "…" : message;

    private static string AccentFor(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var colors = new[] { "#8B5CF6", "#EC4899", "#06B6D4", "#F97316", "#14B8A6", "#6366F1" };
        return colors[bytes[0] % colors.Length];
    }
}
