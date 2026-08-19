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
    public override string ModuleAuthor => "NiGHT & K4ryuu (Optimized by 0-GC)";
    public override string ModuleName => "AFK Manager (Lite)";
    public override string ModuleVersion => "2.0.0_UltimateZeroGC";
    
    public required AFKManagerConfig Config { get; set; }
    private CCSGameRules? _gGameRulesProxy;
    
    private readonly Dictionary<uint, PlayerInfo> _gPlayerInfo = []; 
    
    // 快取玩家名單，預先分配 64 人空間，永不產生 GC
    private readonly List<CCSPlayerController> _activePlayersCache = new(64);

    public void OnConfigParsed(AFKManagerConfig config)
    {
        Config = config;
        AddTimer(Config.Timer, AfkTimer_Callback, TimerFlags.REPEAT);
    }

    private class PlayerInfo
    {
        // 降級為最底層的浮點數，徹底消滅 new Vector() 與 new QAngle()
        public float AngleX, AngleY, AngleZ;
        public float OriginX, OriginY, OriginZ;
        public bool IsTracking; // 防止初始狀態誤判
        
        public float AfkTime;
        public int AfkWarningCount;
    }

    // 更新快取名單的函數 (O(N) 極速重構，0 記憶體分配)
    private void RefreshPlayersCache()
    {
        _activePlayersCache.Clear();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected })
            {
                _activePlayersCache.Add(p);
            }
        }
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            Server.NextFrame(() =>
            {
                _gGameRulesProxy = null;
                foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
                {
                    _gGameRulesProxy = entity.GameRules;
                    break;
                }
                
                if (_gGameRulesProxy is null)
                    throw new Exception("Failed to find game rules proxy entity.");
                    
                RefreshPlayersCache();
            });
        });
        
        RegisterListener<Listeners.OnClientConnected>(playerSlot =>
        {
            var finalSlot = (uint)playerSlot + 1;
            // 使用 target-typed new() 僅在連線時分配一次記憶體
            _gPlayerInfo.TryAdd(finalSlot, new PlayerInfo());
        });
        
        RegisterEventHandler<EventPlayerConnectFull>((@event, info) => 
        {
            Server.NextFrame(RefreshPlayersCache);
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => 
        {
            _gPlayerInfo.Remove((uint)playerSlot + 1);
            Server.NextFrame(RefreshPlayersCache);
        });

        // 攔截玩家重生，防止 AFK 狀態被死亡/換局傳送給洗白
        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            if (@event.Userid is not { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected } player)
                return HookResult.Continue;

            if (_gPlayerInfo.TryGetValue(player.Index, out var data) && data is not null)
            {
                Server.NextFrame(() =>
                {
                    if (player is { IsValid: true, PawnIsAlive: true } && player.PlayerPawn.Value is { } pawn)
                    {
                        var angles = pawn.EyeAngles;
                        var origin = pawn.CBodyComponent?.SceneNode?.AbsOrigin;

                        // 純數值更新基準座標，無 new 記憶體分配，且不清除 AFK 累積時間
                        data.AngleX = angles?.X ?? 0; data.AngleY = angles?.Y ?? 0; data.AngleZ = angles?.Z ?? 0;
                        data.OriginX = origin?.X ?? 0; data.OriginY = origin?.Y ?? 0; data.OriginZ = origin?.Z ?? 0;
                        data.IsTracking = true;
                    }
                });
            }
            return HookResult.Continue;
        });
    }

    private void AfkTimer_Callback()
    {
        if (_gGameRulesProxy is null or { FreezePeriod: true } || (Config.SkipWarmup && _gGameRulesProxy is { WarmupPeriod: true }))
            return;

        // 直接讀取 C# 記憶體內的快取名單，不再跨界呼叫底層 C++
        foreach (var player in _activePlayersCache)
        {
            // 雙重防護，確保快取中的玩家尚未斷線
            if (player is not { IsValid: true }) continue; 

            if (player.ControllingBot || !_gPlayerInfo.TryGetValue(player.Index, out var data) || data is null)
                continue;

            if (player is { LifeState: (byte)LifeState_t.LIFE_ALIVE, Team: CsTeam.Terrorist or CsTeam.CounterTerrorist })
            {
                if (player.PlayerPawn.Value is not { } playerPawn) continue;

                var angles = playerPawn.EyeAngles;
                var origin = playerPawn.CBodyComponent?.SceneNode?.AbsOrigin;

                float cAngX = angles?.X ?? 0, cAngY = angles?.Y ?? 0, cAngZ = angles?.Z ?? 0;
                float cOrgX = origin?.X ?? 0, cOrgY = origin?.Y ?? 0, cOrgZ = origin?.Z ?? 0;

                // 純浮點數精準比對，0 垃圾！
                if (data.IsTracking &&
                    data.AngleX == cAngX && data.AngleY == cAngY && 
                    data.OriginX == cOrgX && data.OriginY == cOrgY)
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
                    
                    // 覆寫新座標，依然是純數值替換
                    data.AngleX = cAngX; data.AngleY = cAngY; data.AngleZ = cAngZ;
                    data.OriginX = cOrgX; data.OriginY = cOrgY; data.OriginZ = cOrgZ;
                    data.IsTracking = true;
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
