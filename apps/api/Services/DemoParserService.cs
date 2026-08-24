using CsDemoMap.Api.Models;
using DemoFile;
using DemoFile.Game.Cs;

namespace CsDemoMap.Api.Services;

public sealed class DemoParserService
{
    public const int SampleRate = 8;
    public const int UtilitySampleRate = 16;
    private const int TickRate = 64;
    private const int PlayerSampleStride = TickRate / SampleRate;
    private const int UtilitySampleStride = TickRate / UtilitySampleRate;

    public async Task<DemoTimeline> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        var demo = new CsDemoParser();
        var frames = new List<DemoFrame>();
        var events = new List<TimelineEvent>();
        var utilityTrackBuilders = new List<UtilityTrackBuilder>();
        var utilityEffectBuilders = new List<UtilityEffectBuilder>();
        var activeProjectiles = new Dictionary<uint, UtilityTrackBuilder>();
        var detonatedProjectiles = new HashSet<uint>();
        var activeEffects = new Dictionary<string, UtilityEffectBuilder>();
        var playerUtilityStates = new List<PlayerUtilityState>();
        var playerEquipmentStates = new List<PlayerEquipmentState>();
        var lastPlayerUtilities = new Dictionary<string, string>();
        var lastPlayerEquipment = new Dictionary<string, string>();
        var lastPlayerPositions = new Dictionary<string, (int Tick, float X, float Y, float Z)>();
        var mapName = "unknown";
        var totalTicks = 0;
        var durationSeconds = 0d;
        var lastSampledTick = int.MinValue;
        var lastUtilitySampledTick = int.MinValue;
        var roundCounter = 0;
        var currentRoundStartTick = 0;
        var currentLiveStartTick = 0;
        var roundEnded = false;
        var bombTerminalState = "unavailable";
        BombSnapshot? lastBombSnapshot = null;

        demo.PacketEvents.SvcServerInfo += info => mapName = info.MapName;
        demo.DemoEvents.DemoFileInfo += info =>
        {
            totalTicks = info.PlaybackTicks;
            durationSeconds = info.PlaybackTime;
        };

        demo.Source1GameEvents.RoundStart += _ =>
        {
            currentRoundStartTick = Math.Max(0, demo.CurrentDemoTick.Value);
            currentLiveStartTick = 0;
            roundCounter = Math.Max(roundCounter + 1, demo.GameRules.TotalRoundsPlayed + 1);
            roundEnded = false;
            bombTerminalState = "unavailable";
            lastBombSnapshot = null;
            AddEvent("round-start", "回合开始", $"第 {roundCounter} 回合");
        };
        demo.Source1GameEvents.RoundFreezeEnd += _ =>
        {
            currentLiveStartTick = Math.Max(0, demo.CurrentDemoTick.Value);
            AddEvent("round-live", "冻结时间结束", $"第 {Math.Max(1, roundCounter)} 回合");
        };
        demo.Source1GameEvents.RoundEnd += e =>
        {
            roundEnded = true;
            AddEvent("round-end", "回合结束", $"胜方队伍编号 {e.Winner} · 原因 {e.Reason}");
        };
        demo.Source1GameEvents.PlayerDeath += e =>
            AddEvent(
                "kill",
                $"{e.Attacker?.PlayerName ?? "世界"} → {e.Player?.PlayerName ?? "未知玩家"}",
                $"{e.Weapon}{(e.Headshot ? " · 爆头" : string.Empty)}");
        demo.Source1GameEvents.BombPlanted += e =>
        {
            bombTerminalState = "planted";
            AddEvent("bomb-planted", "炸弹已安放", $"{e.Player?.PlayerName ?? "未知玩家"} · 区域 {e.Site}");
        };
        demo.Source1GameEvents.BombDefused += e =>
        {
            bombTerminalState = "defused";
            AddEvent("bomb-defused", "炸弹已拆除", e.Player?.PlayerName);
        };
        demo.Source1GameEvents.BombExploded += e =>
        {
            bombTerminalState = "exploded";
            AddEvent("bomb-exploded", "炸弹已爆炸", $"区域 {e.Site}");
        };
        demo.Source1GameEvents.SmokegrenadeDetonate += e => DetonateProjectile(e.Entityid);
        demo.Source1GameEvents.FlashbangDetonate += e => DetonateProjectile(e.Entityid);
        demo.Source1GameEvents.HegrenadeDetonate += e => DetonateProjectile(e.Entityid);
        demo.Source1GameEvents.DecoyDetonate += e => DetonateProjectile(e.Entityid);

