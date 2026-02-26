using System.Collections.Concurrent;
using System.Text;
using AudioApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Scheduler;

namespace Advert;

[PluginMetadata(Id = "Advert", Version = "1.1.3", Name = "Advert", Author = "E!N", Website = "https://nova-hosting.ru/?ref=ein")]
public class Advert : BasePlugin
{
    private static readonly (string Tag, string Color)[] ColorReplacements =
    [
        ("{DEFAULT}", Helper.ChatColors.Default),
        ("{WHITE}", Helper.ChatColors.White),
        ("{DARKRED}", Helper.ChatColors.DarkRed),
        ("{GREEN}", Helper.ChatColors.Green),
        ("{LIGHTYELLOW}", Helper.ChatColors.LightYellow),
        ("{LIGHTBLUE}", Helper.ChatColors.LightBlue),
        ("{OLIVE}", Helper.ChatColors.Olive),
        ("{LIME}", Helper.ChatColors.Lime),
        ("{RED}", Helper.ChatColors.Red),
        ("{LIGHTPURPLE}", Helper.ChatColors.LightPurple),
        ("{PURPLE}", Helper.ChatColors.Purple),
        ("{GREY}", Helper.ChatColors.Grey),
        ("{YELLOW}", Helper.ChatColors.Yellow),
        ("{GOLD}", Helper.ChatColors.Gold),
        ("{SILVER}", Helper.ChatColors.Silver),
        ("{BLUE}", Helper.ChatColors.Blue),
        ("{DARKBLUE}", Helper.ChatColors.DarkBlue),
        ("{BLUEGREY}", Helper.ChatColors.BlueGrey),
        ("{MAGENTA}", Helper.ChatColors.Magenta),
        ("{LIGHTRED}", Helper.ChatColors.LightRed),
        ("{ORANGE}", Helper.ChatColors.Orange)
    ];

    private readonly ConcurrentDictionary<string, IAudioSource> _decodedSources = new();
    private readonly ILogger _logger;
    private readonly ISchedulerService _scheduler;

    private IAudioApi? _audioApi;
    private string? _cachedPanelMessage = string.Empty;
    private int _channelCounter;

    private ConfigModel _config = new();
    private IOptionsMonitor<ConfigModel> _configMonitor = null!;

    private int _currentAdIndex;
    private CancellationTokenSource? _timerToken;

    // 1 db center “refresh” timer (nem playerenként)
    private readonly object _centerLock = new();
    private CancellationTokenSource? _centerRepeatToken;
    private CancellationTokenSource? _centerStopToken;

    public Advert(ISwiftlyCore core) : base(core)
    {
        _scheduler = Core.Scheduler;
        _logger = Core.LoggerFactory.CreateLogger<Advert>();
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) { }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
        if (!interfaceManager.HasSharedInterface("audio"))
        {
            Core.Logger.LogWarning("Audio shared interface not found. Install/enable the 'Audio' plugin.");
            _audioApi = null;
            return;
        }

