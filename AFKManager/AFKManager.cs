using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace AFKManager;

public class AFKManagerConfig : BasePluginConfig
{
    public int AfkPunishAfterWarnings { get; set; } = 3;
    public int AfkPunishment { get; set; } = 1;
    public float AfkWarnInterval { get; set; } = 5.0f;
    public bool SkipWarmup { get; set; } = false;
    public float Timer { get; set; } = 5.0f;
}

public class AFKManager : BasePlugin, IPluginConfig<AFKManagerConfig>
{
    public override string ModuleAuthor => "NiGHT & K4ryuu (Cleaned)";
    public override string ModuleName => "AFK Manager (Lite)";
    public override string ModuleVersion => "1.0.0";
    
    public required AFKManagerConfig Config { get; set; }
    private CCSGameRules? _gGameRulesProxy;
    
    // .NET 10 集合表達式 (Collection Expression) 簡化實例化
    private readonly Dictionary<uint, PlayerInfo> _gPlayerInfo = []; 
    
    public void OnConfigParsed(AFKManagerConfig config)
    {
        Config = config;
        AddTimer(Config.Timer, AfkTimer_Callback, TimerFlags.REPEAT);
    }

    private class PlayerInfo
    {
        public QAngle? Angles { get; set; }
        public Vector? Origin { get; set; }
        public float AfkTime { get; set; }
        public int AfkWarningCount { get; set; }
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            Server.NextFrame(() =>
            {
                // 拔除 LINQ 的 .First()，改用純迴圈，達到真正的 0 GC 效能
                _gGameRulesProxy = null;
                foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
                {
                    _gGameRulesProxy = entity.GameRules;
                    break;
                }
                
                if (_gGameRulesProxy is null)
                    throw new Exception("Failed to find game rules proxy entity.");
            });
        });
        
        RegisterListener<Listeners.OnClientConnected>(playerSlot =>
        {
            var finalSlot = (uint)playerSlot + 1;
            
            // 使用現代 Dictionary 的 TryAdd 提升效能，並搭配 target-typed new()
            _gPlayerInfo.TryAdd(finalSlot, new PlayerInfo { Angles = new(), Origin = new() });
        });
        
        RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => _gPlayerInfo.Remove((uint)playerSlot + 1));
    }

    private void AfkTimer_Callback()
    {
        // C# 屬性模式匹配，取代又長又臭的 == null 與多重判斷
        if (_gGameRulesProxy is null or { FreezePeriod: true } || (Config.SkipWarmup && _gGameRulesProxy is { WarmupPeriod: true }))
            return;

        // 直接整合進 foreach，徹底消滅記憶體分配！
        foreach (var player in Utilities.GetPlayers())
        {
            // 使用 is not 模式匹配反向過濾
            if (player is not { IsBot: false, Connected: PlayerConnectedState.Connected }) 
                continue;

            // 模式匹配 is null，更安全且效能更好
            if (player.ControllingBot || !_gPlayerInfo.TryGetValue(player.Index, out var data) || data is null)
                continue;

            if (player is { LifeState: (byte)LifeState_t.LIFE_ALIVE, Team: CsTeam.Terrorist or CsTeam.CounterTerrorist })
            {
                // 屬性模式解構防護
                if (player.PlayerPawn.Value is not { } playerPawn) continue;

                var angles = playerPawn.EyeAngles;
                var origin = playerPawn.CBodyComponent?.SceneNode?.AbsOrigin;

                // 結合 is 模式匹配的 not null 檢查
                if (data is { Angles: not null, Origin: not null } &&
                    data.Angles.X == angles?.X && data.Angles.Y == angles?.Y && 
                    data.Origin.X == origin?.X && data.Origin.Y == origin?.Y)
                {
                    data.AfkTime += Config.Timer;
                    if (data.AfkTime < Config.AfkWarnInterval) continue;

                    if (data.AfkWarningCount >= Config.AfkPunishAfterWarnings)
                    {
                        string msgKey = Config.AfkPunishment switch { 0 => "ChatKillMessage", 1 => "ChatMoveMessage", _ => "ChatKickMessage" };
                        Server.PrintToChatAll(ReplaceVars(player, Localizer[msgKey].Value));
                        
                        if (Config.AfkPunishment == 0) playerPawn.CommitSuicide(false, true);
                        else if (Config.AfkPunishment == 1) player.ChangeTeam(CsTeam.Spectator);
                        else Server.ExecuteCommand($"kickid {player.UserId}");

                        data.AfkWarningCount = 0;
                        data.AfkTime = 0;
                        continue;
                    }

                    string warnKey = Config.AfkPunishment switch { 0 => "ChatWarningKillMessage", 1 => "ChatWarningMoveMessage", _ => "ChatWarningKickMessage" };
                    float remainingTime = (Config.AfkPunishAfterWarnings - data.AfkWarningCount) * Config.AfkWarnInterval;
                    player.PrintToChat(ReplaceVars(player, Localizer[warnKey].Value, remainingTime));
                    
                    data.AfkWarningCount++;
                    data.AfkTime = 0;
                }
                else
                {
                    data.AfkTime = 0;
                    data.AfkWarningCount = 0;
                    // 縮寫寫法
                    data.Angles = new(angles?.X, angles?.Y, angles?.Z);
                    data.Origin = new(origin?.X, origin?.Y, origin?.Z);
                }
            }
        }
    }

    private string ReplaceVars(CCSPlayerController player, string message, float timeAmount = 0.0f)
    {
        return Localizer["ChatPrefix"] + message.Replace("{playerName}", player.PlayerName)
                                              .Replace("{timeAmount}", $"{timeAmount:F0}");
    }
}