        demo.OnCommandFinishPersistent += CaptureState;

        var reader = DemoFileReader.Create(demo, stream);
        await reader.ReadAllAsync(cancellationToken);

        var finalTick = totalTicks > 0 ? totalTicks : Math.Max(0, demo.CurrentDemoTick.Value);
        FinalizeMissing(activeProjectiles, new HashSet<uint>(), finalTick + 1);
        FinalizeMissing(activeEffects, new HashSet<string>(), finalTick + 1);

        if (durationSeconds <= 0 && totalTicks > 0)
            durationSeconds = totalTicks / (double)TickRate;

        return new DemoTimeline(
            new DemoMetadata(
                Path.GetFileName(fileName),
                mapName,
                TickRate,
                SampleRate,
                finalTick,
                durationSeconds > 0 ? durationSeconds : demo.Elapsed.TotalSeconds),
            frames,
            utilityTrackBuilders.Select(item => item.Build()).ToArray(),
            utilityEffectBuilders.Select(item => item.Build()).ToArray(),
            playerUtilityStates,
            playerEquipmentStates,
            events.OrderBy(item => item.Tick).ToArray());

        void AddEvent(string type, string title, string? detail = null)
        {
            var tick = Math.Max(0, demo.CurrentDemoTick.Value);
            events.Add(new TimelineEvent(tick, tick / (double)TickRate, type, title, detail));
        }

        void CaptureState()
        {
            var tick = demo.CurrentDemoTick.Value;
            if (tick < 0)
                return;

            if (tick != lastUtilitySampledTick && tick % UtilitySampleStride == 0)
            {
                lastUtilitySampledTick = tick;
                CaptureUtilities(tick);
            }

            if (tick == lastSampledTick || tick % PlayerSampleStride != 0)
                return;

            lastSampledTick = tick;
            var players = demo.Players
                .Where(player => player.PlayerPawn is not null)
                .Select(player =>
                {
                    var pawn = player.PlayerPawn!;
                    CapturePlayerUtilities(player, pawn, tick);
                    CapturePlayerEquipment(player, pawn, tick);
                    var stats = player.ActionTrackingServices?.MatchStats;
                    var playerId = player.SteamID.ToString();
                    var velocity = CaptureVelocity(playerId, pawn, tick);
                    return new PlayerSnapshot(
                        playerId,
                        player.PlayerName,
                        TeamName(player.CSTeamNum),
                        pawn.IsAlive,
                        pawn.Health,
                        pawn.Origin.X,
                        pawn.Origin.Y,
                        pawn.Origin.Z,
                        pawn.EyeAngles.Yaw,
                        velocity.X,
                        velocity.Y,
                        velocity.Z,
                        NormalizeRegion(pawn.LastPlaceName),
                        pawn.ActiveWeapon?.EconItem.Name,
                        stats?.Kills ?? 0,
                        stats?.Deaths ?? 0);
                })
                .ToArray();

            if (players.Length > 0)
            {
                var bomb = CaptureBomb(players);
                frames.Add(new DemoFrame(
                    tick,
                    tick / (double)TickRate,
                    players,
                    CaptureRound(tick, bomb.State),
                    bomb,
                    CaptureZones(players)));
            }
        }