        _audioApi = interfaceManager.GetSharedInterface<IAudioApi>("audio");
    }

    public override void Load(bool hotReload)
    {
        try
        {
            const string fileName = "config.jsonc";
            const string section = "ConfigModel";

            Core.Configuration.InitializeJsonWithModel<ConfigModel>(fileName, section);
            Core.Configuration.Configure(cfg => cfg.AddJsonFile(fileName, false, true));

            var services = new ServiceCollection();
            services.AddSwiftly(Core)
                .AddOptionsWithValidateOnStart<ConfigModel>()
                .BindConfiguration(section);

            var provider = services.BuildServiceProvider();

            _configMonitor = provider.GetRequiredService<IOptionsMonitor<ConfigModel>>();
            _config = _configMonitor.CurrentValue;

            _configMonitor.OnChange(cfg =>
            {
                _config = cfg;
                _currentAdIndex = 0;
                _decodedSources.Clear();
                RestartTimer();
            });

            // Panel advert
            Core.GameEvent.HookPre<EventRoundEnd>(@event =>
            {
                if (string.IsNullOrEmpty(_cachedPanelMessage)) return HookResult.Continue;

                var winnerByte = @event.Winner;
                Team? winner = Enum.IsDefined(typeof(Team), winnerByte) ? (Team)winnerByte : null;

                if (winner is null or Team.None) return HookResult.Continue;

                PanelAdvertising(_cachedPanelMessage, (byte)winner.Value);
                return HookResult.Continue;
            });

            // ÚJ: WelcomeMessage connect után
            Core.GameEvent.HookPost<EventPlayerConnectFull>(OnPlayerConnectFull);

            StartTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin.");
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event)
    {
        if (@event == null) return HookResult.Continue;

        var player = @event.Accessor.GetPlayer("userid");
        if (player == null || !player.IsValid) return HookResult.Continue;

        if (!_config.WelcomeEnabled) return HookResult.Continue;
        if (string.IsNullOrWhiteSpace(_config.WelcomeMessage)) return HookResult.Continue;

        // Delay után küldjük (mint a puskázott plugin)
        _scheduler.DelayBySeconds(_config.WelcomeDelay, () =>
        {
            if (!player.IsValid) return;

            var msg = ReplaceAllTags(_config.WelcomeMessage, player);

            switch (_config.WelcomeLocation)
            {
                case WelcomeLocationType.Chat:
                    player.SendChat(msg);
                    break;

                case WelcomeLocationType.Center:
                    // Sima centernek nincs duration overload, itt 1x küldjük
                    player.SendCenter(msg);
                    break;

                case WelcomeLocationType.Html:
                    // HTML-nek van duration-ja -> ezt ajánlom ha “maradjon kint”
                    player.SendCenterHTML(msg, _config.WelcomeHtmlDuration * 1000);
                    break;

                case WelcomeLocationType.Alert:
                    player.SendAlert(msg);
                    break;
            }
        });

        return HookResult.Continue;
    }

    private void StartTimer()
    {
        _timerToken = _scheduler.DelayAndRepeatBySeconds(_config.Interval, _config.Interval, ShowAdvertising());
    }

    private void RestartTimer()
    {
        _timerToken?.Cancel();
        StartTimer();
    }

    private Action ShowAdvertising()
    {
        return () =>
        {
            if (_config.AdvertList.Count == 0) return;

            if (_currentAdIndex >= _config.AdvertList.Count)
                _currentAdIndex = 0;

            var currentGroupDict = _config.AdvertList[_currentAdIndex];
            _currentAdIndex++;

            foreach (var innerAds in currentGroupDict.Values)
            {
                foreach (var (location, rawMessage) in innerAds)
                {
                    if (string.IsNullOrWhiteSpace(rawMessage)) continue;

                    var finalMessage = ReplaceAllTags(rawMessage, player: null);

                    if (location == AdvertLocationType.Panel)
                    {
                        _cachedPanelMessage = finalMessage;
                        continue;
                    }

                    // Center: 1 timer / mindenki
                    if (location == AdvertLocationType.Center)
                    {
                        SendCenterToAllWithDuration(finalMessage);
                        continue;
                    }

                    foreach (var player in Core.PlayerManager.GetAllPlayers())
                    {
                        if (!player.IsValid) continue;

                        switch (location)
                        {
                            case AdvertLocationType.Chat:
                                player.SendChat(finalMessage);
                                break;

                            case AdvertLocationType.Html:
                                player.SendCenterHTML(finalMessage, _config.HtmlDuration * 1000);
                                break;

                            case AdvertLocationType.Alert:
                                player.SendAlert(finalMessage);
                                break;

                            case AdvertLocationType.Sound:
                                SoundAdvertising(finalMessage);
                                break;
                        }
                    }
                }
            }
        };
    }

    // 1 db timer, ami frissíti a center üzenetet mindenkinek
    private void SendCenterToAllWithDuration(string message)
    {
        foreach (var p in Core.PlayerManager.GetAllPlayers())
        {
            if (!p.IsValid) continue;
            p.SendCenter(message);
        }

        if (_config.CenterDuration <= 0f) return;

        var refreshEvery = _config.CenterRefreshEvery;
        if (refreshEvery < 0.5f) refreshEvery = 0.5f;
        if (refreshEvery > 5.0f) refreshEvery = 5.0f;

        lock (_centerLock)
        {
            _centerRepeatToken?.Cancel();
            _centerStopToken?.Cancel();

            CancellationTokenSource? repeatToken = null;

            repeatToken = _scheduler.DelayAndRepeatBySeconds(refreshEvery, refreshEvery, () =>
            {
                foreach (var p in Core.PlayerManager.GetAllPlayers())
                {
                    if (!p.IsValid) continue;
                    p.SendCenter(message);
                }
            });

            _centerRepeatToken = repeatToken;

            _centerStopToken = _scheduler.DelayBySeconds(_config.CenterDuration, () =>
            {
                lock (_centerLock)
                {
                    _centerRepeatToken?.Cancel();
                    _centerRepeatToken = null;
                    _centerStopToken?.Cancel();
                    _centerStopToken = null;
                }
            });

            if (_centerRepeatToken != null)
                _scheduler.StopOnMapChange(_centerRepeatToken);

            if (_centerStopToken != null)
                _scheduler.StopOnMapChange(_centerStopToken);
        }
    }

    private void PanelAdvertising(string finalMessage, byte teamByte)
    {
        byte finalEvent = teamByte switch
        {
            (byte)Team.T => (int)RoundEndReason.TerroristsWin,
            (byte)Team.CT => (int)RoundEndReason.CTsWin,
            _ => (int)RoundEndReason.RoundDraw
        };

        Core.GameEvent.Fire<EventCsWinPanelRound>(@event =>
        {
            @event.FinalEvent = finalEvent;
            @event.FunfactToken = finalMessage;
        });
    }

    private void SoundAdvertising(string soundPath)
    {
        if (_audioApi == null) return;
        if (string.IsNullOrWhiteSpace(soundPath)) return;

        var resolvedPath = ResolvePath(soundPath);

        if (!File.Exists(resolvedPath))
        {
            _logger.LogWarning("Audio file not found: {ResolvedPath}", resolvedPath);
            return;
        }

        IAudioSource source;
        try
        {
            source = _decodedSources.GetOrAdd(resolvedPath, path => _audioApi.DecodeFromFile(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode sound file: {ResolvedPath}", resolvedPath);
            return;
        }

        var channelId = $"advert.{Interlocked.Increment(ref _channelCounter)}";
        var channel = _audioApi.UseChannel(channelId);
        channel.SetSource(source);
        channel.SetVolumeToAll(_config.Volume);

        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (!player.IsValid || player.IsFakeClient) continue;
            channel.Play(player.PlayerID);
        }
    }

    private string ResolvePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath)) return configuredPath;
        var dataPath = Path.Combine(Core.PluginDataDirectory, configuredPath);
        return File.Exists(dataPath) ? dataPath : Path.Combine(Core.PluginPath, configuredPath);
    }

    // ÚJ: player kontextus támogatás a {player}/{PLAYER} placeholderhez
    private string ReplaceAllTags(string message, IPlayer? player)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var sb = new StringBuilder(message);
        var now = DateTime.Now;

        sb.Replace("{IP}", Core.Engine.ServerIP);
        sb.Replace("{PORT}", (Core.ConVar.Find<int>("hostport")?.Value ?? 0).ToString());
        sb.Replace("{DATE}", now.ToString("dd-MM-yyyy"));
        sb.Replace("{TIME}", now.ToString("HH:mm:ss"));
        sb.Replace("{PL}", Core.PlayerManager.PlayerCount.ToString());
        sb.Replace("\n", "\u2029");

        if (player != null && player.IsValid)
        {
            var name = player.Controller?.PlayerName ?? "Player";
            sb.Replace("{player}", name);
            sb.Replace("{PLAYER}", name);
        }

        if (message.Contains("{MAP}", StringComparison.Ordinal))
        {
            var currentMap = Core.Engine.GlobalVars.MapName;
            var mapReplacement = _config.MapsName.GetValueOrDefault(currentMap, currentMap);
            sb.Replace("{MAP}", mapReplacement);
        }

        if (message.Contains("{SERVERNAME}", StringComparison.Ordinal))
        {
            var serverName = Core.ConVar.Find<string>("hostname")?.Value ?? "Unknown";
            sb.Replace("{SERVERNAME}", serverName);
        }

        foreach (var (tag, color) in ColorReplacements)
            sb.Replace(tag, color);

        return sb.ToString();
    }

    // Meglévő hirdetésekhez (player nélkül)
    private string ReplaceAllTags(string message)
        => ReplaceAllTags(message, player: null);

    public override void Unload()
    {
        _timerToken?.Cancel();

        lock (_centerLock)
        {
            _centerRepeatToken?.Cancel();
            _centerStopToken?.Cancel();
            _centerRepeatToken = null;
            _centerStopToken = null;
        }

        _decodedSources.Clear();
    }
}

