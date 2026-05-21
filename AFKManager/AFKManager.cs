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
    private readonly Dictionary<uint, PlayerInfo> _gPlayerInfo = new();
    
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
                _gGameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules ??
                    throw new Exception("Failed to find game rules proxy entity.");
            });
        });
        
        RegisterListener<Listeners.OnClientConnected>(playerSlot =>
        {
            var finalSlot = (uint)playerSlot + 1;
            if (!_gPlayerInfo.ContainsKey(finalSlot))
                _gPlayerInfo.Add(finalSlot, new PlayerInfo { Angles = new QAngle(), Origin = new Vector() });
        });
        
        RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot => _gPlayerInfo.Remove((uint)playerSlot + 1));
    }

    private void AfkTimer_Callback()
    {
        if (_gGameRulesProxy == null || _gGameRulesProxy.FreezePeriod || (Config.SkipWarmup && _gGameRulesProxy.WarmupPeriod))
            return;

        // 修正點 1: 使用 .Connected 來匹配 API
        var players = Utilities.GetPlayers().Where(x => x is { IsBot: false, Connected: PlayerConnectedState.Connected });
        
        foreach (var player in players)
        {
            // 修正點 2: 使用 TryGetValue 並確保變數 data 在後續流程中是有效的
            if (player.ControllingBot || !_gPlayerInfo.TryGetValue(player.Index, out var data) || data == null)
                continue;

            if (player is { LifeState: (byte)LifeState_t.LIFE_ALIVE, Team: CsTeam.Terrorist or CsTeam.CounterTerrorist })
            {
                var playerPawn = player.PlayerPawn.Value;
                if (playerPawn == null) continue;

                var angles = playerPawn.EyeAngles;
                var origin = playerPawn.CBodyComponent?.SceneNode?.AbsOrigin;

                if (data.Angles != null && data.Origin != null &&
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
                    data.Angles = new QAngle(angles?.X, angles?.Y, angles?.Z);
                    data.Origin = new Vector(origin?.X, origin?.Y, origin?.Z);
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