        (float X, float Y, float Z) CaptureVelocity(string playerId, CCSPlayerPawn pawn, int tick)
        {
            var position = (Tick: tick, pawn.Origin.X, pawn.Origin.Y, pawn.Origin.Z);
            if (!lastPlayerPositions.TryGetValue(playerId, out var previous) || tick <= previous.Tick)
            {
                lastPlayerPositions[playerId] = position;
                return (0, 0, 0);
            }

            lastPlayerPositions[playerId] = position;
            var seconds = (tick - previous.Tick) / (float)TickRate;
            var velocity = (
                X: (position.X - previous.X) / seconds,
                Y: (position.Y - previous.Y) / seconds,
                Z: (position.Z - previous.Z) / seconds);
            var speedSquared = velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z;
            return speedSquared > 1_000_000 ? (0, 0, 0) : velocity;
        }

        void CapturePlayerUtilities(CCSPlayerController player, CCSPlayerPawn pawn, int tick)
        {
            var items = pawn.Weapons
                .OfType<CBaseCSGrenade>()
                .Select(grenade => new
                {
                    Type = UtilityTypeFromWeapon(grenade.EconItem.Name),
                    Count = Math.Max(1, grenade.GrenadeCount)
                })
                .Where(item => item.Type is not null)
                .GroupBy(item => item.Type!)
                .Select(group => new CarriedUtility(group.Key, group.Max(item => item.Count)))
                .OrderBy(item => item.Type)
                .ToArray();

            var playerId = player.SteamID.ToString();
            var signature = string.Join('|', items.Select(item => $"{item.Type}:{item.Count}"));
            if (lastPlayerUtilities.TryGetValue(playerId, out var previous) && previous == signature)
                return;

            lastPlayerUtilities[playerId] = signature;
            playerUtilityStates.Add(new PlayerUtilityState(tick, tick / (double)TickRate, playerId, items));
        }

        void CapturePlayerEquipment(CCSPlayerController player, CCSPlayerPawn pawn, int tick)
        {
            var items = pawn.Weapons
                .Select(weapon => new EquipmentItem(
                    weapon.EconItem.Name,
                    EquipmentCategory(weapon.EconItem.Name),
                    weapon is CBaseCSGrenade grenade ? Math.Max(1, grenade.GrenadeCount) : 1,
                    Math.Max(0, weapon.Clip1),
                    Math.Max(0, weapon.ReserveAmmo.FirstOrDefault())))
                .OrderBy(item => item.Category)
                .ThenBy(item => item.Name)
                .ToArray();
            var money = player.InGameMoneyServices?.Account ?? 0;
            var armor = Math.Max(0, pawn.ArmorValue);
            var hasHelmet = pawn.ItemServices?.HasHelmet ?? player.PawnHasHelmet;
            var hasDefuser = pawn.ItemServices?.HasDefuser ?? player.PawnHasDefuser;
            var currentEquipmentValue = pawn.CurrentEquipmentValue;
            var roundStartEquipmentValue = pawn.RoundStartEquipmentValue;
            var cashSpentThisRound = player.InGameMoneyServices?.CashSpentThisRound ?? 0;
            var playerId = player.SteamID.ToString();
            var signature = string.Join('|', new[]
            {
                money.ToString(), armor.ToString(), hasHelmet.ToString(), hasDefuser.ToString(),
                currentEquipmentValue.ToString(), roundStartEquipmentValue.ToString(), cashSpentThisRound.ToString(),
                string.Join(',', items.Select(item => $"{item.Name}:{item.Count}:{item.ClipAmmo}:{item.ReserveAmmo}"))
            });
            if (lastPlayerEquipment.TryGetValue(playerId, out var previous) && previous == signature)
                return;

            lastPlayerEquipment[playerId] = signature;
            playerEquipmentStates.Add(new PlayerEquipmentState(
                tick,
                tick / (double)TickRate,
                playerId,
                money,
                armor,
                hasHelmet,
                hasDefuser,
                currentEquipmentValue,
                roundStartEquipmentValue,
                cashSpentThisRound,
                items));
        }