public class ConfigModel
{
    public float Interval { get; set; } = 15.0f;

    public int HtmlDuration { get; set; } = 5;

    // Center hirdetések “hossza” (sec)
    public float CenterDuration { get; set; } = 6.0f;

    // Center refresh gyakoriság (sec) – 1.0–2.0 ajánlott
    public float CenterRefreshEvery { get; set; } = 1.0f;

    public float Volume { get; set; } = 0.5f;

    // ÚJ: Welcome
    public bool WelcomeEnabled { get; set; } = true;

    // Másodperc
    public float WelcomeDelay { get; set; } = 2.0f;

    // Üzenet (működik: {player}, {IP}, {PORT}, {SERVERNAME}, {DATE}, {TIME}, {MAP}, színek)
    public string WelcomeMessage { get; set; } = "{GREEN}ÜDV {player}{DEFAULT} a szerveren: {GOLD}{SERVERNAME}{DEFAULT}!";

    public WelcomeLocationType WelcomeLocation { get; set; } = WelcomeLocationType.Chat;

    // csak akkor kell, ha WelcomeLocation == Html
    public int WelcomeHtmlDuration { get; set; } = 6;

    public Dictionary<string, string> MapsName { get; set; } = new()
    {
        { "de_dust2", "Dust II" },
        { "de_mirage", "Mirage" },
        { "awp_lego_2", "AWP Lego 2" },
        { "de_inferno", "Inferno" }
    };

    public List<Dictionary<string, Dictionary<AdvertLocationType, string>>> AdvertList { get; init; } =
    [
        new()
        {
            ["test1"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Chat] = "test in chat"
            }
        },
        new()
        {
            ["test2"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Center] = "test in center"
            }
        },
        new()
        {
            ["test3"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Alert] = "test in alert"
            }
        },
        new()
        {
            ["test4"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Html] = "<b><font color='lime'>test in</font> <font color='white'>html</font></b>"
            }
        },
        new()
        {
            ["test5"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Panel] = "<b><font color='lime'>test in</font> <font color='white'>panel</font></b>"
            }
        },
        new()
        {
            ["test6"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Alert] = "test in alert",
                [AdvertLocationType.Chat] = "and in chat"
            }
        },
        new()
        {
            ["test7"] = new Dictionary<AdvertLocationType, string>
            {
                [AdvertLocationType.Sound] = "test_in_audio.mp3"
            }
        }
    ];
}

public enum AdvertLocationType
{
    Chat,
    Center,
    Alert,
    Html,
    Panel,
    Sound
}

public enum WelcomeLocationType
{
    Chat,
    Center,
    Html,
    Alert
}
