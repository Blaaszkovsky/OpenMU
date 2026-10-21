// <copyright file="FereaEncounter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

using System.Collections.Concurrent;
using System.Threading;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Pathfinding;
using Nito.AsyncEx;

/// <summary>
/// Owns one Ferea arena and its monsters on one game server.
/// </summary>
/// <remarks>
/// Tick and teardown share one non-reentrant async lock. Hit filtering never acquires it;
/// the NPC hit lock serializes health gates, and pending phase protection is published before the hit returns.
/// Player membership is revoked on death, movement out of the arena, and map removal.
/// </remarks>
public sealed class FereaEncounter
{
    private readonly GameContext _gameContext;
    private readonly TimeProvider _timeProvider;
    private readonly AsyncLock _tickLock = new();
    private readonly ConcurrentDictionary<Player, byte> _participants = new();
    private readonly List<Monster> _ownedMonsters = new();
    private readonly List<Monster> _guardians = new();
    private readonly List<(Point Position, DateTimeOffset Due)> _mines = new();
    private GameMap? _map;
    private Monster? _orb;
    private Monster? _lord;
    private Monster? _general;
    private DateTimeOffset _deadline;
    private DateTimeOffset _battleDeadline;
    private DateTimeOffset? _emptySince;
    private DateTimeOffset _nextRegeneration;
    private DateTimeOffset _nextBrandish;
    private DateTimeOffset _nextMines;
    private int _phase;
    private int _phasePending;
    private int _roarVictims;
    private bool _disposed;
    private volatile FereaEncounterState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="FereaEncounter"/> class.
    /// </summary>
    /// <param name="gameContext">The game server.</param>
    /// <param name="configuration">The configuration, kept for this encounter lifetime.</param>
    /// <param name="timeProvider">The clock used for deadlines.</param>
    public FereaEncounter(GameContext gameContext, FereaEncounterConfiguration configuration, TimeProvider? timeProvider = null)
    {
        configuration.Validate();
        this._gameContext = gameContext;
        this.Configuration = configuration;
        this._timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the rules of this encounter.</summary>
    public FereaEncounterConfiguration Configuration { get; }

    /// <summary>Gets the current stage.</summary>
    public FereaEncounterState State => this._state;

    /// <summary>Gets the number of completed health phases.</summary>
    public int Phase => Volatile.Read(ref this._phase);

    /// <summary>Gets the living Lord, if spawned.</summary>
    public Monster? Lord => this._lord;

    /// <summary>Gets the summoned General, if spawned.</summary>
    public Monster? General => this._general;

    /// <summary>
    /// Executes one serialized encounter update.
    /// </summary>
    public async ValueTask TickAsync()
    {
        using var tickLock = await this._tickLock.LockAsync().ConfigureAwait(false);
        if (this._disposed)
        {
            return;
        }

        this._map ??= await this._gameContext.GetMapAsync((ushort)this.Configuration.MapNumber).ConfigureAwait(false);
        if (this._map is not { IsTornDown: false } map)
        {
            return;
        }

        var now = this._timeProvider.GetUtcNow();
        try
        {
            if (this.State == FereaEncounterState.Waiting || (this.State == FereaEncounterState.Cooldown && now >= this._deadline))
            {
                await this.OpenAsync().ConfigureAwait(false);
            }

            var players = map.GetPlayers();
            if (this.State == FereaEncounterState.Orb)
            {
                foreach (var player in players.Where(p => IsLiving(p)
                    && p.Position.X >= this.Configuration.EntranceX1 && p.Position.X <= this.Configuration.EntranceX2
                    && p.Position.Y >= this.Configuration.EntranceY1 && p.Position.Y <= this.Configuration.EntranceY2))
                {
                    await player.MoveAsync(new Point(this.Configuration.ArrivalX, this.Configuration.ArrivalY)).ConfigureAwait(false);
                }
            }

            foreach (var participant in this._participants.Keys)
            {
                if (!IsLiving(participant) || !players.Contains(participant) || !this.Configuration.Contains(participant.Position))
                {
                    this._participants.TryRemove(participant, out _);
                }
            }

            if (this.State is FereaEncounterState.Summoning or FereaEncounterState.Battle or FereaEncounterState.General or FereaEncounterState.Cooldown)
            {
                foreach (var player in players.Where(p => this.Configuration.Contains(p.Position) && !this._participants.ContainsKey(p)))
                {
                    await this.EjectAsync(player).ConfigureAwait(false);
                }
            }

            if (this.State == FereaEncounterState.Summoning)
            {
                this._lord = await this.SpawnSingleAsync(this.Configuration.LordWave).ConfigureAwait(false);
                this._state = FereaEncounterState.Battle;
                this._battleDeadline = now + this.Configuration.BattleTimeout;
                this._nextBrandish = now + this.Configuration.BrandishInterval;
                this._nextMines = now + this.Configuration.MineInterval;
                await this.NotifyAsync("FereaEncounterStarted").ConfigureAwait(false);
            }

            if (this.State is not (FereaEncounterState.Battle or FereaEncounterState.General))
            {
                return;
            }

            if (this._lord is not { IsAlive: true } lord)
            {
                await this.EndAsync(true, now).ConfigureAwait(false);
                return;
            }

            this._emptySince = this._participants.IsEmpty ? this._emptySince ?? now : null;
            if ((this._emptySince is { } empty && now - empty >= this.Configuration.AbandonmentTimeout) || now >= this._battleDeadline)
            {
                await this.EndAsync(false, now).ConfigureAwait(false);
                return;
            }

            foreach (var monster in this._ownedMonsters.Where(m => m.IsAlive && !this.Configuration.Contains(m.Position)))
            {
                await monster.TeleportAsync(new Point(monster.SpawnArea.X1, monster.SpawnArea.Y1)).ConfigureAwait(false);
            }

            if (Volatile.Read(ref this._phasePending) != 0 && this.State == FereaEncounterState.Battle)
            {
                await this.StartGeneralPhaseAsync(lord, now).ConfigureAwait(false);
            }

            if (this.State == FereaEncounterState.General)
            {
                if (this._general is not { IsAlive: true })
                {
                    this._general = null;
                    this._state = FereaEncounterState.Battle;
                    Volatile.Write(ref this._phasePending, 0);
                    await this.NotifyAsync("FereaGeneralDefeated").ConfigureAwait(false);
                }
                else if (now >= this._nextRegeneration)
                {
                    var amount = (double)lord.Attributes[Stats.MaximumHealth] * this.Configuration.RegenerationPercentPerPlayer / 100 * Math.Max(1, this._roarVictims);
                    lord.RecoverHealth((int)Math.Min(int.MaxValue, amount));
                    this._nextRegeneration = now + this.Configuration.RegenerationInterval;
                }

                return;
            }

            await this.AttackAreaAsync(lord, now).ConfigureAwait(false);
        }
        catch
        {
            // A failed spawn or view notification must never leave half an encounter active.
            await this.EndAsync(false, now).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Determines whether an incoming gate may place this player inside the arena.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="gate">The destination gate.</param>
    /// <returns>Whether entry is open or the gate leads elsewhere.</returns>
    public bool CanEnter(Player player, ExitGate gate)
    {
        if (gate.Map?.Number != this.Configuration.MapNumber
            || gate.X2 < this.Configuration.ArenaX1 || gate.X1 > this.Configuration.ArenaX2
            || gate.Y2 < this.Configuration.ArenaY1 || gate.Y1 > this.Configuration.ArenaY2)
        {
            return true;
        }

        return this.State == FereaEncounterState.Orb && IsLiving(player);
    }

    /// <summary>
    /// Revokes participation as soon as the player dies or leaves the arena.
    /// </summary>
    /// <param name="player">The departing participant.</param>
    public void Revoke(Player player) => this._participants.TryRemove(player, out _);

    /// <summary>
    /// Removes encounter objects on plugin shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        using var tickLock = await this._tickLock.LockAsync().ConfigureAwait(false);
        this._disposed = true;
        await this.RemoveMonstersAsync().ConfigureAwait(false);
        this._participants.Clear();
    }

    /// <summary>
    /// Filters all damage paths, including poison, bleed, reflection and lethal hits across phase boundaries.
    /// </summary>
    /// <param name="monster">The encounter monster.</param>
    /// <param name="hit">The incoming hit.</param>
    /// <param name="attacker">The source.</param>
    /// <returns>The effective hit.</returns>
    internal HitInfo FilterHit(Monster monster, HitInfo hit, IAttacker attacker)
    {
        var player = attacker switch
        {
            Player direct => direct,
            IPlayerSurrogate surrogate => surrogate.Owner,
            Monster { SummonedBy: { } owner } => owner,
            _ => null,
        };
        if (player is null || !IsLiving(player) || player.CurrentMap != this._map
            || !this.Configuration.Contains(player.Position) || !this.Configuration.Contains(monster.Position)
            || this.IsProtected(monster)
            || (this.State is FereaEncounterState.Battle or FereaEncounterState.General && !this._participants.ContainsKey(player)))
        {
            return hit with { HealthDamage = 0, ShieldDamage = 0 };
        }

        if (monster == this._lord && this.Phase < this.Configuration.PhaseHealthPercentages.Count)
        {
            var floor = Math.Max(1, (int)(monster.Attributes[Stats.MaximumHealth] * this.Configuration.PhaseHealthPercentages[this.Phase] / 100));
            var allowed = (uint)Math.Max(0, monster.Health - floor);
            if (hit.TotalDamage >= allowed)
            {
                // The health gate is armed under the NPC hit lock before another attacker can bypass it.
                Volatile.Write(ref this._phasePending, 1);
                return hit with { HealthDamage = Math.Min(hit.TotalDamage, allowed), ShieldDamage = 0 };
            }
        }

        return hit;
    }

    /// <summary>
    /// Determines whether a monster is protected or passive in the current stage.
    /// </summary>
    /// <param name="monster">The monster.</param>
    /// <returns>Whether damage and normal attacks are blocked.</returns>
    internal bool IsProtected(Monster monster) =>
        (monster == this._orb && this._guardians.Any(g => g.IsAlive))
        || (monster == this._lord && (this.State == FereaEncounterState.General || Volatile.Read(ref this._phasePending) != 0));

    private static bool IsLiving(Player player) => player.IsAlive && player.PlayerState.CurrentState == PlayerState.EnteredWorld;

    private IEnumerable<MonsterSpawnArea> GetWave(byte wave) =>
        this._map?.Definition.MonsterSpawns.Where(s => s.SpawnTrigger == SpawnTrigger.OnceAtWaveStart && s.WaveNumber == wave) ?? [];

    private async ValueTask OpenAsync()
    {
        await this.RemoveMonstersAsync().ConfigureAwait(false);
        this.ValidateSpawns();
        this._participants.Clear();
        this._phase = 0;
        this._phasePending = 0;
        this._emptySince = null;
        this._guardians.AddRange(await this.SpawnWaveAsync(this.Configuration.GuardianWave).ConfigureAwait(false));
        this._orb = await this.SpawnSingleAsync(this.Configuration.OrbWave).ConfigureAwait(false);
        this._orb.Died += this.OnOrbDied;
        this._state = FereaEncounterState.Orb;
    }

    private void OnOrbDied(object? sender, DeathInformation death)
    {
        if (this.State != FereaEncounterState.Orb || sender != this._orb || this._map is not { } map)
        {
            return;
        }

        foreach (var player in map.GetPlayers().Where(p => IsLiving(p) && this.Configuration.Contains(p.Position)))
        {
            this._participants.TryAdd(player, 0);
        }

        this._state = FereaEncounterState.Summoning;
    }

    private void ValidateSpawns()
    {
        foreach (var wave in new[] { this.Configuration.OrbWave, this.Configuration.LordWave, this.Configuration.GeneralWave })
        {
            if (this.GetWave(wave).Sum(s => s.Quantity) != 1)
            {
                throw new InvalidOperationException($"Ferea wave {wave} must contain exactly one monster.");
            }
        }

        if (!this.GetWave(this.Configuration.GuardianWave).Any())
        {
            throw new InvalidOperationException("Ferea requires Orb guardians.");
        }

        foreach (var spawn in new[] { this.Configuration.OrbWave, this.Configuration.GuardianWave, this.Configuration.LordWave, this.Configuration.GeneralWave, this.Configuration.ReinforcementWave }.SelectMany(this.GetWave))
        {
            if (spawn.MonsterDefinition is null || spawn.Quantity <= 0 || spawn.X1 != spawn.X2 || spawn.Y1 != spawn.Y2
                || !this.Configuration.Contains(new Point(spawn.X1, spawn.Y1))
                || this._map!.Terrain.AIgrid[spawn.X1, spawn.Y1] != 1)
            {
                throw new InvalidOperationException("Ferea spawns must be walkable points inside the arena.");
            }
        }

        if (!this._map!.Terrain.WalkMap[this.Configuration.EjectionX, this.Configuration.EjectionY]
            || !this._map.Terrain.WalkMap[this.Configuration.ArrivalX, this.Configuration.ArrivalY])
        {
            throw new InvalidOperationException("Ferea ejection position is not walkable.");
        }
    }

    private async ValueTask<Monster> SpawnSingleAsync(byte wave) => (await this.SpawnWaveAsync(wave).ConfigureAwait(false)).Single();

    private async ValueTask<List<Monster>> SpawnWaveAsync(byte wave)
    {
        var monsters = new List<Monster>();
        foreach (var spawn in this.GetWave(wave))
        {
            for (var i = 0; i < spawn.Quantity; i++)
            {
                var monster = new Monster(
                    spawn,
                    spawn.MonsterDefinition!,
                    this._map!,
                    NullDropGenerator.Instance,
                    new FereaIntelligence(this, wave == this.Configuration.OrbWave),
                    this._gameContext.PlugInManager,
                    this._gameContext.PathFinderPool);
                this._ownedMonsters.Add(monster);
                monster.Initialize();
                await this._map!.AddAsync(monster).ConfigureAwait(false);
                monster.OnSpawn();
                monsters.Add(monster);
            }
        }

        return monsters;
    }

    private async ValueTask StartGeneralPhaseAsync(Monster lord, DateTimeOffset now)
    {
        this._general = await this.SpawnSingleAsync(this.Configuration.GeneralWave).ConfigureAwait(false);
        this._state = FereaEncounterState.General;
        this._phase++;
        var victims = this._participants.Keys.Where(p => IsLiving(p) && p.GetDistanceTo(lord) <= this.Configuration.RoarRadius).ToArray();
        this._roarVictims = victims.Length;
        foreach (var player in victims)
        {
            if (player.Attributes is { } attributes)
            {
                attributes[Stats.CurrentMana] = 0;
                attributes[Stats.CurrentAbility] = 0;
                await player.InvokeViewPlugInAsync<IUpdateStatsPlugIn>(p => p.UpdateStatsAsync(Stats.CurrentMana, 0)).ConfigureAwait(false);
                await player.InvokeViewPlugInAsync<IUpdateStatsPlugIn>(p => p.UpdateStatsAsync(Stats.CurrentAbility, 0)).ConfigureAwait(false);
            }
        }

        await lord.TeleportAsync(new Point(lord.SpawnArea.X1, lord.SpawnArea.Y1)).ConfigureAwait(false);
        await this.SpawnWaveAsync(this.Configuration.ReinforcementWave).ConfigureAwait(false);
        this._nextRegeneration = now + this.Configuration.RegenerationInterval;
        this._mines.Clear();
        await this.NotifyAsync("FereaGeneralSummoned").ConfigureAwait(false);
    }

    private async ValueTask AttackAreaAsync(Monster lord, DateTimeOffset now)
    {
        var targets = this._participants.Keys.Where(IsLiving).ToArray();
        foreach (var mine in this._mines.Where(m => now >= m.Due).ToArray())
        {
            foreach (var player in targets.Where(p => p.Position.EuclideanDistanceTo(mine.Position) <= this.Configuration.MineRadius))
            {
                await player.AttackByAsync(lord, null, false).ConfigureAwait(false);
            }

            this._mines.Remove(mine);
        }

        if (now >= this._nextBrandish)
        {
            foreach (var player in targets.Where(p => p.GetDistanceTo(lord) <= this.Configuration.BrandishRadius && lord.HasLineOfSightTo(p)))
            {
                await lord.AttackAsync(player).ConfigureAwait(false);
            }

            this._nextBrandish = now + this.Configuration.BrandishInterval;
        }

        if (now >= this._nextMines)
        {
            foreach (var target in targets.Take(Math.Min(this.Configuration.MaximumMines, this.Phase + 1)))
            {
                this._mines.Add((target.Position, now + this.Configuration.MineDelay));
            }

            if (targets.Length > 0)
            {
                await this.NotifyAsync("FereaMineWarning").ConfigureAwait(false);
            }

            this._nextMines = now + this.Configuration.MineInterval;
        }
    }

    private async ValueTask EndAsync(bool victory, DateTimeOffset now)
    {
        this._state = FereaEncounterState.Cooldown;
        var minimum = this.Configuration.VictoryCooldownMinimum.TotalSeconds;
        var maximum = this.Configuration.VictoryCooldownMaximum.TotalSeconds;
        this._deadline = now + (victory ? TimeSpan.FromSeconds(minimum + (Random.Shared.NextDouble() * (maximum - minimum))) : this.Configuration.FailureCooldown);
        await this.RemoveMonstersAsync().ConfigureAwait(false);
        await this.NotifyAsync(victory ? "FereaEncounterVictory" : "FereaEncounterReset").ConfigureAwait(false);
        // Keep winners registered through cooldown so the late-entry guard does not remove them on the next tick.
        if (!victory)
        {
            this._participants.Clear();
        }

        if (this._map is { } map)
        {
            foreach (var player in map.GetPlayers().Where(p => this.Configuration.Contains(p.Position)
                && (!victory || !this._participants.ContainsKey(p))))
            {
                await this.EjectAsync(player).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask EjectAsync(Player player)
    {
        this.Revoke(player);
        if (player.CurrentMap == this._map)
        {
            await player.MoveAsync(new Point(this.Configuration.EjectionX, this.Configuration.EjectionY)).ConfigureAwait(false);
        }
    }

    private async ValueTask RemoveMonstersAsync()
    {
        if (this._orb is { } orb)
        {
            orb.Died -= this.OnOrbDied;
        }

        foreach (var monster in this._ownedMonsters)
        {
            if (!monster.IsDisposed)
            {
                if (monster.CurrentMap.GetObject(monster.Id) == monster)
                {
                    await monster.CurrentMap.RemoveAsync(monster).ConfigureAwait(false);
                }

                monster.Dispose();
            }
        }

        this._ownedMonsters.Clear();
        this._guardians.Clear();
        this._mines.Clear();
        this._orb = null;
        this._lord = null;
        this._general = null;
    }

    private async ValueTask NotifyAsync(string key)
    {
        foreach (var player in this._participants.Keys)
        {
            try
            {
                await player.ShowLocalizedBlueMessageAsync(key).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this._gameContext.LoggerFactory.CreateLogger<FereaEncounter>().LogWarning(exception, "Could not notify a Ferea participant.");
            }
        }
    }
}