        RoundSnapshot CaptureRound(int tick, string bombState)
        {
            var rules = demo.GameRules;
            var rulesRoundNumber = rules.TotalRoundsPlayed + (roundEnded ? 0 : 1);
            var number = Math.Max(Math.Max(1, roundCounter), rulesRoundNumber);
            var elapsed = Math.Max(0, (tick - currentRoundStartTick) / (double)TickRate);
            var roundDuration = rules.RoundTime > 0 ? rules.RoundTime : 115;
            var liveElapsed = currentLiveStartTick > 0
                ? Math.Max(0, (tick - currentLiveStartTick) / (double)TickRate)
                : 0;
            var phase = rules.WarmupPeriod ? "warmup"
                : rules.TeamIntroPeriod ? "team-intro"
                : roundEnded ? "ended"
                : bombState is "planted" or "defusing" ? "post-plant"
                : rules.FreezePeriod || currentLiveStartTick == 0 ? "freeze"
                : "live";
            var remaining = phase == "freeze"
                ? roundDuration
                : Math.Max(0, roundDuration - liveElapsed);
            return new RoundSnapshot(
                number,
                phase,
                demo.TeamTerrorist.Score,
                demo.TeamCounterTerrorist.Score,
                elapsed,
                remaining,
                rules.NumConsecutiveTerroristLoses,
                rules.NumConsecutiveCTLoses);
        }

        BombSnapshot CaptureBomb(IReadOnlyList<PlayerSnapshot> players)
        {
            var planted = demo.Entities.OfType<CPlantedC4>().FirstOrDefault();
            if (planted is not null)
            {
                var site = BombSiteName(planted.BombSite);
                var defuser = FindPlayer(planted.BombDefuser);
                var state = planted.BombDefused ? "defused"
                    : planted.HasExploded ? "exploded"
                    : planted.BeingDefused ? "defusing"
                    : "planted";
                bombTerminalState = state;
                lastBombSnapshot = new BombSnapshot(
                    state,
                    null,
                    defuser?.SteamID.ToString(),
                    site,
                    site is null ? NearestRegion(players, planted.Origin.X, planted.Origin.Y) : $"Bombsite{site}",
                    planted.Origin.X,
                    planted.Origin.Y,
                    planted.Origin.Z,
                    state is "planted" or "defusing"
                        ? Math.Max(0, planted.C4Blow.Value - demo.CurrentGameTime.Value)
                        : null,
                    state == "defusing"
                        ? Math.Max(0, planted.DefuseCountDown.Value - demo.CurrentGameTime.Value)
                        : null);
                return lastBombSnapshot;
            }

            var c4 = demo.Entities.OfType<CC4>().FirstOrDefault();
            var carrier = demo.Players.FirstOrDefault(player =>
                player.PlayerPawn?.Weapons.OfType<CC4>().Any() == true);
            if (c4 is not null || carrier?.PlayerPawn is not null)
            {
                var pawn = carrier?.PlayerPawn;
                var x = pawn?.Origin.X ?? c4!.Origin.X;
                var y = pawn?.Origin.Y ?? c4!.Origin.Y;
                var z = pawn?.Origin.Z ?? c4!.Origin.Z;
                var state = c4?.StartedArming == true ? "planting" : carrier is not null ? "carried" : "dropped";
                lastBombSnapshot = new BombSnapshot(
                    state,
                    carrier?.SteamID.ToString(),
                    null,
                    null,
                    pawn is null ? NearestRegion(players, x, y) : NormalizeRegion(pawn.LastPlaceName),
                    x,
                    y,
                    z,
                    null,
                    null);
                return lastBombSnapshot;
            }

            if (bombTerminalState is "defused" or "exploded" && lastBombSnapshot is not null)
                return lastBombSnapshot with { State = bombTerminalState };

            return new BombSnapshot("unavailable", null, null, null, null, null, null, null, null, null);
        }

        static IReadOnlyList<MapZoneOccupancy> CaptureZones(IReadOnlyList<PlayerSnapshot> players) => players
            .Where(player => player.Team is "T" or "CT")
            .GroupBy(player => player.Region)
            .Select(group => new MapZoneOccupancy(
                group.Key,
                group.Count(player => player.Team == "T" && player.Alive),
                group.Count(player => player.Team == "CT" && player.Alive),
                group.Count(player => player.Team == "T"),
                group.Count(player => player.Team == "CT")))
            .OrderBy(zone => zone.Region)
            .ToArray();

