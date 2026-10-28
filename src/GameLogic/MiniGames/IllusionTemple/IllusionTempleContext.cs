// <copyright file="IllusionTempleContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;

using System.Collections.Concurrent;
using System.Threading;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using Nito.AsyncEx;

/// <summary>The shared Season Six Illusion Temple match.</summary>
public sealed class IllusionTempleContext : MiniGameContext
{
    private const short ProtectionEffect = 202;
    private const short RestraintEffect = 203;
    private const short CarrierEffect = 204;
    private readonly ConcurrentDictionary<Guid, Member> _members = new();
    private readonly ConcurrentDictionary<Player, IElement> _skins = new();
    private readonly ConcurrentDictionary<Guid, Channel> _channels = new();
    private readonly List<NonPlayerCharacter> _statues = [];
    private readonly List<(Point Position, byte Attributes)> _waterTiles = [];

    // Acquire a player's persistence lock before this lock; never wait for another player's lock while holding either.
    private readonly AsyncLock _matchLock = new();
    private readonly IllusionTempleStartConfiguration _settings;
    private readonly byte[] _scores = new byte[2];
    private Player? _holder;
    private DroppedItem? _relicDrop;
    private DateTime _endsAtUtc;
    private byte _winningTeam = byte.MaxValue;

    /// <summary>Initializes a new instance of the <see cref="IllusionTempleContext"/> class.</summary>
    /// <param name="key">The match key.</param>
    /// <param name="definition">The configured event level.</param>
    /// <param name="gameContext">The game context.</param>
    /// <param name="mapInitializer">The map initializer.</param>
    public IllusionTempleContext(MiniGameMapKey key, MiniGameDefinition definition, IGameContext gameContext, IMapInitializer mapInitializer)
        : base(key, definition, gameContext, mapInitializer)
    {
        this._settings = (gameContext.PlugInManager.GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(MiniGameType.IllusionTemple) as IllusionTempleStartPlugIn)?.Configuration
            ?? IllusionTempleStartConfiguration.Default;
        Validator.ValidateObject(this._settings, new ValidationContext(this._settings), true);
        if (this._settings.CaptureDuration <= TimeSpan.Zero || this._settings.CaptureDuration > TimeSpan.FromMinutes(5)
            || this._settings.SkillDuration <= TimeSpan.Zero || this._settings.SkillDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Illusion Temple capture and skill durations must be positive and at most five minutes.");
        }
    }

    /// <inheritdoc />
    public override bool AllowPlayerKilling => true;

    /// <inheritdoc />
    protected override int MinimumPlayerCount => this._settings.MinimumPlayers;

    /// <inheritdoc />
    protected override bool AllowDeadPlayerRejoin => true;

    /// <inheritdoc />
    protected override TimeSpan RemainingTime => this.State == MiniGameState.Playing
        ? (this._endsAtUtc - DateTime.UtcNow).AtLeast(TimeSpan.Zero) : TimeSpan.Zero;

    private bool IsMatchPlaying => !this.IsDisposed && !this.IsDisposing
        && this.State == MiniGameState.Playing && !this.GameEndedToken.IsCancellationRequested;

    /// <inheritdoc />
    public override bool IsItemAllowedToEquip(Item item) => !item.IsTransformationRing();

    /// <inheritdoc />
    public override bool IsSkillAllowed(Skill skill, Player attacker, IAttackable target) => false;

    /// <inheritdoc />
    public override bool IsAttackAllowed(IAttacker attacker, Player target)
    {
        var allowed = this.IsMatchPlaying && attacker is Player source
            && source.IsAlive && target.IsAlive && source.CurrentMap == this.Map && target.CurrentMap == this.Map
            && this.GetTeam(source) is var team && team < 2 && this.GetTeam(target) == 1 - team
            && source.Attributes?[Stats.IsStunned] is not > 0
            && !target.MagicEffectList.ContainsEffect(ProtectionEffect);
        if (allowed && target.SelectedCharacter is { } character)
        {
            this._channels.TryRemove(character.Id, out _);
        }

        return allowed;
    }

