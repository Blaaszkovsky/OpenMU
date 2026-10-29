// <copyright file="CastleSiegeContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.CastleSiege;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.CastleSiege.NPC;
using MUnique.OpenMU.GameLogic.Views.CastleSiege;
using MUnique.OpenMU.Persistence;
using Nito.AsyncEx;

/// <summary>
/// Holds the runtime state of Castle Siege for one game context.
/// </summary>
/// <remarks>
/// Runtime objects and <see cref="ExecutionLock"/> are scoped to this game context; persistence operations are
/// coordinated across game contexts sharing the same provider and persistent siege identifier.
/// </remarks>
public class CastleSiegeContext : IEventStateProvider
{
    private static readonly TimeSpan JoinSideEffectDuration = TimeSpan.FromDays(7);
    private static readonly ConditionalWeakTable<IPersistenceContextProvider, PersistenceCoordinator> PersistenceCoordinators = new();

    private readonly IGameContext _gameContext;
    private readonly TimeProvider _timeProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Player, CastleSiegeJoinSide> _notifiedPlayerJoinSides = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Player, byte> _siegeMapPlayers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, CastleSiegeLifeStone> _lifeStones = new();
    private int _requestedState = -1;
    private CastleSiegeData _savedState = new RuntimeSiegeData();

    /// <summary>
    /// Initializes a new instance of the <see cref="CastleSiegeContext"/> class.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="configuration">The Castle Siege configuration.</param>
    /// <param name="timeProvider">The time provider used for runtime Castle Siege objects.</param>
    public CastleSiegeContext(IGameContext gameContext, CastleSiegeConfiguration configuration, TimeProvider? timeProvider = null)
    {
        this._gameContext = gameContext;
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this.Configuration = configuration;
        this.Schedule = new CastleSiegeSchedule(configuration.StateSchedule, gameContext.ServerTimeZone);
        this.NpcController = new CastleSiegeNpcController(this);
    }

    /// <summary>
    /// Gets the current state.
    /// </summary>
    public CastleSiegeState CurrentState { get; internal set; }

    /// <summary>
    /// Gets the UTC time at which the current state started.
    /// </summary>
    public DateTime StateStartTimeUtc { get; internal set; }

    /// <summary>
    /// Gets the UTC time at which the current state ends.
    /// </summary>
    public DateTime StateEndTimeUtc { get; internal set; }

    /// <summary>
    /// Gets the Castle Siege configuration.
    /// </summary>
    public CastleSiegeConfiguration Configuration { get; }

    /// <summary>
    /// Gets the persistent Castle Siege state.
    /// </summary>
    public CastleSiegeData SiegeData { get; private set; } = null!;

    /// <summary>
    /// Gets the guild registrations keyed by their persistent guild identifier.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, CastleSiegeGuildRegistration> RegisteredGuilds { get; } = new();

    /// <summary>
    /// Gets the selected guilds keyed by their runtime guild identifier.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<uint, CastleSiegeGuildParticipant> FinalGuildList { get; } = new();

    /// <summary>
    /// Gets the assigned join sides keyed by persistent character identifier.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, CastleSiegeJoinSide> PlayerJoinSides { get; } = new();

    /// <summary>
    /// Gets the participating characters keyed by their persistent character identifier.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, CastleSiegeParticipant> ParticipantTracking { get; } = new();

    /// <summary>
    /// Gets or sets the runtime identifier of the guild which currently owns the castle during the battle.
    /// </summary>
    public uint? MiddleOwnerGuildId { get; set; }

    /// <summary>
    /// Gets the active Castle Siege NPCs.
    /// </summary>
    public List<CastleSiegeNpcRuntime> ActiveNpcs { get; } = new();

    /// <summary>
    /// Gets the controller for Castle Siege NPC lifecycle and lookup operations.
    /// </summary>
    public CastleSiegeNpcController NpcController { get; }

    /// <summary>
    /// Gets or sets the player which currently operates the Crown.
    /// </summary>
    public Player? CrownUser { get; set; }

    /// <summary>
    /// Gets the players which currently operate the Crown switches.
    /// </summary>
    public Player?[] SwitchUsers { get; } = new Player?[2];

    /// <summary>
    /// Gets or sets the accumulated Crown operation time.
    /// </summary>
    public TimeSpan CrownAccumulatedTime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Crown can currently be operated.
    /// </summary>
    public bool IsCrownAvailable { get; set; }

    /// <inheritdoc />
    public bool IsEventRunning => this.CurrentState == CastleSiegeState.Start;