        void CaptureUtilities(int tick)
        {
            var projectiles = demo.Entities.OfType<CBaseCSGrenadeProjectile>().ToArray();
            var presentProjectileIds = projectiles
                .Where(item => item.EntityIndex.IsValid)
                .Select(item => item.EntityIndex.Value)
                .ToHashSet();
            detonatedProjectiles.IntersectWith(presentProjectileIds);

            var seenProjectiles = new HashSet<uint>();
            foreach (var projectile in projectiles)
            {
                var type = UtilityTypeFromProjectile(projectile);
                if (type is null || !projectile.EntityIndex.IsValid ||
                    detonatedProjectiles.Contains(projectile.EntityIndex.Value) ||
                    projectile is CSmokeGrenadeProjectile { DidSmokeEffect: true })
                    continue;

                var entityIndex = projectile.EntityIndex.Value;
                seenProjectiles.Add(entityIndex);
                if (!activeProjectiles.TryGetValue(entityIndex, out var track) || track.Type != type)
                {
                    if (track is not null)
                        track.EndTick = tick;

                    var thrower = FindPlayer(projectile.Thrower);
                    track = new UtilityTrackBuilder(
                        $"projectile-{entityIndex}-{tick}",
                        type,
                        thrower?.SteamID.ToString(),
                        thrower?.PlayerName,
                        TeamName(thrower?.CSTeamNum ?? projectile.CSTeamNum),
                        tick);
                    activeProjectiles[entityIndex] = track;
                    utilityTrackBuilders.Add(track);
                }

                track.AddPoint(tick, projectile.Origin.X, projectile.Origin.Y, projectile.Origin.Z);
            }
            FinalizeMissing(activeProjectiles, seenProjectiles, tick);

            var seenEffects = new HashSet<string>();
            foreach (var smoke in demo.Entities.OfType<CSmokeGrenadeProjectile>().Where(item => item.DidSmokeEffect))
            {
                if (!smoke.EntityIndex.IsValid)
                    continue;

                var entityIndex = smoke.EntityIndex.Value;
                var key = $"smoke-{entityIndex}";
                seenEffects.Add(key);
                if (!activeEffects.TryGetValue(key, out var effect))
                {
                    activeProjectiles.TryGetValue(entityIndex, out var projectileTrack);
                    var thrower = FindPlayer(smoke.Thrower);
                    effect = new UtilityEffectBuilder(
                        $"{key}-{tick}",
                        "smoke",
                        projectileTrack?.ThrowerId ?? thrower?.SteamID.ToString(),
                        projectileTrack?.ThrowerName ?? thrower?.PlayerName,
                        projectileTrack?.Team ?? TeamName(thrower?.CSTeamNum ?? smoke.CSTeamNum),
                        tick);
                    activeEffects[key] = effect;
                    utilityEffectBuilders.Add(effect);
                    if (projectileTrack is not null)
                        projectileTrack.DetonateTick = effect.StartTick;
                }

                var position = smoke.SmokeDetonationPos;
                effect.AddSample(tick, position.X, position.Y, position.Z, 144, []);
            }

            foreach (var inferno in demo.Entities.OfType<CInferno>())
            {
                if (!inferno.EntityIndex.IsValid || inferno.FireCount <= 0)
                    continue;

                var entityIndex = inferno.EntityIndex.Value;
                var key = $"fire-{entityIndex}";
                seenEffects.Add(key);
                if (!activeEffects.TryGetValue(key, out var effect))
                {
                    var relatedTrack = FindRelatedFireTrack(inferno.Origin.X, inferno.Origin.Y, tick);
                    effect = new UtilityEffectBuilder(
                        $"{key}-{tick}",
                        "fire",
                        relatedTrack?.ThrowerId,
                        relatedTrack?.ThrowerName,
                        relatedTrack?.Team ?? TeamName(inferno.CSTeamNum),
                        tick);
                    activeEffects[key] = effect;
                    utilityEffectBuilders.Add(effect);
                    if (relatedTrack is not null)
                        relatedTrack.DetonateTick = effect.StartTick;
                }

                var area = ActiveFirePositions(inferno);
                effect.AddSample(tick, inferno.Origin.X, inferno.Origin.Y, inferno.Origin.Z, 48, area);
            }
            FinalizeMissing(activeEffects, seenEffects, tick);
        }

