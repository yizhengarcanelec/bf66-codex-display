using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BF66Host;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var startupImage = args.FirstOrDefault(File.Exists);
        Application.Run(new MainForm(args.Any(x => x.Equals("--landscape", StringComparison.OrdinalIgnoreCase)), startupImage));
    }
}

internal sealed record DisplayState(
    string Mode,
    string Orientation,
    string Title,
    string Message,
    string Background,
    string Foreground,
    int FontSize,
    bool ShowClock,
    string ImageFit,
    bool HasImage,
    UsageSnapshot? Usage,
    long Version);

internal sealed class StateStore
{
    private readonly object _gate = new();
    private DisplayState _state = new("custom", "portrait", "BF66 显示屏", "已连接电脑控制端", "#081525", "#F4F8FF", 42, true, "contain", false, null, 1);
    private string? _imagePath;

    public DisplayState Get()
    {
        lock (_gate) return _state;
    }

    public string? GetImagePath()
    {
        lock (_gate) return _imagePath;
    }

    public void Update(string mode, string orientation, string title, string message, Color background, Color foreground, int fontSize, bool showClock, string imageFit, string? imagePath)
    {
        lock (_gate)
        {
            _imagePath = imagePath;
            var hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            _state = new DisplayState(
                mode,
                orientation,
                title,
                message,
                ColorTranslator.ToHtml(background),
                ColorTranslator.ToHtml(foreground),
                fontSize,
                showClock,
                imageFit,
                hasImage,
                _state.Usage,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public void UpdateUsage(UsageSnapshot usage)
    {
        lock (_gate)
            _state = _state with { Usage = usage };
    }
}

internal static class ConnectionToken
{
    public static string LoadOrCreate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "bf66-connection.key");
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (saved.Length == 48 && saved.All(Uri.IsHexDigit)) return saved.ToUpperInvariant();
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            File.WriteAllText(path, token);
            return token;
        }
        catch
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        }
    }
}

internal sealed class ConnectionTracker
{
    private readonly object _gate = new();
    private DateTimeOffset _lastWirelessSeen = DateTimeOffset.MinValue;
    private string _lastWirelessAddress = "";

    public void Mark(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || IPAddress.IsLoopback(address)) return;
        lock (_gate)
        {
            _lastWirelessSeen = DateTimeOffset.UtcNow;
            _lastWirelessAddress = address.ToString();
        }
    }

    public bool TryGetWireless(out string address)
    {
        lock (_gate)
        {
            address = _lastWirelessAddress;
            return DateTimeOffset.UtcNow - _lastWirelessSeen < TimeSpan.FromSeconds(8);
        }
    }
}

internal sealed class DisplayServer : IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly string _token;
    private readonly ConnectionTracker _tracker;
    private WebApplication? _app;

    public DisplayServer(StateStore store, string token, ConnectionTracker tracker)
    {
        _store = store;
        _token = token;
        _tracker = tracker;
    }

    public async Task StartAsync()
    {
        var options = new WebApplicationOptions { Args = Array.Empty<string>(), ApplicationName = typeof(DisplayServer).Assembly.FullName };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseUrls("http://0.0.0.0:8787");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/health", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            _tracker.Mark(context);
            return Results.Text("ok");
        });
        _app.MapGet("/api/state", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            _tracker.Mark(context);
            return Results.Json(_store.Get());
        });
        _app.MapGet("/media", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            _tracker.Mark(context);
            var path = _store.GetImagePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Results.NotFound();
            return Results.File(path, ContentType(path), enableRangeProcessing: false);
        });
        _app.MapGet("/pet-media/{name}", (HttpContext context, string name) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            _tracker.Mark(context);
            var allowed = name switch
            {
                "idle" => "idle.gif",
                "angry" => "angry.gif",
                "comfort" => "comfort.gif",
                "enjoy" => "enjoy.gif",
                "cry" => "cry.gif",
                "work" => "work.gif",
                "sleep" => "sleep.gif",
                _ => null
            };
            if (allowed is null) return Results.NotFound();
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Miao", allowed);
            return File.Exists(path)
                ? Results.File(path, "image/gif", enableRangeProcessing: false)
                : Results.NotFound();
        });
        _app.MapGet("/display", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            _tracker.Mark(context);
            return Results.Content(DisplayHtml, "text/html; charset=utf-8", Encoding.UTF8);
        });
        await _app.StartAsync();
    }

    private bool Authorized(HttpContext context)
    {
        var provided = context.Request.Query["token"].ToString();
        if (provided.Length != _token.Length) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(provided), Convert.FromHexString(_token));
        }
        catch { return false; }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private const string DisplayHtml = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