    /// <summary>
    /// Gets the remaining time of the current state.
    /// </summary>
    public TimeSpan RemainingTime => this.GetRemainingTime(this._timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Gets a snapshot of the Life Stones which are currently placed by participating guilds.
    /// </summary>
    internal IReadOnlyCollection<CastleSiegeLifeStone> LifeStones => this._lifeStones.Values.ToArray();

    /// <summary>
    /// Gets or sets the player whose active Crown attempt was announced to the client.
    /// </summary>
    internal Player? PreviousCrownUser { get; set; }

    /// <summary>
    /// Gets or sets the UTC time of the previous Crown progress update.
    /// </summary>
    internal DateTime LastCrownUpdateUtc { get; set; }

    /// <summary>
    /// Gets the switch information which was last broadcast to the siege map, keyed by network object identifier.
    /// </summary>
    internal Dictionary<ushort, CastleSiegeSwitchInfo> LastBroadcastSwitchInfos { get; } = [];

    /// <summary>
    /// Gets or sets the Crown availability which was last broadcast to the siege map.
    /// </summary>
    internal bool? LastBroadcastCrownAvailability { get; set; }

    /// <summary>
    /// Gets a value indicating whether the context has been initialized.
    /// </summary>
    internal bool IsInitialized { get; private set; }

    /// <summary>
    /// Gets the game context.
    /// </summary>
    internal IGameContext GameContext => this._gameContext;

    /// <summary>
    /// Gets the clock used by Castle Siege runtime operations.
    /// </summary>
    internal TimeProvider TimeProvider => this._timeProvider;

    /// <summary>
    /// Gets the configured weekly schedule.
    /// </summary>
    internal CastleSiegeSchedule Schedule { get; }

    /// <summary>
    /// Gets the lock which prevents overlapping timer executions for this context.
    /// </summary>
    internal SemaphoreSlim ExecutionLock { get; } = new(1, 1);

    /// <summary>
    /// Gets or sets the next regular notification time.
    /// </summary>
    internal DateTime NextNotificationUtc { get; set; } = DateTime.MaxValue;

    /// <summary>
    /// Gets or sets the next NPC persistence time.
    /// </summary>
    internal DateTime NextNpcSaveUtc { get; set; } = DateTime.MaxValue;

    /// <summary>
    /// Gets or sets the next economy persistence time.
    /// </summary>
    internal DateTime NextEconomySaveUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Gets or sets a value indicating whether the in-memory economy state needs to be persisted.
    /// </summary>
    internal bool IsEconomyPersistencePending { get; set; }

    /// <summary>
    /// Gets or sets the next participant tracking time.
    /// </summary>
    internal DateTime NextParticipantUpdateUtc { get; set; } = DateTime.MaxValue;

    /// <summary>
    /// Gets or sets the next mini-map broadcast time.
    /// </summary>
    internal DateTime NextMiniMapUpdateUtc { get; set; } = DateTime.MaxValue;

    /// <summary>
    /// Gets the Ready-state countdown values which have already been sent.
    /// </summary>
    internal HashSet<int> SentReadyCountdownMinutes { get; } = new();

    /// <summary>
    /// Gets or sets the last force-state request processed by this context.
    /// </summary>
    internal int LastForceRequestVersion { get; set; }

    /// <inheritdoc />
    public bool IsSpawnWaveActive(byte waveNumber) => false;

    /// <summary>
    /// Gets the side of a player in the current Castle Siege.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The assigned side, or <see cref="CastleSiegeJoinSide.None"/>.</returns>
    public CastleSiegeJoinSide GetPlayerJoinSide(Player player)
    {
        if (this.Configuration.CastleSiegeMapDefinition?.Number != player.CurrentMap?.Definition.Number)
        {
            return CastleSiegeJoinSide.None;
        }

        return this.GetTrackedPlayerJoinSide(player);
    }

    /// <summary>
    /// Assigns the current Castle Siege side to all online players on the Castle Siege map.
    /// </summary>
    /// <returns>A task that represents the asynchronous synchronization operation.</returns>
    public async ValueTask SetPlayerJoinSideAsync()
    {
        if (this.Configuration.CastleSiegeMapDefinition is null)
        {
            return;
        }

        var activeCharacterIds = new HashSet<Guid>();
        var activePlayers = new HashSet<Player>();
        foreach (var player in this.GetSiegePlayers())
        {
            if (player.SelectedCharacter is not { } character)
            {
                continue;
            }

            activeCharacterIds.Add(character.Id);
            activePlayers.Add(player);
            await this.SynchronizePlayerJoinSideAsync(player).ConfigureAwait(false);
        }

        foreach (var player in this._notifiedPlayerJoinSides.Keys.Where(player => !activePlayers.Contains(player)))
        {
            await this.ClearPlayerJoinSideAsync(player).ConfigureAwait(false);
        }

        foreach (var characterId in this.PlayerJoinSides.Keys.Where(id => !activeCharacterIds.Contains(id)))
        {
            this.PlayerJoinSides.TryRemove(characterId, out _);
        }
    }

    /// <summary>
    /// Loads the persistent Castle Siege state and registrations.
    /// </summary>
    public async ValueTask LoadAsync()
    {
        await this.LoadPersistentStateAsync(false).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the persistent Castle Siege owner and economy state.
    /// </summary>
    public async ValueTask SaveOwnerAsync()
    {
        if (this.SiegeData is null)
        {
            return;
        }

        await this.WithPersistenceLockAsync(async () =>
        {
            var local = CreateSnapshot(this.SiegeData);
            var baseline = this._savedState;
            if (ScalarStateEquals(local, baseline))
            {
                // An idle peer learns a capture without issuing a stale owner/economy update.
                using var read = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
                var current = await read.GetByIdAsync<CastleSiegeData>(local.Id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The persistent Castle Siege state no longer exists.");
                CopyScalarState(current, this.SiegeData);
                CopyScalarState(current, this._savedState);
                this.IsEconomyPersistencePending = false;
                return;
            }

            CastleSiegeData? acknowledged = null;
            await this.PersistAsync((_, target) =>
            {
                ApplyOwnerChanges(local, baseline, target);
                acknowledged = CreateSnapshot(target);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            CopyScalarState(acknowledged!, this.SiegeData);
            CopyScalarState(acknowledged!, this._savedState);
            this.IsEconomyPersistencePending = false;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists deliberate changes to the selected guilds without replacing unchanged rows.
    /// </summary>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    public async ValueTask SaveFinalGuildListAsync()
    {
        if (this.SiegeData is null)
        {
            return;
        }

        await this.WithPersistenceLockAsync(this.SaveFinalGuildListCoreAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the persistent NPC states from a complete runtime snapshot.
    /// </summary>
    public async ValueTask SaveNpcStatesAsync()
    {
        if (this.SiegeData is null)
        {
            return;
        }

        await this.WithPersistenceLockAsync(() => this.SaveNpcStatesCoreAsync(true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all guild registrations of the completed cycle.
    /// </summary>
    public async ValueTask ClearRegistrationsAsync()
    {
        await this.WithPersistenceLockAsync(this.ClearRegistrationsCoreAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures owner and economy values, including local changes not yet acknowledged by persistence.
    /// </summary>
    /// <returns>The caller's pre-mutation scalar state.</returns>
    internal CastleSiegeData CaptureOwnerState()
    {
        var snapshot = new RuntimeSiegeData { Id = this.SiegeData.Id };
        CopyScalarState(this.SiegeData, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Restores a caller's owner and economy values after a known noncommitting rejection.
    /// </summary>
    /// <param name="snapshot">The values captured before the attempted mutation.</param>
    internal void RestoreOwnerState(CastleSiegeData snapshot)
    {
        CopyScalarState(snapshot, this.SiegeData);
    }

    /// <summary>
    /// Commits a Crown handover and its guild-side changes as one persistent operation.
    /// </summary>
    /// <returns>The atomic save operation.</returns>
    internal async ValueTask SaveOwnershipAsync()
    {
        await this.WithPersistenceLockAsync(async () =>
        {
            var local = CreateSnapshot(this.SiegeData);
            var guilds = this.GetFinalGuildSnapshot();
            var guildBaseline = this._savedState.Guilds.ToDictionary(guild => guild.GuildId);
            CastleSiegeData? acknowledged = null;
            await this.PersistAsync(async (context, target) =>
            {
                // Ownership can return to the same guild with different attacking sides after several captures.
                if (!SameOwner(target, this._savedState)
                    || target.Guilds.Count != guildBaseline.Count
                    || guildBaseline.Any(saved => !target.Guilds.Any(current => current.GuildId == saved.Key && current.Side == saved.Value.Side)))
                {
                    throw new PersistenceConflictException(new InvalidOperationException("The Crown owner or selected guild sides changed before this local handover could be saved."));
                }

                ApplyOwnerChanges(local, this._savedState, target);
                await ApplyGuildChangesAsync(context, target, guilds, guildBaseline).ConfigureAwait(false);
                acknowledged = CreateSnapshot(target);
            }).ConfigureAwait(false);
            CopyScalarState(acknowledged!, this.SiegeData);
            CopyScalarState(acknowledged!, this._savedState);
            this.AcknowledgeFinalGuilds(guilds);
            this.IsEconomyPersistencePending = false;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves a staged management mutation before changing its spawned NPC or consuming its item cost.
    /// </summary>
    /// <param name="runtime">The structure whose management request requires an unchanged persistent checkpoint.</param>
    /// <returns>The save operation.</returns>
    internal async ValueTask SaveNpcStateMutationAsync(CastleSiegeNpcRuntime runtime)
    {
        await this.WithPersistenceLockAsync(() => this.SaveNpcStatesCoreAsync(false, runtime)).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a state transition for this game context on its next periodic task tick.
    /// </summary>
    /// <param name="state">The requested state.</param>
    internal void RequestState(CastleSiegeState state)
    {
        Interlocked.Exchange(ref this._requestedState, (int)state);
    }

    /// <summary>
    /// Gets and clears a context-specific pending state request.
    /// </summary>
    /// <param name="state">The requested state when a request was pending.</param>
    /// <returns>A value indicating whether a request was pending.</returns>
    internal bool TryTakeRequestedState(out CastleSiegeState state)
    {
        var requestedState = Interlocked.Exchange(ref this._requestedState, -1);
        state = (CastleSiegeState)requestedState;
        return requestedState >= 0;
    }

    /// <summary>
    /// Tries to place a Life Stone for the specified guild at the player's current position.
    /// </summary>
    /// <param name="player">The player placing the Life Stone.</param>
    /// <param name="guildId">The runtime identifier of the player's guild.</param>
    /// <param name="side">The player's Castle Siege side.</param>
    /// <returns>The placed Life Stone, or <see langword="null"/> when it could not be created.</returns>
    internal async ValueTask<CastleSiegeLifeStone?> CreateLifeStoneAsync(
        Player player,
        uint guildId,
        CastleSiegeJoinSide side)
    {
        var map = player.CurrentMap;
        var definition = this._gameContext.Configuration.Monsters
            .FirstOrDefault(candidate => candidate.Number == CastleSiegeLifeStone.MonsterNumber);
        if (map is null
            || definition is null
            || map.Definition.Number != this.Configuration.CastleSiegeMapDefinition?.Number)
        {
            return null;
        }

        var spawnArea = new MonsterSpawnArea
        {
            MonsterDefinition = definition,
            GameMap = map.Definition,
            X1 = player.Position.X,
            X2 = player.Position.X,
            Y1 = player.Position.Y,
            Y2 = player.Position.Y,
            Direction = player.Rotation,
            Quantity = 1,
            SpawnTrigger = SpawnTrigger.ManuallyForEvent,
        };
        var lifeStone = new CastleSiegeLifeStone(
            spawnArea,
            definition,
            map,
            this,
            guildId,
            side,
            this._timeProvider.GetUtcNow().UtcDateTime,
            this._gameContext.DropGenerator,
            this._gameContext.PlugInManager);
        if (!this._lifeStones.TryAdd(guildId, lifeStone))
        {
            lifeStone.Dispose();
            return null;
        }

        try
        {
            lifeStone.Initialize();
            await map.AddAsync(lifeStone).ConfigureAwait(false);
            lifeStone.OnSpawn();
            return lifeStone;
        }
        catch (OperationCanceledException)
        {
            await lifeStone.DestroyAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await lifeStone.DestroyAsync().ConfigureAwait(false);
            player.Logger.LogError(ex, "Castle Siege Life Stone placement failed.");
            return null;
        }
    }

    /// <summary>
    /// Removes all active Life Stones and clears their guild mappings.
    /// </summary>
    /// <returns>A task that represents the destruction operation.</returns>
    internal async ValueTask KillAllLifeStonesAsync()
    {
        var lifeStones = this._lifeStones.Values.ToArray();
        this._lifeStones.Clear();
        foreach (var lifeStone in lifeStones)
        {
            await lifeStone.DestroyAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Advances all active Life Stones by one Castle Siege task tick.
    /// </summary>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>A task that represents the update operation.</returns>
    internal async ValueTask TickLifeStonesAsync(DateTime utcNow)
    {
        foreach (var lifeStone in this._lifeStones.Values.ToArray())
        {
            await lifeStone.TickAsync(utcNow).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a Life Stone from its guild mapping.
    /// </summary>
    /// <param name="lifeStone">The Life Stone to remove.</param>
    internal void RemoveLifeStone(CastleSiegeLifeStone lifeStone)
    {
        if (this._lifeStones.TryGetValue(lifeStone.OwnerGuildId, out var tracked)
            && ReferenceEquals(tracked, lifeStone))
        {
            this._lifeStones.TryRemove(lifeStone.OwnerGuildId, out _);
        }
    }

    /// <summary>
    /// Tracks a player after a map entry operation.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="map">The entered map.</param>
    internal void TrackPlayer(Player player, GameMap map)
    {
        if (this.Configuration.CastleSiegeMapDefinition?.Number == map.Definition.Number)
        {
            this._siegeMapPlayers[player] = 0;
        }
        else
        {
            this._siegeMapPlayers.TryRemove(player, out _);
        }
    }

    /// <summary>
    /// Stops tracking a player after a map exit operation.
    /// </summary>
    /// <param name="player">The player.</param>
    internal void UntrackPlayer(Player player)
    {
        this._siegeMapPlayers.TryRemove(player, out _);
    }

    /// <summary>
    /// Initializes the current battle owner from the selected defending alliance.
    /// A later Crown capture replaces this value with the attacking guild identifier.
    /// </summary>
    internal void InitializeBattleOwner()
    {
        this.MiddleOwnerGuildId = this.FinalGuildList.Values
                                      .FirstOrDefault(guild => guild.Side == CastleSiegeJoinSide.Defense
                                                               && guild.PersistentGuildId == this.SiegeData.OwnerGuildId)
                                      ?.GuildId
                                  ?? this.FinalGuildList.Values
                                      .FirstOrDefault(guild => guild.Side == CastleSiegeJoinSide.Defense && guild.IsAllianceMaster)
                                      ?.GuildId;
    }

    /// <summary>
    /// Gets the previously assigned side without requiring the player to still be on the siege map.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The assigned side, or <see cref="CastleSiegeJoinSide.None"/>.</returns>
    internal CastleSiegeJoinSide GetTrackedPlayerJoinSide(Player player)
    {
        if (player.SelectedCharacter is { } character
            && this.PlayerJoinSides.TryGetValue(character.Id, out var assignedSide))
        {
            return assignedSide;
        }

        return player.GuildStatus is { } guildStatus
               && this.FinalGuildList.TryGetValue(guildStatus.GuildId, out var participatingGuild)
            ? participatingGuild.Side
            : CastleSiegeJoinSide.None;
    }

    /// <summary>
    /// Gets the gate at which players of a side are spawned during the battle.
    /// </summary>
    /// <param name="side">The side.</param>
    /// <returns>The gate of the side's respawn area, or <see langword="null"/> if the side has none.</returns>
    internal ExitGate? GetRespawnGate(CastleSiegeJoinSide side)
    {
        var area = side switch
        {
            CastleSiegeJoinSide.Defense => this.Configuration.DefenseRespawnArea,
            CastleSiegeJoinSide.Attack1 or CastleSiegeJoinSide.Attack2 or CastleSiegeJoinSide.Attack3 => this.Configuration.AttackRespawnArea,
            _ => null,
        };
        if (area is null || this.Configuration.CastleSiegeMapDefinition is not { } siegeMap)
        {
            return null;
        }

        return new ExitGate
        {
            Map = siegeMap,
            X1 = area.X1,
            Y1 = area.Y1,
            X2 = area.X2,
            Y2 = area.Y2,
            Direction = Direction.South,
        };
    }

    /// <summary>
    /// Gets a snapshot of players currently tracked on the Castle Siege map.
    /// </summary>
    /// <returns>The tracked players which are still on the Castle Siege map.</returns>
    internal IReadOnlyList<Player> GetSiegePlayers()
    {
        var mapNumber = this.Configuration.CastleSiegeMapDefinition?.Number;
        return this._siegeMapPlayers.Keys
            .Where(player => player.CurrentMap?.Definition.Number == mapNumber)
            .ToList();
    }

    /// <summary>
    /// Executes an action concurrently for players currently on the Castle Siege map.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <returns>A task that represents the asynchronous fan-out operation.</returns>
    internal async ValueTask ForEachSiegePlayerAsync(Func<Player, Task> action)
    {
        var actions = this.GetSiegePlayers().Select(action);
        await Task.WhenAll(actions).ConfigureAwait(false);
    }

    /// <summary>
    /// Assigns and sends the current Castle Siege side to a player entering the battle map.
    /// </summary>
    /// <param name="player">The player to synchronize.</param>
    /// <returns>A task that represents the asynchronous synchronization operation.</returns>
    internal async ValueTask SynchronizePlayerJoinSideAsync(Player player)
    {
        if (player.SelectedCharacter is not { } character
            || this.Configuration.CastleSiegeMapDefinition?.Number != player.CurrentMap?.Definition.Number)
        {
            return;
        }

        var side = await this.ResolvePlayerJoinSideAsync(player).ConfigureAwait(false);
        var notificationRequired = !this._notifiedPlayerJoinSides.TryGetValue(player, out var notifiedSide)
                                   || notifiedSide != side;
        this.PlayerJoinSides[character.Id] = side;
        await SetJoinSideMagicEffectAsync(player, side).ConfigureAwait(false);
        if (!notificationRequired)
        {
            return;
        }

        await player.InvokeViewPlugInAsync<ICastleSiegeJoinSidePlugIn>(
                plugIn => plugIn.ShowJoinSideAsync(side))
            .ConfigureAwait(false);
        this._notifiedPlayerJoinSides[player] = side;
    }

    /// <summary>
    /// Clears a player's Castle Siege side and its client-visible effect.
    /// </summary>
    /// <param name="player">The player to clear.</param>
    /// <returns>A task that represents the asynchronous clear operation.</returns>
    internal async ValueTask ClearPlayerJoinSideAsync(Player player)
    {
        if (player.SelectedCharacter is { } character)
        {
            this.PlayerJoinSides.TryRemove(character.Id, out _);
        }

        this._notifiedPlayerJoinSides.TryRemove(player, out _);
        await SetJoinSideMagicEffectAsync(player, CastleSiegeJoinSide.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears all cached player-side assignments and connection notifications.
    /// </summary>
    internal void ClearPlayerJoinSides()
    {
        this.PlayerJoinSides.Clear();
        this._notifiedPlayerJoinSides.Clear();
    }

    /// <summary>
    /// Creates and persists a guild registration.
    /// </summary>
    /// <param name="guildId">The persistent guild identifier.</param>
    /// <param name="guildName">The guild name.</param>
    /// <returns>The created registration.</returns>
    internal async ValueTask<CastleSiegeGuildRegistration> AddRegistrationAsync(Guid guildId, string guildName)
    {
        CastleSiegeGuildRegistration? result = null;
        await this.WithPersistenceLockAsync(async () =>
        {
            await this.LoadRegistrationsAsync().ConfigureAwait(false);
            result = this.RegisteredGuilds.GetValueOrDefault(guildId)
                ?? await this.AddRegistrationCoreAsync(guildId, guildName).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Deletes a persisted guild registration.
    /// </summary>
    /// <param name="registration">The registration.</param>
    internal async ValueTask RemoveRegistrationAsync(CastleSiegeGuildRegistration registration)
    {
        await this.WithPersistenceLockAsync(() => this.RemoveRegistrationCoreAsync(registration)).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments and persists the submitted mark count.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <returns>The updated mark count, or <see langword="null"/> if the registration no longer exists.</returns>
    internal async ValueTask<int?> IncrementMarksAsync(CastleSiegeGuildRegistration registration)
    {
        int? result = null;
        await this.WithPersistenceLockAsync(async () => result = await this.IncrementMarksCoreAsync(registration).ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Initializes the context at the state which contains <paramref name="utcNow"/>.
    /// </summary>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The initialized state period.</returns>
    internal async ValueTask<CastleSiegeStatePeriod> InitializeAsync(DateTime utcNow)
    {
        await this.LoadPersistentStateAsync(true).ConfigureAwait(false);
        var period = this.Schedule.GetCurrentEventPeriod(utcNow);
        this.SetPeriod(period);
        await this.InitializeSiegeMapPlayersAsync().ConfigureAwait(false);
        this.NextNpcSaveUtc = utcNow.AddMinutes(2);
        this.IsInitialized = true;
        return period;
    }

    /// <summary>
    /// Sets the current state period.
    /// </summary>
    /// <param name="period">The state period.</param>
    internal void SetPeriod(CastleSiegeStatePeriod period)
    {
        this.CurrentState = period.State;
        this.StateStartTimeUtc = period.StartUtc;
        this.StateEndTimeUtc = period.EndUtc;
        this.NextNotificationUtc = DateTime.MaxValue;
        this.SentReadyCountdownMinutes.Clear();
    }

    /// <summary>
    /// Gets the remaining time at the specified UTC time.
    /// </summary>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The remaining time.</returns>
    internal TimeSpan GetRemainingTime(DateTime utcNow)
    {
        var remaining = this.StateEndTimeUtc - utcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Resolves a player's side, including persistent membership before the runtime guild callback.
    /// </summary>
    /// <param name="player">The player whose current membership is resolved.</param>
    /// <returns>The selected guild's side, or None for an unassigned character.</returns>
    internal async ValueTask<CastleSiegeJoinSide> ResolvePlayerJoinSideAsync(Player player)
    {
        if (player.GuildStatus is not { } guildStatus)
        {
            if (this.CurrentState != CastleSiegeState.Start || player.SelectedCharacter is not { } character)
            {
                return CastleSiegeJoinSide.None;
            }

            // Normal character admission precedes the GuildServer assignment callback.
            using var guildContext = this._gameContext.PersistenceContextProvider.CreateNewGuildContext();
            if (await guildContext.GetGuildMembershipAsync(character.Name).ConfigureAwait(false) is not { } membership
                || membership.CharacterId != character.Id)
            {
                return CastleSiegeJoinSide.None;
            }

            return this.FinalGuildList.Values.FirstOrDefault(guild => guild.PersistentGuildId == membership.GuildId)?.Side
                ?? CastleSiegeJoinSide.None;
        }

        if (this.FinalGuildList.TryGetValue(guildStatus.GuildId, out var runtimeGuild))
        {
            return runtimeGuild.Side;
        }

        if (this._gameContext is not IGameServerContext gameServerContext
            || await gameServerContext.GuildServer
                .GetPersistentGuildIdAsync(guildStatus.GuildId)
                .ConfigureAwait(false) is not { } persistentGuildId)
        {
            return CastleSiegeJoinSide.None;
        }

        var persistedEntry = this.FinalGuildList.FirstOrDefault(
            entry => entry.Value.PersistentGuildId == persistentGuildId);
        if (persistedEntry.Value is null)
        {
            return CastleSiegeJoinSide.None;
        }

        this.FinalGuildList.TryRemove(persistedEntry.Key, out _);
        persistedEntry.Value.GuildId = guildStatus.GuildId;
        this.FinalGuildList[guildStatus.GuildId] = persistedEntry.Value;
        return persistedEntry.Value.Side;
    }

    /// <summary>
    /// Selects guilds from current registrations while coordinating the complete read-select-save operation.
    /// </summary>
    /// <param name="select">The runtime selection operation.</param>
    /// <returns>The selection task.</returns>
    internal async ValueTask SelectGuildsAsync(Func<ValueTask> select)
    {
        await this.WithPersistenceLockAsync(async () =>
        {
            await this.LoadRegistrationsAsync().ConfigureAwait(false);
            using var read = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
            var current = await read.GetByIdAsync<CastleSiegeData>(this.SiegeData.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The persistent Castle Siege state no longer exists.");
            if (ScalarStateEquals(this.SiegeData, this._savedState))
            {
                CopyScalarState(current, this.SiegeData);
                CopyScalarState(current, this._savedState);
            }

            await select().ConfigureAwait(false);
            await this.SaveFinalGuildListCoreAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static bool SameOwner(CastleSiegeData first, CastleSiegeData second)
    {
        return first.OwnerGuildId == second.OwnerGuildId && first.IsOccupied == second.IsOccupied;
    }

    private static void ApplyOwnerChanges(CastleSiegeData local, CastleSiegeData baseline, CastleSiegeData target)
    {
        var ownerChanged = !SameOwner(local, baseline);
        if (ownerChanged && !SameOwner(target, baseline) && !SameOwner(target, local))
        {
            throw new PersistenceConflictException(new InvalidOperationException("Castle ownership changed before this local handover could be saved."));
        }

        if (ownerChanged)
        {
            if (target.OwnerGuildId != local.OwnerGuildId || target.IsOccupied != local.IsOccupied)
            {
                // A capture intentionally replaces the former owner's entire economy, including zero values.
                CopyScalarState(local, target);
            }
            else
            {
                // Another context already persisted this handover. Do not repeat the zero-value reset.
                if (local.TaxChaos != 0)
                {
                    target.TaxChaos = local.TaxChaos;
                }

                if (local.TaxStore != 0)
                {
                    target.TaxStore = local.TaxStore;
                }

                if (local.TaxHunt != 0)
                {
                    target.TaxHunt = local.TaxHunt;
                }

                if (local.IsHuntZoneEnabled)
                {
                    target.IsHuntZoneEnabled = true;
                }

                target.TributeMoney = checked(target.TributeMoney + local.TributeMoney);
            }
        }
        else
        {
            var economyChanged = !ScalarStateEquals(local, baseline);
            if (economyChanged && (target.OwnerGuildId != baseline.OwnerGuildId || target.IsOccupied != baseline.IsOccupied))
            {
                throw new InvalidOperationException("Castle ownership changed while local economy changes were pending.");
            }

            if (local.TaxChaos != baseline.TaxChaos)
            {
                target.TaxChaos = local.TaxChaos;
            }

            if (local.TaxStore != baseline.TaxStore)
            {
                target.TaxStore = local.TaxStore;
            }

            if (local.TaxHunt != baseline.TaxHunt)
            {
                target.TaxHunt = local.TaxHunt;
            }

            if (local.IsHuntZoneEnabled != baseline.IsHuntZoneEnabled)
            {
                target.IsHuntZoneEnabled = local.IsHuntZoneEnabled;
            }

            target.TributeMoney = checked(target.TributeMoney + (local.TributeMoney - baseline.TributeMoney));
            if (target.TributeMoney < 0)
            {
                throw new InvalidOperationException("Castle tribute was withdrawn by another game context.");
            }
        }
    }

    private static async ValueTask ApplyGuildChangesAsync(
        IContext context,
        CastleSiegeData persistentData,
        Dictionary<Guid, CastleSiegeGuild> local,
        Dictionary<Guid, CastleSiegeGuild> baseline)
    {
        foreach (var target in persistentData.Guilds.ToList())
        {
            if (baseline.ContainsKey(target.GuildId) && !local.ContainsKey(target.GuildId))
            {
                await context.DeleteAsync(target).ConfigureAwait(false);
                persistentData.Guilds.Remove(target);
            }
        }

        foreach (var (id, source) in local)
        {
            var target = persistentData.Guilds.FirstOrDefault(guild => guild.GuildId == id);
            if (target is null)
            {
                // An unchanged stale entry must not resurrect a row removed by the other server.
                if (baseline.TryGetValue(id, out var previous) && GuildStateEquals(source, previous))
                {
                    continue;
                }

                target = context.CreateNew<CastleSiegeGuild>();
                target.GuildId = id;
                CopyGuildState(source, target);
                persistentData.Guilds.Add(target);
            }
            else if (!baseline.TryGetValue(id, out var previous))
            {
                CopyGuildState(source, target);
            }
            else
            {
                if (source.GuildName != previous.GuildName)
                {
                    target.GuildName = source.GuildName;
                }

                if (source.Side != previous.Side)
                {
                    target.Side = source.Side;
                }

                if (source.Score != previous.Score)
                {
                    target.Score = source.Score;
                }

                if (source.IsAllianceMaster != previous.IsAllianceMaster)
                {
                    target.IsAllianceMaster = source.IsAllianceMaster;
                }
            }
        }
    }

    private static bool ScalarStateEquals(CastleSiegeData first, CastleSiegeData second)
    {
        return first.OwnerGuildId == second.OwnerGuildId && first.IsOccupied == second.IsOccupied
            && first.TaxChaos == second.TaxChaos && first.TaxStore == second.TaxStore && first.TaxHunt == second.TaxHunt
            && first.IsHuntZoneEnabled == second.IsHuntZoneEnabled && first.TributeMoney == second.TributeMoney;
    }

    private static bool GuildStateEquals(CastleSiegeGuild first, CastleSiegeGuild second)
    {
        return first.GuildName == second.GuildName && first.Side == second.Side
            && first.Score == second.Score && first.IsAllianceMaster == second.IsAllianceMaster;
    }

    private static bool NpcStateEquals(CastleSiegeNpcState first, CastleSiegeNpcState second)
    {
        return first.CurrentHp == second.CurrentHp && first.DefenseLevel == second.DefenseLevel
            && first.RegenLevel == second.RegenLevel && first.LifeLevel == second.LifeLevel;
    }

    private static void CopyGuildState(CastleSiegeGuild source, CastleSiegeGuild target)
    {
        target.GuildName = source.GuildName;
        target.Side = source.Side;
        target.Score = source.Score;
        target.IsAllianceMaster = source.IsAllianceMaster;
    }

    private static CastleSiegeData CreateSnapshot(CastleSiegeData source)
    {
        var snapshot = new RuntimeSiegeData { Id = source.Id };
        CopyScalarState(source, snapshot);
        foreach (var state in source.NpcStates)
        {
            snapshot.NpcStates.Add(CloneNpcState(state));
        }

        foreach (var guild in source.Guilds)
        {
            var copy = new CastleSiegeGuild { Id = guild.Id, GuildId = guild.GuildId };
            CopyGuildState(guild, copy);
            snapshot.Guilds.Add(copy);
        }

        return snapshot;
    }

    private static CastleSiegeNpcState CloneNpcState(CastleSiegeNpcState source)
    {
        var clone = new CastleSiegeNpcState { Id = source.Id };
        CopyNpcState(source, clone);
        return clone;
    }

    private static void CopyScalarState(CastleSiegeData source, CastleSiegeData target)
    {
        target.OwnerGuildId = source.OwnerGuildId;
        target.IsOccupied = source.IsOccupied;
        target.TaxChaos = source.TaxChaos;
        target.TaxStore = source.TaxStore;
        target.TaxHunt = source.TaxHunt;
        target.IsHuntZoneEnabled = source.IsHuntZoneEnabled;
        target.TributeMoney = source.TributeMoney;
    }

    private static void CopyNpcState(CastleSiegeNpcState source, CastleSiegeNpcState target)
    {
        target.MonsterNumber = source.MonsterNumber;
        target.InstanceId = source.InstanceId;
        target.DefenseLevel = source.DefenseLevel;
        target.RegenLevel = source.RegenLevel;
        target.LifeLevel = source.LifeLevel;
        target.CurrentHp = source.CurrentHp;
    }

    private static (short MonsterNumber, byte InstanceId) GetNpcKey(CastleSiegeNpcState state)
    {
        return (state.MonsterNumber, state.InstanceId);
    }

    private static CastleSiegeMagicEffectNumber? GetMagicEffectNumber(CastleSiegeJoinSide side)
    {
        return side switch
        {
            CastleSiegeJoinSide.Defense => CastleSiegeMagicEffectNumber.Defense,
            CastleSiegeJoinSide.Attack1 => CastleSiegeMagicEffectNumber.Attack1,
            CastleSiegeJoinSide.Attack2 => CastleSiegeMagicEffectNumber.Attack2,
            CastleSiegeJoinSide.Attack3 => CastleSiegeMagicEffectNumber.Attack3,
            _ => null,
        };
    }

    private static bool IsJoinSideEffect(short effectNumber)
    {
        return Enum.IsDefined<CastleSiegeMagicEffectNumber>((CastleSiegeMagicEffectNumber)effectNumber);
    }

    private static async ValueTask SetJoinSideMagicEffectAsync(Player player, CastleSiegeJoinSide side)
    {
        var expectedEffectNumber = GetMagicEffectNumber(side);
        var activeSideEffects = player.MagicEffectList
            .GetActiveEffectsSnapshot()
            .Where(effect => IsJoinSideEffect(effect.Id))
            .ToList();
        foreach (var effect in activeSideEffects)
        {
            if (effect.Id != (short?)expectedEffectNumber)
            {
                await effect.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (expectedEffectNumber is not { } expected
            || activeSideEffects.Any(effect => effect.Id == (short)expected))
        {
            return;
        }

        if (player.GameContext.Configuration.MagicEffects.FirstOrDefault(
                effect => effect.Number == (short)expected) is not { } effectDefinition)
        {
            return;
        }

        await player.MagicEffectList
            .AddEffectAsync(new MagicEffect(JoinSideEffectDuration, effectDefinition))
            .ConfigureAwait(false);
    }

    private async ValueTask SaveNpcStatesCoreAsync(bool synchronizeRuntime, CastleSiegeNpcRuntime? mutation = null)
    {
        if (synchronizeRuntime)
        {
            this.NpcController.SynchronizeNpcStates();
        }

        var persistedNpcs = this.NpcController.GetRuntimeSnapshot()
            .Where(npc => npc.Definition.IsPersistedToDatabase)
            .ToList();
        if (persistedNpcs.Count == 0 || persistedNpcs.Any(npc => npc.PersistedState is null)
            || (mutation is not null && !persistedNpcs.Contains(mutation)))
        {
            if (mutation is not null)
            {
                throw new PersistenceConflictException(new InvalidOperationException("The Castle NPC management snapshot is incomplete."));
            }

            return;
        }

        var statesToSave = persistedNpcs
            .Select(npc => CloneNpcState(npc.PersistedState!))
            .ToDictionary(GetNpcKey);

        // Completeness follows the current configuration, which may intentionally remove saved structures.
        var configuredKeys = this.Configuration.NpcDefinitions
            .Where(definition => definition.IsPersistedToDatabase)
            .Select(definition => (
                definition.MonsterDefinition?.Number ?? throw Error.NotInitializedProperty(definition, nameof(definition.MonsterDefinition)),
                definition.InstanceId))
            .ToHashSet();
        if (!configuredKeys.SetEquals(statesToSave.Keys))
        {
            if (mutation is not null)
            {
                throw new PersistenceConflictException(new InvalidOperationException("Castle NPC definitions changed before the management request could be saved."));
            }

            return;
        }

        var baseline = this._savedState.NpcStates.ToDictionary(GetNpcKey);
        await this.PersistAsync(async (context, persistentData) =>
        {
            if (mutation?.PersistedState is { } staged)
            {
                var key = GetNpcKey(staged);
                var current = persistentData.NpcStates.FirstOrDefault(state => GetNpcKey(state) == key);
                if ((baseline.TryGetValue(key, out var previous) && (current is null ? previous.Id != Guid.Empty : !NpcStateEquals(current, previous)))
                    || persistentData.OwnerGuildId != this._savedState.OwnerGuildId
                    || persistentData.IsOccupied != this._savedState.IsOccupied)
                {
                    throw new PersistenceConflictException(new InvalidOperationException("Castle ownership or NPC state changed before the management request could be saved."));
                }
            }

            // Validate every health increase before mutating any tracked state (including InMemory entities).
            foreach (var (key, source) in statesToSave)
            {
                var current = persistentData.NpcStates.FirstOrDefault(state => GetNpcKey(state) == key);
                if (baseline.TryGetValue(key, out var previous)
                    && source.CurrentHp > previous.CurrentHp
                    && current is not null
                    && current.CurrentHp != previous.CurrentHp)
                {
                    throw new PersistenceConflictException(new InvalidOperationException("Castle NPC health changed before a local repair or regeneration checkpoint could be saved."));
                }
            }

            foreach (var target in persistentData.NpcStates.ToList())
            {
                var key = GetNpcKey(target);
                if (!statesToSave.ContainsKey(key) && baseline.ContainsKey(key))
                {
                    await context.DeleteAsync(target).ConfigureAwait(false);
                    persistentData.NpcStates.Remove(target);
                }
            }

            foreach (var (key, source) in statesToSave)
            {
                var target = persistentData.NpcStates.FirstOrDefault(state => GetNpcKey(state) == key);
                if (target is null)
                {
                    if (baseline.TryGetValue(key, out var previous) && previous.Id != Guid.Empty)
                    {
                        continue;
                    }

                    target = context.CreateNew<CastleSiegeNpcState>();
                    CopyNpcState(source, target);
                    persistentData.NpcStates.Add(target);
                }
                else if (!baseline.TryGetValue(key, out var previous))
                {
                    CopyNpcState(source, target);
                }
                else
                {
                    if (source.CurrentHp < previous.CurrentHp)
                    {
                        // Damage checkpoints merge their local loss, so a delayed save cannot resurrect a kill.
                        target.CurrentHp = (int)Math.Max(0L, (long)target.CurrentHp + source.CurrentHp - previous.CurrentHp);
                    }
                    else if (source.CurrentHp > previous.CurrentHp)
                    {
                        target.CurrentHp = source.CurrentHp;
                    }

                    if (source.DefenseLevel != previous.DefenseLevel)
                    {
                        target.DefenseLevel = source.DefenseLevel;
                    }

                    if (source.RegenLevel != previous.RegenLevel)
                    {
                        target.RegenLevel = source.RegenLevel;
                    }

                    if (source.LifeLevel != previous.LifeLevel)
                    {
                        target.LifeLevel = source.LifeLevel;
                    }
                }
            }
        }).ConfigureAwait(false);

        this._savedState.NpcStates.Clear();
        foreach (var state in statesToSave.Values)
        {
            this._savedState.NpcStates.Add(state);
        }
    }

    private async ValueTask ClearRegistrationsCoreAsync()
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeGuildRegistration), false, this._gameContext.Configuration);
        foreach (var registration in (await context.GetAsync<CastleSiegeGuildRegistration>().ConfigureAwait(false)).ToList())
        {
            await context.DeleteAsync(registration).ConfigureAwait(false);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        this.RegisteredGuilds.Clear();
    }

    private async ValueTask<CastleSiegeGuildRegistration> AddRegistrationCoreAsync(Guid guildId, string guildName)
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeGuildRegistration),
            false,
            this._gameContext.Configuration);
        var registration = context.CreateNew<CastleSiegeGuildRegistration>();
        registration.GuildId = guildId;
        registration.GuildName = guildName;

        // Registration orders stay monotonic for one cycle and are intentionally not compacted after unregistration.
        registration.RegistrationOrder = this.RegisteredGuilds.IsEmpty
            ? 1
            : this.RegisteredGuilds.Values.Max(entry => entry.RegistrationOrder) + 1;
        await context.SaveChangesAsync().ConfigureAwait(false);
        this.RegisteredGuilds[guildId] = registration;
        return registration;
    }

    private async ValueTask RemoveRegistrationCoreAsync(CastleSiegeGuildRegistration registration)
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeGuildRegistration),
            false,
            this._gameContext.Configuration);
        if (await context.GetByIdAsync<CastleSiegeGuildRegistration>(registration.Id).ConfigureAwait(false) is { } persistentRegistration)
        {
            await context.DeleteAsync(persistentRegistration).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        this.RegisteredGuilds.TryRemove(registration.GuildId, out _);
    }

    private async ValueTask<int?> IncrementMarksCoreAsync(CastleSiegeGuildRegistration registration)
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeGuildRegistration),
            false,
            this._gameContext.Configuration);
        if (await context.GetByIdAsync<CastleSiegeGuildRegistration>(registration.Id).ConfigureAwait(false) is not { } persistentRegistration)
        {
            this.RegisteredGuilds.TryRemove(registration.GuildId, out _);
            return null;
        }

        persistentRegistration.Marks++;
        await context.SaveChangesAsync().ConfigureAwait(false);
        registration.Marks = persistentRegistration.Marks;
        return registration.Marks;
    }

    private async ValueTask SaveFinalGuildListCoreAsync()
    {
        var local = this.GetFinalGuildSnapshot();
        var baseline = this._savedState.Guilds.ToDictionary(guild => guild.GuildId);
        await this.PersistAsync((context, target) => ApplyGuildChangesAsync(context, target, local, baseline)).ConfigureAwait(false);
        this.AcknowledgeFinalGuilds(local);
    }

    private Dictionary<Guid, CastleSiegeGuild> GetFinalGuildSnapshot()
    {
        return CastleSiegeGuildSelector.OrderFinalGuilds(this.FinalGuildList.Values)
            .ToDictionary(guild => guild.PersistentGuildId, guild => new CastleSiegeGuild
            {
                GuildId = guild.PersistentGuildId,
                GuildName = guild.GuildName,
                Side = guild.Side,
                Score = guild.Score,
                IsAllianceMaster = guild.IsAllianceMaster,
            });
    }

    private void AcknowledgeFinalGuilds(Dictionary<Guid, CastleSiegeGuild> local)
    {
        this._savedState.Guilds.Clear();
        foreach (var guild in local.Values)
        {
            this._savedState.Guilds.Add(guild);
        }
    }

    private async ValueTask LoadPersistentStateAsync(bool createMissing)
    {
        var coordinator = PersistenceCoordinators.GetValue(this._gameContext.PersistenceContextProvider, _ => new PersistenceCoordinator());
        using (await coordinator.Initialization.LockAsync().ConfigureAwait(false))
        {
            var data = await this.LoadDataAsync().ConfigureAwait(false);
            if (data is null)
            {
                data = createMissing
                    ? await this.CreateDataAsync().ConfigureAwait(false)
                    : throw new InvalidOperationException("The persistent Castle Siege state does not exist.");
            }

            var gate = coordinator.Aggregates.GetOrAdd(data.Id, _ => new AsyncLock());
            using (await gate.LockAsync().ConfigureAwait(false))
            {
                using var read = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
                var current = await read.GetByIdAsync<CastleSiegeData>(data.Id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The persistent Castle Siege state no longer exists.");
                this.SiegeData = CreateSnapshot(current);
                foreach (var runtime in this.NpcController.GetRuntimeSnapshot().Where(runtime => runtime.Definition.IsPersistedToDatabase))
                {
                    var number = runtime.Definition.MonsterDefinition?.Number;
                    runtime.PersistedState = this.SiegeData.NpcStates.FirstOrDefault(state => state.MonsterNumber == number && state.InstanceId == runtime.Definition.InstanceId);
                }

                this.NpcController.InitializePersistentStructures();
                this._savedState = CreateSnapshot(this.SiegeData);
                await this.LoadRegistrationsAsync().ConfigureAwait(false);
                await this.LoadFinalGuildListAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask WithPersistenceLockAsync(Func<ValueTask> operation)
    {
        // shortcut: coordination covers game contexts sharing a provider in this process. Multiple server hosts
        // still need database/distributed coordination; this lock is not a distributed aggregate lock.
        var coordinator = PersistenceCoordinators.GetValue(this._gameContext.PersistenceContextProvider, _ => new PersistenceCoordinator());
        var gate = coordinator.Aggregates.GetOrAdd(this.SiegeData.Id, _ => new AsyncLock());
        using (await gate.LockAsync().ConfigureAwait(false))
        {
            await operation().ConfigureAwait(false);
        }
    }

    private async ValueTask PersistAsync(Func<IContext, CastleSiegeData, ValueTask> apply)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
            var current = await context.GetByIdAsync<CastleSiegeData>(this.SiegeData.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The persistent Castle Siege state no longer exists.");
            await apply(context, current).ConfigureAwait(false);
            try
            {
                if (!await context.SaveChangesAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The Castle Siege persistence operation did not succeed.");
                }

                return;
            }
            catch (PersistenceConflictException exception) when (attempt < maximumAttempts)
            {
                // A conflict is known not to have committed. Reapply the logical delta to a fresh unit of work.
                this._gameContext.LoggerFactory?.CreateLogger<CastleSiegeContext>()
                    .LogWarning(exception, "Retrying Castle Siege persistence with a fresh context (attempt {Attempt}/{MaximumAttempts}).", attempt, maximumAttempts);
                await Task.Delay(attempt * 10).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask InitializeSiegeMapPlayersAsync()
    {
        this._siegeMapPlayers.Clear();
        if (this.Configuration.CastleSiegeMapDefinition is not { } siegeMapDefinition)
        {
            return;
        }

        foreach (var player in await this._gameContext.GetPlayersAsync().ConfigureAwait(false))
        {
            if (player.CurrentMap?.Definition.Number == siegeMapDefinition.Number)
            {
                this._siegeMapPlayers[player] = 0;
            }
        }
    }

    private async ValueTask<CastleSiegeData?> LoadDataAsync()
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
        return (await context.GetAsync<CastleSiegeData>().ConfigureAwait(false)).FirstOrDefault();
    }

    private async ValueTask<CastleSiegeData> CreateDataAsync()
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, this._gameContext.Configuration);
        var data = context.CreateNew<CastleSiegeData>();
        await context.SaveChangesAsync().ConfigureAwait(false);
        return data;
    }

    private async ValueTask LoadRegistrationsAsync()
    {
        using var context = this._gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeGuildRegistration), false, this._gameContext.Configuration);
        var registrations = await context.GetAsync<CastleSiegeGuildRegistration>().ConfigureAwait(false);
        this.RegisteredGuilds.Clear();
        foreach (var registration in registrations)
        {
            this.RegisteredGuilds[registration.GuildId] = registration;
        }
    }

    private async ValueTask LoadFinalGuildListAsync()
    {
        this.FinalGuildList.Clear();
        if (this._gameContext is not IGameServerContext gameServerContext)
        {
            return;
        }

        foreach (var guild in this.SiegeData.Guilds)
        {
            var runtimeGuildId = await gameServerContext.GuildServer
                .GetGuildIdAsync(guild.GuildId)
                .ConfigureAwait(false);
            if (runtimeGuildId == 0)
            {
                continue;
            }

            this.FinalGuildList[runtimeGuildId] = new CastleSiegeGuildParticipant
            {
                GuildId = runtimeGuildId,
                PersistentGuildId = guild.GuildId,
                GuildName = guild.GuildName,
                Side = guild.Side,
                Score = guild.Score,
                IsAllianceMaster = guild.IsAllianceMaster,
            };
        }

        this.InitializeBattleOwner();
    }

    private sealed class PersistenceCoordinator
    {
        public AsyncLock Initialization { get; } = new();

        public ConcurrentDictionary<Guid, AsyncLock> Aggregates { get; } = new();
    }

    private sealed class RuntimeSiegeData : CastleSiegeData
    {
        public RuntimeSiegeData()
        {
            this.NpcStates = new List<CastleSiegeNpcState>();
            this.Guilds = new List<CastleSiegeGuild>();
        }
    }
}