        CCSPlayerController? FindPlayer(CCSPlayerPawn? pawn)
        {
            if (pawn is null || !pawn.EntityIndex.IsValid)
                return null;

            var entityIndex = pawn.EntityIndex.Value;
            return demo.Players.FirstOrDefault(player => player.PlayerPawn?.EntityIndex.Value == entityIndex);
        }

        UtilityTrackBuilder? FindRelatedFireTrack(float x, float y, int tick) => utilityTrackBuilders
            .Where(item => item.Type == "fire" && item.Points.Count > 0 && item.EndTick >= tick - TickRate)
            .OrderBy(item => DistanceSquared(item.Points[^1].X, item.Points[^1].Y, x, y))
            .FirstOrDefault();

        void DetonateProjectile(int entityId)
        {
            if (entityId < 0)
                return;

            var key = (uint)entityId;
            var tick = Math.Max(0, demo.CurrentDemoTick.Value);
            detonatedProjectiles.Add(key);
            if (!activeProjectiles.Remove(key, out var track))
                return;

            track.DetonateTick = tick;
            track.EndTick = tick;
        }
    }

    private static IReadOnlyList<UtilityAreaPoint> ActiveFirePositions(CInferno inferno)
    {
        var result = new List<UtilityAreaPoint>();
        var count = Math.Min(inferno.FirePositions.Length, inferno.FireIsBurning.Length);
        for (var index = 0; index < count; index++)
        {
            if (!inferno.FireIsBurning[index])
                continue;

            var position = inferno.FirePositions[index];
            result.Add(new UtilityAreaPoint(position.X, position.Y, position.Z));
        }
        return result;
    }

    private static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        var x = x1 - x2;
        var y = y1 - y2;
        return x * x + y * y;
    }

    private static string NormalizeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "unknown";

        return region.Trim() switch
        {
            "BombsiteA" => "A Site",
            "BombsiteB" => "B Site",
            "TSpawn" => "T Spawn",
            "CTSpawn" => "CT Spawn",
            var value => value
        };
    }

    private static string NearestRegion(IReadOnlyList<PlayerSnapshot> players, float x, float y) => players
        .Where(player => player.Region != "unknown")
        .OrderBy(player => DistanceSquared(player.X, player.Y, x, y))
        .Select(player => player.Region)
        .FirstOrDefault() ?? "unknown";

    private static string? BombSiteName(int site) => site switch
    {
        0 => "A",
        1 => "B",
        _ => null
    };

    private static string EquipmentCategory(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon))
            return "unknown";
        if (UtilityTypeFromWeapon(weapon) is not null)
            return "grenade";
        if (weapon.Contains("knife", StringComparison.Ordinal) || weapon is "weapon_bayonet")
            return "knife";
        if (weapon is "weapon_c4")
            return "objective";
        if (weapon is "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_p250" or
            "weapon_deagle" or "weapon_elite" or "weapon_fiveseven" or "weapon_tec9" or
            "weapon_cz75a" or "weapon_revolver")
            return "pistol";
        if (weapon is "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or
            "weapon_ump45" or "weapon_p90" or "weapon_bizon")
            return "smg";
        if (weapon is "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff")
            return "shotgun";
        if (weapon is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1")
            return "sniper";
        if (weapon is "weapon_m249" or "weapon_negev")
            return "heavy";
        if (weapon is "weapon_ak47" or "weapon_galilar" or "weapon_famas" or "weapon_m4a1" or
            "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556")
            return "rifle";
        return "other";
    }

    private static string? UtilityTypeFromProjectile(CBaseCSGrenadeProjectile projectile) => projectile switch
    {
        CSmokeGrenadeProjectile => "smoke",
        CFlashbangProjectile => "flash",
        CHEGrenadeProjectile => "he",
        CMolotovProjectile => "fire",
        CDecoyProjectile => "decoy",
        _ => null
    };

    private static string? UtilityTypeFromWeapon(string? weapon) => weapon switch
    {
        "weapon_smokegrenade" => "smoke",
        "weapon_flashbang" => "flash",
        "weapon_hegrenade" => "he",
        "weapon_molotov" => "molotov",
        "weapon_incgrenade" => "incendiary",
        "weapon_decoy" => "decoy",
        _ => null
    };

    private static string TeamName(CSTeamNumber team) => team switch
    {
        CSTeamNumber.Terrorist => "T",
        CSTeamNumber.CounterTerrorist => "CT",
        _ => "SPEC"
    };

    private static void FinalizeMissing<TBuilder>(
        Dictionary<uint, TBuilder> active,
        IReadOnlySet<uint> seen,
        int tick) where TBuilder : IActiveTrack
    {
        foreach (var key in active.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            active[key].EndTick = tick;
            active.Remove(key);
        }
    }

    private static void FinalizeMissing<TBuilder>(
        Dictionary<string, TBuilder> active,
        IReadOnlySet<string> seen,
        int tick) where TBuilder : IActiveTrack
    {
        foreach (var key in active.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            active[key].EndTick = tick;
            active.Remove(key);
        }
    }

    private interface IActiveTrack
    {
        int EndTick { get; set; }
    }

    private sealed class UtilityTrackBuilder(
        string id,
        string type,
        string? throwerId,
        string? throwerName,
        string team,
        int startTick) : IActiveTrack
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public string? ThrowerId { get; } = throwerId;
        public string? ThrowerName { get; } = throwerName;
        public string Team { get; } = team;
        public int StartTick { get; } = startTick;
        public int EndTick { get; set; } = int.MaxValue;
        public int? DetonateTick { get; set; }
        public List<UtilityPoint> Points { get; } = [];

        public void AddPoint(int tick, float x, float y, float z)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || Points.LastOrDefault()?.Tick == tick)
                return;
            var previous = Points.LastOrDefault();
            if (previous is not null && previous.X == x && previous.Y == y && previous.Z == z)
                return;
            Points.Add(new UtilityPoint(tick, tick / (double)TickRate, x, y, z));
        }

        public UtilityTrack Build() => new(
            Id,
            Type,
            ThrowerId,
            ThrowerName,
            Team,
            StartTick,
            EndTick,
            DetonateTick ?? EndTick,
            Points);
    }

    private sealed class UtilityEffectBuilder(
        string id,
        string type,
        string? throwerId,
        string? throwerName,
        string team,
        int startTick) : IActiveTrack
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public string? ThrowerId { get; } = throwerId;
        public string? ThrowerName { get; } = throwerName;
        public string Team { get; } = team;
        public int StartTick { get; } = startTick;
        public int EndTick { get; set; } = int.MaxValue;
        public List<UtilityEffectSample> Samples { get; } = [];

        public void AddSample(
            int tick,
            float x,
            float y,
            float z,
            float radius,
            IReadOnlyList<UtilityAreaPoint> area)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return;

            var previous = Samples.LastOrDefault();
            if (previous is not null && SameEffect(previous, x, y, z, radius, area))
                return;

            Samples.Add(new UtilityEffectSample(tick, tick / (double)TickRate, x, y, z, radius, area));
        }

        public UtilityEffectTrack Build() => new(
            Id,
            Type,
            ThrowerId,
            ThrowerName,
            Team,
            StartTick,
            EndTick,
            Samples);

        private static bool SameEffect(
            UtilityEffectSample previous,
            float x,
            float y,
            float z,
            float radius,
            IReadOnlyList<UtilityAreaPoint> area)
        {
            if (previous.X != x || previous.Y != y || previous.Z != z || previous.Radius != radius || previous.Area.Count != area.Count)
                return false;

            for (var index = 0; index < area.Count; index++)
            {
                if (previous.Area[index] != area[index])
                    return false;
            }
            return true;
        }
    }
}