    /// <summary>Gets the player's assigned team, or 0xFF before assignment.</summary>
    /// <param name="player">The player.</param>
    /// <returns>The team.</returns>
    public byte GetTeam(Player player) => this.GetMember(player)?.Team ?? byte.MaxValue;

    /// <summary>Gets the respawn gate next to the player's configured pedestal.</summary>
    /// <param name="player">The player.</param>
    /// <returns>The configured team gate, if available.</returns>
    public ExitGate? GetTeamSpawnGate(Player player)
    {
        if (this.GetTeam(player) is not (0 or 1) || this.State != MiniGameState.Playing)
        {
            return null;
        }

        var baseSpawn = this.Map.Definition.MonsterSpawns.FirstOrDefault(spawn => spawn.MonsterDefinition?.Number == 383 + this.GetTeam(player));
        return baseSpawn is null ? null : this.Map.Definition.ExitGates.Where(gate => gate.IsSpawnGate)
            .OrderBy(gate => Math.Pow(gate.X1 - baseSpawn.X1, 2) + Math.Pow(gate.Y1 - baseSpawn.Y1, 2)).FirstOrDefault();
    }

    /// <inheritdoc />
    internal override Point? GetEntrySpawnPosition(Player player) => this.Map.Terrain.WalkMap[player.Position.X, player.Position.Y]
        ? null
        : this.Map.Terrain.GetWalkableCoordinate(this.GetTeamSpawnGate(player) ?? this.Definition.Entrance);