<style>
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}html,body{width:100%;height:100%;margin:0;overflow:hidden;background:#081525;font-family:system-ui,-apple-system,"Microsoft YaHei",sans-serif}
#root{position:relative;width:100%;height:100%;transition:background .25s,color .25s}
#custom{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;padding:max(18px,5vw)}
#image-media{position:absolute;inset:0;width:100%;height:100%;display:none}#shade{position:absolute;inset:0;background:linear-gradient(180deg,rgba(0,0,0,.10),rgba(0,0,0,.30));display:none}
#clock,#title,#message{position:relative;z-index:2;text-align:center;text-shadow:0 2px 12px rgba(0,0,0,.45)}
#clock{font-size:min(18vw,92px);font-weight:750;letter-spacing:.02em;line-height:1;margin-bottom:3vh;font-variant-numeric:tabular-nums}
#title{font-size:min(8vw,46px);font-weight:700;line-height:1.2;margin-bottom:2vh;white-space:pre-wrap}
#message{font-size:42px;font-weight:450;line-height:1.35;white-space:pre-wrap;max-width:96%;overflow-wrap:anywhere}
#usage{position:absolute;inset:0;display:none;flex-direction:column;padding:24px 22px 17px;background:linear-gradient(155deg,#10141c 0%,#0c1119 58%,#111820 100%);color:#f6f8fc}
.meter-head{display:flex;align-items:center;height:42px}.brand-dot{color:#4ade80;font-size:20px;margin-right:9px;line-height:1}.brand{font-size:20px;font-weight:800;letter-spacing:1.1px;white-space:nowrap}.live{margin-left:auto;border:1px solid #344052;border-radius:30px;padding:5px 10px;color:#98a6ba;font-size:11px;letter-spacing:.7px;white-space:nowrap}
.eyebrow{color:#8492a6;font-size:12px;font-weight:800;letter-spacing:1.8px}.hero{padding:34px 0 27px}.hero-value{font-size:68px;font-weight:800;line-height:1.03;letter-spacing:-2px;margin-top:8px;font-variant-numeric:tabular-nums}.partial{color:#6f7e92;font-size:11px;margin-top:7px;min-height:14px}
.breakdown{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:7px;margin-bottom:17px}.metric{min-width:0;overflow:hidden;background:#171d27;border:1px solid #2b3442;border-radius:12px;padding:13px 9px}.metric-label{color:#7f8ca0;font-size:10px;font-weight:800;letter-spacing:.8px}.metric-value{font-size:18px;font-weight:750;margin-top:6px;font-variant-numeric:tabular-nums;white-space:nowrap}
.weekly{background:#171d27;border:1px solid #2b3442;border-radius:16px;padding:18px 16px 16px}.weekly-top{display:flex;align-items:flex-end}.weekly-value{margin-left:auto;color:#4ade80;font-size:35px;font-weight:800;line-height:1;font-variant-numeric:tabular-nums}.track{height:8px;border-radius:20px;background:#29313e;margin:15px 0 13px;overflow:hidden}.fill{height:100%;width:0;border-radius:20px;background:#4ade80;transition:width .45s,background .25s}.weekly-meta{display:flex;color:#aab5c5;font-size:11px;white-space:nowrap}.reset{margin-left:auto;color:#718096}.usage-footer{margin-top:auto;display:flex;align-items:center;color:#657386;font-size:10px}.local{margin-left:auto;font-weight:800;letter-spacing:1px}
#usage-empty{display:none;text-align:center;color:#8b98aa;font-size:22px;margin:auto}
#pet{--lamp-color:#58dca2;--lamp-glow:#3ad796;position:absolute;inset:0;display:none;overflow:hidden;touch-action:none;user-select:none;background:radial-gradient(circle at 50% 42%,#fff7e5 0,#e7dfce 44%,#9ba8c6 100%);color:#53382f}
.pet-grid{position:absolute;inset:0;opacity:.32;background-image:radial-gradient(circle at 15% 18%,rgba(93,63,52,.16) 0 3px,transparent 4px),radial-gradient(circle at 78% 72%,rgba(93,63,52,.12) 0 4px,transparent 5px);background-size:66px 66px,92px 92px}
.pet-halo{position:absolute;left:50%;top:60%;width:min(82vw,520px);height:min(82vw,520px);transform:translate(-50%,-50%);border-radius:50%;background:radial-gradient(circle,rgba(255,255,255,.88),rgba(255,235,198,.42) 48%,transparent 71%);filter:blur(5px);animation:miaoHalo 4.2s ease-in-out infinite}
.pet-name{position:absolute;z-index:9;left:14px;bottom:12px;padding:6px 11px;border:1px solid rgba(91,62,51,.2);border-radius:99px;background:rgba(255,250,239,.56);color:#705247;font-size:11px;font-weight:800;letter-spacing:.8px}
#pet-bubble-zone{position:absolute;z-index:20;left:7%;right:7%;top:14%;height:18%;display:flex;align-items:flex-start;justify-content:center;pointer-events:none}
#pet-bubble{max-width:100%;max-height:100%;overflow:hidden;padding:12px 19px;border:2px solid rgba(100,70,58,.2);border-radius:20px;background:rgba(255,252,244,.94);backdrop-filter:blur(8px);color:#573b32;font-size:20px;font-weight:700;line-height:1.35;text-align:center;opacity:0;transform:translateY(-8px) scale(.98);transition:opacity .2s,transform .2s;box-shadow:0 12px 28px rgba(72,51,45,.18)}#pet-bubble.show{opacity:1;transform:translateY(0) scale(1)}
#pet-anchor{--drag-x:0px;--drag-y:0px;--tilt:0deg;position:absolute;z-index:6;left:50%;top:60%;width:min(70vw,470px);aspect-ratio:1;transform:translate(calc(-50% + var(--drag-x)),calc(-50% + var(--drag-y))) rotate(var(--tilt));cursor:pointer;outline:none;-webkit-touch-callout:none;will-change:transform}
#pet-anchor.returning{transition:transform .68s cubic-bezier(.18,1.55,.32,1)}#pet-anchor:focus,#pet-anchor:focus-visible,#pet-anchor:active{outline:none;background:transparent;box-shadow:none}
#miao{display:block;width:100%;height:100%;object-fit:contain;filter:drop-shadow(0 20px 17px rgba(67,43,35,.26));pointer-events:none;transform-origin:50% 78%}#buddy{display:none!important}
#pet-anchor.working #miao{filter:drop-shadow(0 20px 17px rgba(67,43,35,.26)) drop-shadow(0 0 12px rgba(62,141,255,.34))}
#pet-anchor.dragging{transition:none}#pet-anchor.dragging #miao{transform:scale(.94);filter:drop-shadow(0 25px 13px rgba(67,43,35,.3))}#pet-anchor.drag-right #miao{transform:scale(.94) rotate(7deg)}#pet-anchor.drag-left #miao{transform:scale(.94) rotate(-7deg)}
#pet-anchor.joy #miao{animation:miaoJump 1.05s cubic-bezier(.2,.8,.25,1)}#pet-anchor.land #miao{animation:miaoLand .62s ease}
#pet-clock{position:absolute;z-index:10;left:50%;top:4.5%;transform:translateX(-50%);color:#67483d;font-size:clamp(38px,8vw,68px);font-weight:800;line-height:1;font-variant-numeric:tabular-nums;letter-spacing:2px;text-shadow:0 3px 16px rgba(255,255,255,.85);white-space:nowrap;pointer-events:none}
#pet-hint{position:absolute;z-index:8;left:0;right:0;bottom:8.2%;text-align:center;color:rgba(81,58,50,.65);font-size:13px;font-weight:650;letter-spacing:.25px}.pet-status{position:absolute;z-index:9;right:14px;bottom:12px;padding:6px 10px;border:1px solid rgba(91,62,51,.2);border-radius:99px;background:rgba(255,250,239,.5);color:#705247;font-size:10px;font-weight:800;letter-spacing:.7px}
.spark{position:absolute;z-index:12;left:50%;top:58%;font-size:25px;pointer-events:none;animation:spark .9s ease-out forwards}
@keyframes miaoHalo{0%,100%{opacity:.62;transform:translate(-50%,-50%) scale(.94)}50%{opacity:1;transform:translate(-50%,-50%) scale(1.06)}}@keyframes miaoJump{0%,100%{transform:translateY(0) scale(1)}30%{transform:translateY(10px) scale(1.04,.93)}56%{transform:translateY(-48px) scale(.97,1.05)}80%{transform:translateY(5px) scale(1.04,.94)}}@keyframes miaoLand{0%,100%{transform:translateY(0) scale(1)}35%{transform:translateY(9px) scale(1.07,.9)}66%{transform:translateY(-4px) scale(.98,1.03)}}@keyframes usagePulse{0%,100%{opacity:1;transform:scale(1);box-shadow:0 0 0 2px rgba(95,66,55,.24),0 0 17px var(--lamp-glow)}50%{opacity:.72;transform:scale(1.55);box-shadow:0 0 0 8px rgba(255,255,255,.3),0 0 29px var(--lamp-glow)}}@keyframes spark{0%{opacity:1;transform:translate(-50%,-20%) scale(.6)}100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(1.35)}}
#offline{position:fixed;left:50%;bottom:18px;transform:translateX(-50%);z-index:5;padding:8px 14px;border-radius:99px;background:rgba(0,0,0,.65);color:#fff;font-size:14px;display:none;white-space:nowrap}
@media (orientation:landscape){
#usage{padding:12px 20px 10px}.meter-head{height:30px}.brand-dot{font-size:16px;margin-right:7px}.brand{font-size:16px}.live{font-size:9px;padding:3px 8px}
#usage-content{display:grid!important;grid-template-columns:.82fr 1.45fr;grid-template-rows:84px 142px;gap:10px 15px;margin-top:8px}.hero{grid-row:1/3;padding:24px 0 0;align-self:center}.hero-value{font-size:58px;margin-top:7px}.eyebrow{font-size:10px}.partial{font-size:9px}
.breakdown{margin:0;gap:6px}.metric{border-radius:10px;padding:10px 8px}.metric-label{font-size:8px}.metric-value{font-size:16px;margin-top:4px}
.weekly{border-radius:13px;padding:13px 14px 11px}.weekly-value{font-size:29px}.track{height:7px;margin:10px 0 9px}.weekly-meta{font-size:9px}.usage-footer{font-size:8px}
#pet-bubble-zone{left:4%;right:auto;top:17%;width:38%;height:55%;align-items:center}#pet-bubble{font-size:16px;padding:10px 15px}#pet-anchor{left:69%;top:52%;width:min(37vw,390px)}.pet-halo{left:69%;top:52%;width:min(48vw,430px);height:min(48vw,430px)}#pet-hint{left:47%;bottom:2.5%;font-size:11px}.pet-status{top:9px;bottom:auto}.pet-name{top:9px;bottom:auto}#pet-clock{top:13px;font-size:34px;letter-spacing:1.5px}
}
</style></head><body><main id="root">
<section id="custom"><img id="image-media"><div id="shade"></div><div id="clock"></div><div id="title"></div><div id="message"></div></section>
<section id="usage"><header class="meter-head"><span class="brand-dot">●</span><span class="brand">CODEX METER</span><span class="live" id="transport">CONNECTING</span></header><div id="usage-content"><div class="hero"><div class="eyebrow">TOKENS TODAY</div><div class="hero-value" id="u-total">--</div><div class="partial" id="u-partial"></div></div><div class="breakdown"><div class="metric"><div class="metric-label">INPUT</div><div class="metric-value" id="u-input">--</div></div><div class="metric"><div class="metric-label">OUTPUT</div><div class="metric-value" id="u-output">--</div></div><div class="metric"><div class="metric-label">CACHE</div><div class="metric-value" id="u-cache">--</div></div></div><div class="weekly"><div class="weekly-top"><div class="eyebrow">WEEKLY LEFT</div><div class="weekly-value" id="u-left">--%</div></div><div class="track"><div class="fill" id="u-fill"></div></div><div class="weekly-meta"><span id="u-used">等待本地数据</span><span class="reset" id="u-reset"></span></div></div></div><div id="usage-empty">正在读取 GPT Usage 数据…</div><footer class="usage-footer"><span id="u-updated">--</span><span class="local">LOCAL ONLY</span></footer></section>
<section id="pet"><div class="pet-grid"></div><div class="pet-halo"></div><div id="pet-clock"></div><div id="pet-bubble-zone"><div id="pet-bubble"></div></div><div id="pet-anchor" aria-label="月薪喵工作伙伴"><img id="miao" alt="月薪喵" draggable="false">
<svg id="buddy" viewBox="0 0 320 430" xmlns="http://www.w3.org/2000/svg">
<defs>
<linearGradient id="shell" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#b5c4ff"/><stop offset=".36" stop-color="#778cff"/><stop offset="1" stop-color="#4c42c9"/></linearGradient>
<linearGradient id="bodyGrad" x1=".15" y1="0" x2=".8" y2="1"><stop stop-color="#8398ff"/><stop offset=".55" stop-color="#5967df"/><stop offset="1" stop-color="#39349d"/></linearGradient>
<linearGradient id="limbGrad" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#91a5ff"/><stop offset="1" stop-color="#4542b3"/></linearGradient>
<linearGradient id="faceGrad" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#172951"/><stop offset="1" stop-color="#050a1c"/></linearGradient>
<filter id="shellGlow" x="-30%" y="-30%" width="160%" height="170%"><feGaussianBlur stdDeviation="5" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
<filter id="eyeGlow" x="-80%" y="-80%" width="260%" height="260%"><feGaussianBlur stdDeviation="3" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
</defs>
<ellipse cx="160" cy="406" rx="92" ry="14" fill="#020613" opacity=".52"/>
<g id="buddy-root">
<g id="leg-left" class="joint"><rect x="91" y="326" width="45" height="58" rx="21" fill="url(#limbGrad)" stroke="#292778" stroke-width="5"/><path d="M79 379 Q113 362 145 382 L145 401 Q112 411 79 400Z" fill="url(#shell)" stroke="#292778" stroke-width="5"/><path d="M91 391h42" stroke="#c3ceff" stroke-width="4" opacity=".45"/></g>
<g id="leg-right" class="joint"><rect x="184" y="326" width="45" height="58" rx="21" fill="url(#limbGrad)" stroke="#292778" stroke-width="5"/><path d="M175 382 Q207 362 241 379 L241 400 Q208 411 175 401Z" fill="url(#shell)" stroke="#292778" stroke-width="5"/><path d="M187 391h42" stroke="#c3ceff" stroke-width="4" opacity=".45"/></g>
<g id="arm-left" class="joint"><rect x="39" y="246" width="44" height="94" rx="22" fill="url(#limbGrad)" stroke="#292778" stroke-width="5" transform="rotate(9 61 246)"/><circle cx="49" cy="333" r="25" fill="url(#shell)" stroke="#292778" stroke-width="5"/></g>
<g id="arm-right" class="joint"><rect x="237" y="246" width="44" height="94" rx="22" fill="url(#limbGrad)" stroke="#292778" stroke-width="5" transform="rotate(-9 259 246)"/><circle cx="271" cy="333" r="25" fill="url(#shell)" stroke="#292778" stroke-width="5"/></g>
<g id="body"><ellipse cx="160" cy="292" rx="82" ry="88" fill="url(#bodyGrad)" stroke="#292778" stroke-width="6"/><ellipse cx="140" cy="258" rx="45" ry="31" fill="#d8e0ff" opacity=".12"/><rect x="112" y="278" width="96" height="58" rx="17" fill="#24275f" stroke="#a99cff" stroke-width="5"/><rect id="chest-light" x="121" y="287" width="78" height="40" rx="12"/><rect x="128" y="294" width="64" height="26" rx="8" fill="#172151" opacity=".82"/><circle class="chest-bar work-dot" cx="146" cy="307" r="4"/><circle class="chest-bar work-dot" cx="160" cy="307" r="4"/><circle class="chest-bar work-dot" cx="174" cy="307" r="4"/></g>
<g id="buddy-head" class="joint" filter="url(#shellGlow)"><path d="M57 205C25 198 24 158 44 139C29 104 55 71 88 76C98 42 135 34 158 58C181 30 222 42 230 76C265 70 291 102 277 136C301 157 289 198 259 204C224 221 91 221 57 205Z" fill="url(#shell)" stroke="#332a9d" stroke-width="7"/><path d="M79 91C108 54 203 49 237 91" fill="none" stroke="#e8ecff" stroke-width="8" opacity=".3" stroke-linecap="round"/><rect x="58" y="105" width="204" height="124" rx="49" fill="url(#faceGrad)" stroke="#26246e" stroke-width="7"/><rect x="69" y="116" width="182" height="101" rx="40" fill="none" stroke="#6278ff" stroke-width="3" opacity=".26"/>
<g id="eyes-normal"><path class="buddy-eye" d="M111 152l22 16-22 16"/><path class="buddy-eye" d="M209 152l-22 16 22 16"/></g>
<g id="eyes-happy"><path class="buddy-eye" d="M106 177q15-25 30 0"/><path class="buddy-eye" d="M184 177q15-25 30 0"/></g>
<g id="eyes-sleep"><path class="buddy-eye" d="M105 174q16 9 31 0"/><path class="buddy-eye" d="M184 174q16 9 31 0"/></g>
<g id="eyes-surprise"><circle cx="121" cy="169" r="13" class="buddy-eye"/><circle cx="199" cy="169" r="13" class="buddy-eye"/></g></g>
</g></svg></div><div class="pet-name">月薪喵 · 工作伙伴</div><div id="pet-hint">轻触互动 · 双击开心 · 拖动回位 · 长按睡觉</div><div class="pet-status" id="pet-transport">COMPANION</div></section>
</main><div id="offline">正在重新连接电脑…</div>
<script>
const $=id=>document.getElementById(id);let ver=-1,lastOk=Date.now(),lastOrientation='';const token=new URLSearchParams(location.search).get('token')||'';const auth='token='+encodeURIComponent(token);$('transport').textContent=location.hostname==='127.0.0.1'?'USB LIVE':'WI-FI';
$('pet-transport').textContent=location.hostname==='127.0.0.1'?'USB':'WI-FI';
function tick(){const d=new Date(),time=d.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false});$('clock').textContent=time;$('pet-clock').textContent=time;}tick();setInterval(tick,1000);
function compact(n){n=Number(n||0);if(n>=1e9)return(n/1e9).toFixed(2)+'B';if(n>=1e6)return(n/1e6).toFixed(2)+'M';if(n>=1e3)return(n/1e3).toFixed(1)+'K';return n.toLocaleString('en-US')}
function renderUsage(u){$('usage-content').style.display=u?'block':'none';$('usage-empty').style.display=u?'none':'block';if(!u)return;$('u-total').textContent=compact(u.total);$('u-input').textContent=compact(u.input);$('u-output').textContent=compact(u.output);$('u-cache').textContent=compact(u.cached);$('u-partial').textContent=u.partial?'今天早期记录不完整':'';const left=u.hasWeekly?Number(u.remainingPercent):0;const accent=left>=50?'#4ade80':left>=20?'#fbbf24':'#fb7185';$('u-left').textContent=u.hasWeekly?left.toFixed(left%1?1:0)+'%':'--%';$('u-left').style.color=accent;$('u-fill').style.width=(u.hasWeekly?left:0)+'%';$('u-fill').style.background=accent;$('u-used').textContent=u.hasWeekly?Number(u.usedPercent).toFixed(u.usedPercent%1?1:0)+'% USED  |  '+String(u.plan||'CODEX').toUpperCase():'暂无周额度样本';$('u-reset').textContent=u.resetAt?'RESET '+new Date(u.resetAt).toLocaleString('zh-CN',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false}):'';$('u-updated').textContent='UPDATED '+new Date(u.generatedAt).toLocaleTimeString('zh-CN',{hour12:false})+'  |  EVERY 5S'}
function renderCustom(s){$('root').style.background=s.background;$('root').style.color=s.foreground;$('clock').style.display=s.showClock?'block':'none';$('title').textContent=s.title;$('title').style.display=s.title?'block':'none';$('message').textContent=s.message;$('message').style.fontSize=s.fontSize+'px';if(s.version===ver)return;const img=$('image-media');img.style.display='none';if(s.hasImage){img.src='/media?'+auth+'&v='+s.version;img.style.objectFit=s.imageFit;img.style.display='block';$('shade').style.display='block'}else{$('shade').style.display='none'}}
const pet=$('pet'),petAnchor=$('pet-anchor'),petBubble=$('pet-bubble'),miao=$('miao');let bubbleTimer=0,poseTimer=0,pressTimer=0,lastTap=0,longPressed=false,dragging=false,startX=0,startY=0,downAt=0,dragX=0,dragY=0,manualUntil=0,partnerState='',lastActivity='',lastWorkBubble=0,lastUsageTotal=null,miaoMode='';
function petVisible(){return pet.style.display==='flex'}
function petSay(text,duration=1800){if(!petVisible())return;clearTimeout(bubbleTimer);petBubble.textContent=text;petBubble.classList.add('show');bubbleTimer=setTimeout(()=>petBubble.classList.remove('show'),duration)}
function setMiao(name,restart=false){if(!restart&&miaoMode===name)return;miaoMode=name;miao.src='/pet-media/'+name+'?'+auth+'&v='+(restart?Date.now():name)}
function removeTransient(){petAnchor.classList.remove('joy','land')}
function applyPartnerState(){if(Date.now()<manualUntil)return;petAnchor.classList.toggle('working',partnerState==='working');setMiao(partnerState==='working'?'work':partnerState==='rest'?'sleep':'idle')}
function sparks(){for(let i=0;i<9;i++){const p=document.createElement('span');p.className='spark';p.textContent=i%2?'✦':'●';p.style.color=i%2?'#f3b64c':'#5078bd';p.style.setProperty('--dx',(Math.random()*220-110)+'px');p.style.setProperty('--dy',(-55-Math.random()*150)+'px');petAnchor.appendChild(p);setTimeout(()=>p.remove(),950)}}
function petReact(gif,text,duration=2200,jump=false){clearTimeout(poseTimer);removeTransient();petAnchor.classList.remove('working');manualUntil=Date.now()+duration;setMiao(gif,true);if(jump){petAnchor.classList.add('joy');sparks()}if(text)petSay(text,Math.min(duration+300,2500));poseTimer=setTimeout(()=>{removeTransient();manualUntil=0;applyPartnerState()},duration)}
function updateLamp(u){if(!u)return;const left=u.hasWeekly?Number(u.remainingPercent):100;const color=left>=50?'#58dca2':left>=20?'#f3bd55':'#f07178';const glow=left>=50?'#3ad796':left>=20?'#e8a836':'#e95863';pet.style.setProperty('--lamp-color',color);pet.style.setProperty('--lamp-glow',glow);const total=Number(u.total||0);if(lastUsageTotal!==null&&total!==lastUsageTotal){pet.classList.remove('usage-pulse');void pet.offsetWidth;pet.classList.add('usage-pulse');setTimeout(()=>pet.classList.remove('usage-pulse'),900)}lastUsageTotal=total}
function updatePartner(u){if(!u)return;updateLamp(u);const activity=u.lastActivityAt?Date.parse(u.lastActivityAt):0;const now=Date.now(),age=activity?now-activity:1e12;const next=age>10*60*1000?'rest':age<90*1000?'working':'standby';const changedActivity=activity&&lastActivity&&activity>Number(lastActivity);if(changedActivity&&now>=manualUntil&&now-lastWorkBubble>45000){lastWorkBubble=now;petReact('work','月薪喵收到新任务，开工啦！',2400)}if(next!==partnerState){const before=partnerState;partnerState=next;if(now>=manualUntil){applyPartnerState();if(before&&next==='rest')petSay('没有新会话，月薪喵先睡一会儿。',2100);else if(before==='rest'&&next==='working')petSay('新任务到了，起床开工！',1800)}}lastActivity=String(activity||'')}
function manualRest(){petReact('sleep','月薪喵先眯一会儿。',4300)}
function returnHome(){dragging=false;petAnchor.classList.remove('dragging','drag-left','drag-right');petAnchor.classList.add('returning');petAnchor.style.setProperty('--drag-x','0px');petAnchor.style.setProperty('--drag-y','0px');petAnchor.style.setProperty('--tilt','0deg');setTimeout(()=>{petAnchor.classList.remove('returning');petAnchor.classList.add('land');setTimeout(()=>{petAnchor.classList.remove('land');manualUntil=0;applyPartnerState()},620)},680)}
setMiao('idle',true);
petAnchor.addEventListener('pointerdown',e=>{e.preventDefault();startX=e.clientX;startY=e.clientY;downAt=Date.now();dragX=dragY=0;dragging=false;longPressed=false;petAnchor.setPointerCapture(e.pointerId);pressTimer=setTimeout(()=>{if(!dragging){longPressed=true;manualRest()}},720)});
petAnchor.addEventListener('pointermove',e=>{if(!petAnchor.hasPointerCapture(e.pointerId))return;const dx=e.clientX-startX,dy=e.clientY-startY;if(!dragging&&Math.hypot(dx,dy)>9){dragging=true;clearTimeout(pressTimer);clearTimeout(poseTimer);manualUntil=Date.now()+5000;petAnchor.classList.remove('returning','working','joy','land');petAnchor.classList.add('dragging');setMiao('angry',true)}if(!dragging)return;dragX=Math.max(-82,Math.min(82,dx));dragY=Math.max(-52,Math.min(52,dy));petAnchor.style.setProperty('--drag-x',dragX+'px');petAnchor.style.setProperty('--drag-y',dragY+'px');petAnchor.style.setProperty('--tilt',Math.max(-7,Math.min(7,dx/12))+'deg');petAnchor.classList.toggle('drag-right',dx>3);petAnchor.classList.toggle('drag-left',dx<-3)});
petAnchor.addEventListener('pointerup',e=>{e.preventDefault();clearTimeout(pressTimer);if(dragging){const quick=Date.now()-downAt<540&&Math.abs(dragX)>58;returnHome();if(quick)petSay('慢一点，我还要上班呢！',1500);return}if(longPressed)return;const now=Date.now();if(now-lastTap<420){lastTap=0;petReact('enjoy','今天也要开心打工！',2400,true)}else{lastTap=now;const rect=petAnchor.getBoundingClientRect(),head=(startY-rect.top)/rect.height<.6;setTimeout(()=>{if(lastTap===now){petReact(head?'comfort':'angry',head?'摸摸收到，继续努力。':'戳到我啦！',head?2800:1900);lastTap=0}},430)}});
petAnchor.addEventListener('pointercancel',()=>{clearTimeout(pressTimer);if(dragging)returnHome()});
async function sync(){try{const r=await fetch('/api/state?'+auth+'&x='+Date.now(),{cache:'no-store'});if(!r.ok)throw 0;const s=await r.json();lastOk=Date.now();$('offline').style.display='none';if(s.orientation!==lastOrientation){lastOrientation=s.orientation;if(window.BF66&&BF66.setOrientation)BF66.setOrientation(s.orientation)}const usageMode=s.mode==='usage',petMode=s.mode==='pet';$('usage').style.display=usageMode?'flex':'none';pet.style.display=petMode?'flex':'none';$('custom').style.display=!usageMode&&!petMode?'flex':'none';updatePartner(s.usage);if(usageMode)renderUsage(s.usage);else if(!petMode)renderCustom(s);ver=s.version}catch(e){if(Date.now()-lastOk>1500)$('offline').style.display='block'}finally{setTimeout(sync,350)}}sync();
</script></body></html>
""";
}

internal sealed class AdbManager
{
    private readonly string _adb;
    private readonly string _apk;
    private readonly string _token;

    public AdbManager(string token)
    {
        var root = AppContext.BaseDirectory;
        _adb = Path.Combine(root, "tools", "platform-tools", "adb.exe");
        _apk = Path.Combine(root, "BF66Display.apk");
        _token = token;
    }

    public bool IsAvailable => File.Exists(_adb);
    public bool ApkAvailable => File.Exists(_apk);

    public async Task<string> DeviceStatusAsync()
    {
        if (!IsAvailable) return "缺少 USB 连接工具";
        var result = await RunAsync("devices");
        if (result.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) return "等待 MP4 允许 USB 调试";
        if (!result.Split('\n').Any(x => x.TrimEnd().EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase))) return "没有检测到 BF66";
        return "BF66 已连接";
    }

    public async Task<string> ConnectAsync()
    {
        var status = await DeviceStatusAsync();
        if (status != "BF66 已连接") return status;
        var reverse = await RunAsync("reverse tcp:8787 tcp:8787");
        if (!string.IsNullOrWhiteSpace(reverse) && !reverse.Contains("8787")) return "USB 通道建立失败：" + reverse.Trim();
        await RunAsync("shell am force-stop com.codex.bf66display");
        await RunAsync($"shell am start -n com.codex.bf66display/.MainActivity --es token {_token}");
        return "BF66 已连接，画面同步中";
    }

    public async Task<string> InstallAsync()
    {
        if (!ApkAvailable) return "安装包不完整";
        var status = await DeviceStatusAsync();
        if (status != "BF66 已连接") return status;
        var output = await RunAsync($"install -r \"{_apk}\"");
        if (!output.Contains("Success", StringComparison.OrdinalIgnoreCase)) return "安装失败：" + output.Trim();
        return await ConnectAsync();
    }

    private async Task<string> RunAsync(string arguments)
    {
        if (!IsAvailable) return "adb unavailable";
        var info = new ProcessStartInfo(_adb, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await stdout) + (await stderr);
    }
}

internal sealed class MainForm : Form
{
    private readonly StateStore _store = new();
    private readonly string _token;
    private readonly ConnectionTracker _tracker = new();
    private readonly AdbManager _adb;
    private readonly TextBox _title = new() { Text = "BF66 显示屏", Dock = DockStyle.Fill };
    private readonly TextBox _message = new() { Text = "已连接电脑控制端", Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _orientation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 };
    private readonly CheckBox _clock = new() { Text = "显示时钟", Checked = true, AutoSize = true };
    private readonly NumericUpDown _fontSize = new() { Minimum = 16, Maximum = 160, Value = 42, Width = 70 };
    private readonly ComboBox _fit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Label _imageName = new() { Text = "未选择图片", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _status = new() { Text = "正在启动…", AutoSize = true, ForeColor = Color.FromArgb(30, 94, 166) };
    private Color _background = ColorTranslator.FromHtml("#081525");
    private Color _foreground = ColorTranslator.FromHtml("#F4F8FF");
    private string? _imagePath;
    private DisplayServer? _server;
    private readonly System.Windows.Forms.Timer _connectionTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _usageTimer = new() { Interval = 5000 };
    private bool _checking;
    private bool _deviceOpened;
    private bool _usageRefreshing;

    public MainForm(bool startLandscape = false, string? startupImage = null)
    {
        _token = ConnectionToken.LoadOrCreate();
        _adb = new AdbManager(_token);
        Text = "BF66 显示控制台";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 650);
        Size = new Size(820, 720);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 252);

        _fit.Items.AddRange(new object[] { "完整显示", "铺满裁剪" });
        _fit.SelectedIndex = 0;
        _mode.Items.AddRange(new object[] { "自定义画面", "GPT Usage", "桌宠" });
        _mode.SelectedIndex = 1;
        _orientation.Items.AddRange(new object[] { "竖屏", "横屏" });
        _orientation.SelectedIndex = startLandscape ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(startupImage) && SupportedImage(startupImage))
        {
            _imagePath = startupImage;
            _imageName.Text = Path.GetFileName(startupImage);
            _mode.SelectedIndex = 0;
        }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var heading = new Label { Text = "设置 BF66 上的画面", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(heading);
        layout.Controls.Add(Field("标题", _title));
        layout.Controls.Add(Field("正文", _message));

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
        options.Controls.Add(new Label { Text = "显示模式", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        options.Controls.Add(_mode);
        options.Controls.Add(Spacer(8));
        options.Controls.Add(new Label { Text = "方向", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        options.Controls.Add(_orientation);
        options.Controls.Add(Spacer(8));
        options.Controls.Add(_clock);
        options.Controls.Add(Spacer(8));
        options.Controls.Add(new Label { Text = "字号", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        options.Controls.Add(_fontSize);
        options.Controls.Add(Spacer(8));
        options.Controls.Add(new Label { Text = "图片", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        options.Controls.Add(_fit);
        layout.Controls.Add(options);

        var colors = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        colors.Controls.Add(Button("背景颜色", (_, _) => ChooseColor(true)));
        colors.Controls.Add(Button("文字颜色", (_, _) => ChooseColor(false)));
        colors.Controls.Add(Button("选择图片", (_, _) => ChooseImage()));
        colors.Controls.Add(Button("清除图片", (_, _) => { _imagePath = null; _imageName.Text = "未选择图片"; ApplyState(); }));
        layout.Controls.Add(colors);

        var imageRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 8) };
        imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        imageRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        imageRow.Controls.Add(new Label { Text = "当前图片", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        imageRow.Controls.Add(_imageName, 1, 0);
        layout.Controls.Add(imageRow);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var apply = PrimaryButton("立即更新画面", (_, _) => ApplyState());
        var connect = Button("连接并打开 BF66", async (_, _) => await ConnectAsync());
        var install = Button("安装显示端到 BF66", async (_, _) => await InstallAsync());
        actions.Controls.Add(apply);
        actions.Controls.Add(connect);
        actions.Controls.Add(install);
        layout.Controls.Add(actions);

        var statusPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        statusPanel.Controls.Add(new Label { Text = "●", ForeColor = Color.FromArgb(38, 166, 91), AutoSize = true, Margin = new Padding(0, 2, 8, 0) });
        statusPanel.Controls.Add(_status);
        layout.Controls.Add(statusPanel);
        Controls.Add(layout);

        _title.TextChanged += (_, _) => ApplyState();
        _message.TextChanged += (_, _) => ApplyState();
        _clock.CheckedChanged += (_, _) => ApplyState();
        _fontSize.ValueChanged += (_, _) => ApplyState();
        _fit.SelectedIndexChanged += (_, _) => ApplyState();
        _mode.SelectedIndexChanged += async (_, _) => { ApplyState(); if (_mode.SelectedIndex == 1) await RefreshUsageAsync(); };
        _orientation.SelectedIndexChanged += (_, _) => ApplyState();
        Load += OnLoaded;
        FormClosed += OnClosed;
        _connectionTimer.Tick += async (_, _) => await CheckConnectionAsync();
        _usageTimer.Tick += async (_, _) => await RefreshUsageAsync();
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            _server = new DisplayServer(_store, _token, _tracker);
            await _server.StartAsync();
            ApplyState();
            _status.Text = "控制服务已启动，等待 BF66";
            _connectionTimer.Start();
            _usageTimer.Start();
            await RefreshUsageAsync();
            await CheckConnectionAsync();
        }
        catch (Exception ex) { _status.Text = "启动失败：" + ex.Message; }
    }

    private async void OnClosed(object? sender, FormClosedEventArgs e)
    {
        _connectionTimer.Stop();
        _usageTimer.Stop();
        if (_server is not null) await _server.DisposeAsync();
    }

    private void ApplyState()
    {
        var mode = _mode.SelectedIndex switch { 1 => "usage", 2 => "pet", _ => "custom" };
        _store.Update(mode, _orientation.SelectedIndex == 1 ? "landscape" : "portrait", _title.Text, _message.Text, _background, _foreground, (int)_fontSize.Value, _clock.Checked, _fit.SelectedIndex == 1 ? "cover" : "contain", _imagePath);
        _status.Text = "画面已更新";
    }

    private async Task RefreshUsageAsync()
    {
        if (_usageRefreshing) return;
        _usageRefreshing = true;
        try
        {
            var snapshot = await Task.Run(UsageReader.Read);
            _store.UpdateUsage(snapshot);
            if (_mode.SelectedIndex == 1)
                _status.Text = "GPT Usage 已更新：" + snapshot.GeneratedAt.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            if (_mode.SelectedIndex == 1) _status.Text = "GPT Usage 读取失败：" + ex.Message;
        }
        finally { _usageRefreshing = false; }
    }

    private async Task CheckConnectionAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var status = await _adb.DeviceStatusAsync();
            _status.Text = status;
            if (status == "BF66 已连接" && !_deviceOpened)
            {
                _status.Text = await _adb.ConnectAsync();
                _deviceOpened = _status.Text.StartsWith("BF66 已连接");
            }
            else if (status != "BF66 已连接")
            {
                _deviceOpened = false;
                if (_tracker.TryGetWireless(out var address))
                    _status.Text = "BF66 已通过同一 Wi-Fi 连接（" + address + "）";
            }
        }
        finally { _checking = false; }
    }

    private async Task ConnectAsync()
    {
        _status.Text = "正在连接…";
        _status.Text = await _adb.ConnectAsync();
        _deviceOpened = _status.Text.StartsWith("BF66 已连接");
    }

    private async Task InstallAsync()
    {
        _status.Text = "正在安装，请稍候…";
        _status.Text = await _adb.InstallAsync();
        _deviceOpened = _status.Text.StartsWith("BF66 已连接");
    }

    private void ChooseColor(bool background)
    {
        using var dialog = new ColorDialog { FullOpen = true, Color = background ? _background : _foreground };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (background) _background = dialog.Color; else _foreground = dialog.Color;
        ApplyState();
    }

    private void ChooseImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 BF66 要显示的图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!SupportedImage(dialog.FileName))
        {
            MessageBox.Show(this, "不支持此文件格式。请选择 JPG、PNG、WebP、GIF 或 BMP 图片。", "无法选择文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _imagePath = dialog.FileName;
        _imageName.Text = Path.GetFileName(dialog.FileName);
        ApplyState();
    }

    private static bool SupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp";

    private static Control Field(string label, Control input)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.FromArgb(70, 78, 92) });
        panel.Controls.Add(input);
        return panel;
    }

    private static Button Button(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 38, Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 10, 0), FlatStyle = FlatStyle.System };
        b.Click += click;
        return b;
    }

    private static Button PrimaryButton(string text, EventHandler click)
    {
        var b = Button(text, click);
        b.BackColor = Color.FromArgb(33, 100, 220);
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private static Control Spacer(int width) => new Panel { Width = width, Height = 1 };
}