    /// <summary>Starts one authoritative stationary capture or deposit channel.</summary>
    /// <param name="player">The participant.</param>
    /// <param name="npc">The statue or own pedestal.</param>
    public async ValueTask TalkToEventNpcAsync(Player player, NonPlayerCharacter npc)
    {
        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            if (!this.CanInteract(player, npc) || player.SelectedCharacter is not { } character
                || this._channels.ContainsKey(character.Id))
            {
                return;
            }

            var channel = new Channel(player, npc, player.Position, player.Attributes![Stats.CurrentHealth], player.Attributes[Stats.CurrentShield]);
            this._channels[character.Id] = channel;
            var cancellationToken = this.GameEndedToken;

            // The packet's persistence scope must not flow into the delayed interaction.
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => this.RunChannelAsync(character.Id, channel, cancellationToken));
            }
        }
    }

    /// <summary>Applies a validated event skill, charging event points only once.</summary>
    /// <param name="player">The caster.</param>
    /// <param name="skill">The event skill number.</param>
    /// <param name="targetId">The target identifier.</param>
    public async ValueTask UseSkillAsync(Player player, ushort skill, ushort targetId)
    {
        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            var member = this.GetMember(player);
            var target = skill is 210 or 212 ? player : player.GetObject(targetId) as Player;
            var cost = skill switch { 210 => this._settings.ProtectionCost, 211 => this._settings.RestraintCost, 212 => this._settings.PursuitCost, 213 => this._settings.ShieldBurnCost, _ => -1 };
            var valid = this.IsMatchPlaying && member is not null && player.CurrentMap == this.Map && player.IsAlive
                && player.Attributes?[Stats.IsStunned] is not > 0 && cost >= 0 && member.Points >= cost && target is { IsAlive: true }
                && target.CurrentMap == this.Map && this.GetMember(target) is not null;
            if (valid && skill is 211 or 213)
            {
                valid = this.GetTeam(target!) != member!.Team && player.GetDistanceTo(target!) <= 3
                    && !target!.MagicEffectList.ContainsEffect(ProtectionEffect);
            }

            if (valid && skill == 212)
            {
                valid = this._holder is { IsAlive: true, CurrentMap: not null } holder && holder.CurrentMap == this.Map;
            }

            if (valid)
            {
                member!.Points -= cost;
                this._channels.TryRemove(player.SelectedCharacter!.Id, out _);
                switch (skill)
                {
                    case 210:
                        await this.ApplyTimedEffectAsync(player, ProtectionEffect, skill).ConfigureAwait(false);
                        break;
                    case 211:
                        this._channels.TryRemove(target!.SelectedCharacter!.Id, out _);
                        await target.StopWalkingAsync().ConfigureAwait(false);
                        await this.ApplyTimedEffectAsync(
                            target,
                            RestraintEffect,
                            skill,
                            new MagicEffect.ElementWithTarget(new SimpleElement(1, AggregateType.AddRaw), Stats.IsStunned)).ConfigureAwait(false);
                        break;
                    case 212:
                        var destination = this._holder!.Position;
                        await player.TeleportToMapAsync(this.Map, destination).ConfigureAwait(false);
                        break;
                    case 213:
                        this._channels.TryRemove(target!.SelectedCharacter!.Id, out _);
                        target.Attributes![Stats.CurrentShield] *= 1 - this._settings.ShieldBurnFactor;
                        break;
                }
            }

            await this.ForEachPlayerAsync(observer => observer.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view =>
                view.ShowSkillResultAsync(skill, player, target ?? player, valid)).AsTask()).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowSkillPointsAsync((byte)(member?.Points ?? 0))).ConfigureAwait(false);
        }
    }

    /// <summary>Drops the relic on death and grants points for an opposing participant's kill.</summary>
    /// <param name="killed">The killed participant.</param>
    /// <param name="killer">The killer, if any.</param>
    public async ValueTask PlayerKilledAsync(Player killed, IAttacker? killer)
    {
        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            if (!this.IsMatchPlaying || this.GetMember(killed) is null)
            {
                return;
            }

            this._channels.TryRemove(killed.SelectedCharacter!.Id, out _);
            if (this._holder == killed)
            {
                var cancellationToken = this.GameEndedToken;

                // The death hook can hold the attacker's lock; never await the victim's lock from that flow.
                using (ExecutionContext.SuppressFlow())
                {
                    _ = Task.Run(() => this.DropRelicAfterDeathAsync(killed, cancellationToken));
                }
            }

            if (killer is Player player && this.GetMember(player) is { } member && member.Team != this.GetTeam(killed))
            {
                member.Points = Math.Min(byte.MaxValue, member.Points + this._settings.SkillPointsPerKill);
                await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowSkillPointsAsync((byte)member.Points)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Claims a finishing participant's configured reward at most once.</summary>
    /// <param name="player">The participant.</param>
    public async ValueTask ClaimRewardAsync(Player player)
    {
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            using (await this._matchLock.LockAsync().ConfigureAwait(false))
            {
                if (this.IsDisposed || this.IsDisposing || this.State != MiniGameState.Ended || !player.IsAlive || player.CurrentMiniGame != this
                    || this.GetMember(player) is not { RewardClaimed: false } member)
                {
                    return;
                }

                member.RewardClaimed = true;
                var experienceBefore = player.SelectedCharacter!.Experience;
                var rank = member.Team == this._winningTeam ? 1 : 2;
                await this.GiveRewardsAndGetBonusScoreAsync(player, rank).ConfigureAwait(false);
                member.Experience = (int)Math.Min((ulong)int.MaxValue, (ulong)(player.SelectedCharacter.Experience - experienceBefore));
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask OnGameStartAsync(ICollection<Player> players)
    {
        await base.OnGameStartAsync(players).ConfigureAwait(false);
        var shuffled = players.ToArray();
        Random.Shared.Shuffle(shuffled);
        for (var index = 0; index < shuffled.Length; index++)
        {
            var player = shuffled[index];
            this._members[player.SelectedCharacter!.Id] = new Member(player, (byte)(index % 2));
        }

        this.SetWaterOpen(true);
        this._endsAtUtc = DateTime.UtcNow.Add(this.Definition.GameDuration);
        foreach (var player in players)
        {
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                this.ApplyTeamSkin(player);
                await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowPhaseAsync(this.Definition.GameLevel, 2)).ConfigureAwait(false);
                if (this.GetTeamSpawnGate(player) is { } gate)
                {
                    await player.WarpToAsync(gate).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        _ = this.RunInfoLoopAsync();
    }

    /// <inheritdoc />
    protected override async ValueTask OnObjectAddedToMapAsync((GameMap Map, ILocateable Object) args)
    {
        await base.OnObjectAddedToMapAsync(args).ConfigureAwait(false);
        if (args.Object is NonPlayerCharacter { Definition.Number: 380 } statue && !this._statues.Contains(statue))
        {
            this._statues.Add(statue);
        }

        if (args.Object is Player player && player.CurrentMiniGame == this)
        {
            if (player.SelectedCharacter is { } character && this._members.TryGetValue(character.Id, out var member))
            {
                member.Player = player;
                this.ApplyTeamSkin(player);
            }

            byte phase = this.State switch { MiniGameState.Playing => 2, MiniGameState.Closed => 1, MiniGameState.Ended => 3, _ => 0 };
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowPhaseAsync(this.Definition.GameLevel, phase)).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowSkillPointsAsync((byte)(this.GetMember(player)?.Points ?? 0))).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnObjectRemovedFromMapAsync((GameMap Map, ILocateable Object) args)
    {
        if (args.Object is Player player)
        {
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                using (await this._matchLock.LockAsync().ConfigureAwait(false))
                {
                    if (player.SelectedCharacter is { } character)
                    {
                        this._channels.TryRemove(character.Id, out _);
                    }

                    if (this.IsMatchPlaying)
                    {
                        await this.DropRelicAsync(player).ConfigureAwait(false);
                    }

                    await this.RemovePlayerEventStateAsync(player).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        await base.OnObjectRemovedFromMapAsync(args).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask OnPlayerPickedUpItemAsync((Player Picker, ILocateable DroppedItem) args)
    {
        if (args.DroppedItem is not DroppedItem dropped || !IsRelic(dropped.Item))
        {
            return;
        }

        await args.Picker.RunPersistenceExclusiveAsync(async () =>
        {
            using (await this._matchLock.LockAsync().ConfigureAwait(false))
            {
                if (!this.IsMatchPlaying || this.GetMember(args.Picker) is null || this._holder is not null || dropped != this._relicDrop)
                {
                    if (args.Picker.Inventory?.GetItem(dropped.Item.ItemSlot) is { } item && IsRelic(item))
                    {
                        await args.Picker.DestroyInventoryItemAsync(item).ConfigureAwait(false);
                    }

                    return;
                }

                this._holder = args.Picker;
                this._relicDrop = null;
                await this.ApplyCarrierSlowAsync(args.Picker).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask GameEndedAsync(ICollection<Player> finishers)
    {
        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            this._channels.Clear();

            // The legacy result window treats scores below two as a loss for both teams.
            this._winningTeam = Math.Max(this._scores[0], this._scores[1]) < 2 || this._scores[0] == this._scores[1]
                ? byte.MaxValue : this._scores[0] > this._scores[1] ? (byte)0 : (byte)1;
        }

        foreach (var player in finishers)
        {
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                using (await this._matchLock.LockAsync().ConfigureAwait(false))
                {
                    await this.RemovePlayerEventStateAsync(player).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            await this.ClaimRewardAsync(player).ConfigureAwait(false);
        }

        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            this._holder = null;
            if (this._relicDrop is { } drop)
            {
                await drop.DisposeAsync().ConfigureAwait(false);
                this._relicDrop = null;
            }
        }

        var results = finishers.Where(player => this.GetMember(player) is not null)
            .Select(player => (player, this.GetTeam(player), this.GetMember(player)!.Experience)).ToList();
        foreach (var player in finishers)
        {
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowPhaseAsync(this.Definition.GameLevel, 3)).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowResultAsync(this._scores[0], this._scores[1], results)).ConfigureAwait(false);
        }

        await this.SaveRankingAsync(finishers.Where(player => this.GetMember(player) is not null)
            .Select(player => (this.GetTeam(player) == this._winningTeam ? 1 : 2, player.SelectedCharacter!, (int)this._scores[this.GetTeam(player)]))).ConfigureAwait(false);
        this.SetWaterOpen(false);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        using (await this._matchLock.LockAsync().ConfigureAwait(false))
        {
            this._channels.Clear();
        }

        foreach (var player in this._skins.Keys)
        {
            await player.RunPersistenceExclusiveAsync(async () =>
            {
                using (await this._matchLock.LockAsync().ConfigureAwait(false))
                {
                    await this.RemovePlayerEventStateAsync(player).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        foreach (var statue in this._statues.Where(statue => this.Map.GetObject(statue.Id) != statue))
        {
            statue.Dispose();
        }

        this.SetWaterOpen(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private static bool IsRelic(Item item) => item.Definition is { Group: 14, Number: 64 };

    private Member? GetMember(Player player) => player.SelectedCharacter is { } character
        && this._members.TryGetValue(character.Id, out var member) && member.Player == player ? member : null;

    private bool CanInteract(Player player, NonPlayerCharacter npc) => this.IsMatchPlaying && player.IsAlive
        && player.CurrentMiniGame == this && player.CurrentMap == this.Map && npc.CurrentMap == this.Map
        && this.Map.GetObject(npc.Id) == npc && player.GetDistanceTo(npc) <= 3 && !player.IsWalking
        && player.Attributes?[Stats.IsStunned] is not > 0 && this.GetMember(player) is not null
        && ((npc.Definition.Number == 380 && this._holder is null && this._relicDrop is null)
            || (npc.Definition.Number == 383 + this.GetTeam(player) && this._holder == player));

    private bool IsChannelValid(Guid characterId, Channel channel) => this._channels.TryGetValue(characterId, out var active) && active == channel
        && this.CanInteract(channel.Player, channel.Npc) && channel.Player.Position == channel.Position
        && channel.Player.Attributes![Stats.CurrentHealth] >= channel.Health && channel.Player.Attributes[Stats.CurrentShield] >= channel.Shield;

    private async Task RunChannelAsync(Guid characterId, Channel channel, CancellationToken cancellationToken)
    {
        try
        {
            var end = DateTime.UtcNow.Add(this._settings.CaptureDuration);
            while (this.IsChannelValid(characterId, channel) && DateTime.UtcNow < end)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            await channel.Player.RunPersistenceExclusiveAsync(
                async () =>
                {
                    using (await this._matchLock.LockAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (this.IsChannelValid(characterId, channel) && DateTime.UtcNow >= end)
                        {
                            if (channel.Npc.Definition.Number == 380)
                            {
                                await this.CaptureRelicAsync(channel.Player).ConfigureAwait(false);
                            }
                            else
                            {
                                await this.DepositRelicAsync(channel.Player).ConfigureAwait(false);
                            }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The match ended.
        }
        catch (Exception exception)
        {
            this.Logger.LogError(exception, "{context}: Illusion Temple interaction failed.", this);
        }
        finally
        {
            using (await this._matchLock.LockAsync().ConfigureAwait(false))
            {
                if (this._channels.TryGetValue(characterId, out var current) && current == channel)
                {
                    this._channels.TryRemove(characterId, out _);
                }
            }
        }
    }

    private async Task DropRelicAfterDeathAsync(Player player, CancellationToken cancellationToken)
    {
        try
        {
            await player.RunPersistenceExclusiveAsync(
                async () =>
                {
                    using (await this._matchLock.LockAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (this.IsMatchPlaying)
                        {
                            await this.DropRelicAsync(player).ConfigureAwait(false);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Event cleanup consumes any remaining relic instead of creating a new ground token.
        }
        catch (Exception exception)
        {
            this.Logger.LogError(exception, "{context}: Could not drop the dead participant's Illusion Temple relic.", this);
        }
    }

    private async ValueTask CaptureRelicAsync(Player player)
    {
        var definition = this.GameContext.Configuration.Items.Single(item => item.Group == 14 && item.Number == 64);
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = definition;
        item.Durability = 1;
        if (player.Inventory is null || !await player.Inventory.AddItemAsync(item).ConfigureAwait(false))
        {
            await player.PersistenceContext.DeleteAsync(item).ConfigureAwait(false);
            return;
        }

        this._holder = player;
        await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(view => view.ItemAppearAsync(item)).ConfigureAwait(false);
        await this.ApplyCarrierSlowAsync(player).ConfigureAwait(false);
        foreach (var statue in this._statues.ToArray())
        {
            await this.Map.RemoveAsync(statue).ConfigureAwait(false);
        }
    }

    private async ValueTask DepositRelicAsync(Player player)
    {
        if (!player.Inventory!.Items.Any(IsRelic))
        {
            return;
        }

        await this.RemoveRelicsAsync(player).ConfigureAwait(false);
        await this.RemoveEffectAsync(player, CarrierEffect).ConfigureAwait(false);
        this._holder = null;
        var team = this.GetTeam(player);
        this._scores[team]++;
        if (this._scores[team] >= this._settings.WinningScore)
        {
            this.FinishEvent();
            return;
        }

        await this.RestoreStatuesAsync().ConfigureAwait(false);
    }

    private async ValueTask RestoreStatuesAsync()
    {
        foreach (var statue in this._statues.ToArray())
        {
            await this.Map.AddAsync(statue).ConfigureAwait(false);
        }
    }

    private async ValueTask DropRelicAsync(Player player)
    {
        if (this._holder != player)
        {
            return;
        }

        var item = player.Inventory?.Items.FirstOrDefault(IsRelic);
        this._holder = null;
        await this.RemoveEffectAsync(player, CarrierEffect).ConfigureAwait(false);
        if (item is not null)
        {
            await player.DestroyInventoryItemAsync(item).ConfigureAwait(false);

            // Ground event tokens have no persistent owner; normal pickup creates a new inventory serial.
            var temporaryItem = new TemporaryItem { Definition = item.Definition, Durability = item.Durability };
            this._relicDrop = new DroppedItem(temporaryItem, player.Position, this.Map, null, null);
            await this.Map.AddAsync(this._relicDrop).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyCarrierSlowAsync(Player player) => await player.MagicEffectList.AddEffectAsync(new MagicEffect(
        this.Definition.GameDuration,
        new MagicEffectDefinition { Number = CarrierEffect },
        new MagicEffect.ElementWithTarget(new SimpleElement(this._settings.CarrierMovementFactor, AggregateType.Multiplicate), Stats.MovementSpeedFactor))).ConfigureAwait(false);

    private async ValueTask ApplyTimedEffectAsync(Player player, short effectNumber, ushort skill, params MagicEffect.ElementWithTarget[] powerUps)
    {
        var effect = new MagicEffect(this._settings.SkillDuration, new MagicEffectDefinition { Number = effectNumber, StopByDeath = true }, powerUps);
        effect.EffectTimeOut += _ => this.ForEachPlayerAsync(observer => observer.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowSkillEndAsync(skill, player)).AsTask());
        await player.MagicEffectList.AddEffectAsync(effect).ConfigureAwait(false);
    }

    private async ValueTask RemoveEventEffectsAsync(Player player)
    {
        foreach (var number in new short[] { ProtectionEffect, RestraintEffect, CarrierEffect })
        {
            await this.RemoveEffectAsync(player, number).ConfigureAwait(false);
        }
    }

    private async ValueTask RemoveEffectAsync(Player player, short number)
    {
        if (player.MagicEffectList.TryGetEffect(number, out var effect))
        {
            await effect.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask RemoveRelicsAsync(Player player)
    {
        foreach (var item in player.Inventory?.Items.Where(IsRelic).ToArray() ?? [])
        {
            await player.DestroyInventoryItemAsync(item).ConfigureAwait(false);
        }
    }

    private async ValueTask RemovePlayerEventStateAsync(Player player)
    {
        await this.RemoveEventEffectsAsync(player).ConfigureAwait(false);
        await this.RemoveRelicsAsync(player).ConfigureAwait(false);
        this.RemoveTeamSkin(player);
        if (this._holder == player)
        {
            this._holder = null;
        }
    }

    private void ApplyTeamSkin(Player player)
    {
        if (player.Attributes is { } attributes && this.GetTeam(player) is 0 or 1 && !this._skins.ContainsKey(player))
        {
            var skin = new SimpleElement(404 + this.GetTeam(player), AggregateType.AddRaw);
            if (this._skins.TryAdd(player, skin))
            {
                attributes.AddElement(skin, Stats.TransformationSkin);
            }
        }
    }

    private void RemoveTeamSkin(Player player)
    {
        if (this._skins.TryRemove(player, out var skin))
        {
            player.Attributes?.RemoveElement(skin, Stats.TransformationSkin);
        }
    }

    private void SetWaterOpen(bool open)
    {
        if (open && this._waterTiles.Count == 0)
        {
            for (var x = 0; x < 256; x++)
            {
                for (var y = 0; y < 256; y++)
                {
                    if ((this.Map.Terrain.AttributeMap[x, y] & (byte)TerrainAttributeType.Water) != 0)
                    {
                        this._waterTiles.Add((new Point((byte)x, (byte)y), this.Map.Terrain.AttributeMap[x, y]));
                    }
                }
            }
        }

        foreach (var (point, original) in this._waterTiles)
        {
            var attributes = open ? (byte)0 : original;
            this.Map.Terrain.ApplyTerrainAttribute(point.X, point.Y, TerrainAttributeType.Safezone, (attributes & (byte)TerrainAttributeType.Safezone) != 0);
            this.Map.Terrain.ApplyTerrainAttribute(point.X, point.Y, (TerrainAttributeType)0xFE, false);
            this.Map.Terrain.ApplyTerrainAttribute(point.X, point.Y, (TerrainAttributeType)(attributes & 0xFE), true);
        }
    }

    private async Task RunInfoLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                using (await this._matchLock.LockAsync().ConfigureAwait(false))
                {
                    // Pickup converts the temporary token before disposing its drop and invoking the player callback.
                    if (this.IsMatchPlaying && this._relicDrop is { IsDisposed: true }
                        && !this._members.Values.Any(member => member.Player.Inventory?.Items.Any(IsRelic) == true)
                        && !this.Map.GetPlayers().Any(player => player.Inventory?.Items.Any(IsRelic) == true))
                    {
                        this._relicDrop = null;
                        await this.RestoreStatuesAsync().ConfigureAwait(false);
                    }
                }

                var players = this.Map.GetPlayers().Where(player => this.GetMember(player) is not null).ToArray();
                foreach (var player in players)
                {
                    var team = this.GetTeam(player);
                    await player.InvokeViewPlugInAsync<IIllusionTempleViewPlugIn>(view => view.ShowStateAsync(
                        this.RemainingTime,
                        this._holder,
                        this._holder?.Position ?? this._relicDrop?.Position,
                        this._scores[0],
                        this._scores[1],
                        team,
                        players.Where(member => this.GetTeam(member) == team).ToArray())).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(this.GameEndedToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The match ended.
        }
        catch (Exception exception)
        {
            this.Logger.LogError(exception, "{context}: Illusion Temple state update failed.", this);
        }
    }

    private sealed class Member(Player player, byte team)
    {
        public Player Player { get; set; } = player;

        public byte Team { get; } = team;

        public int Points { get; set; }

        public int Experience { get; set; }

        public bool RewardClaimed { get; set; }
    }

    private sealed record Channel(Player Player, NonPlayerCharacter Npc, Point Position, float Health, float Shield);
}
