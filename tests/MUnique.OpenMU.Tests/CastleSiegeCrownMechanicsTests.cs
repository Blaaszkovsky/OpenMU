// <copyright file="CastleSiegeCrownMechanicsTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.CastleSiege.Actions;
using MUnique.OpenMU.GameLogic.CastleSiege.Intelligence;
using MUnique.OpenMU.GameLogic.CastleSiege.NPC;
using MUnique.OpenMU.GameLogic.Views.CastleSiege;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;
using RuntimeGuild = MUnique.OpenMU.Interfaces.Guild;

/// <summary>
/// Tests Castle Siege Crown capture, switch state, and final ownership mechanics.
/// </summary>
[TestFixture]
public class CastleSiegeCrownMechanicsTests
{
    private const uint DefenseGuildId = 10;
    private const uint AttackGuildId = 20;

    /// <summary>
    /// Verifies Crown operation from the nearest walkable tile outside the Season 6 Crown footprint.
    /// </summary>
    /// <param name="distance">The master's distance from the Crown.</param>
    /// <param name="canOperate">Whether the position is within operating range.</param>
    [TestCase(2, true)]
    [TestCase(3, false)]
    public async ValueTask CrownOperationRespectsClientFootprintAndRangeAsync(byte distance, bool canOperate)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.Configuration.CrownHoldTimeSeconds = 30;
        var players = new List<Player>();
        try
        {
            await fixture.Context.NpcController.PrepareAsync().ConfigureAwait(false);
            var npcs = fixture.Context.NpcController.GetRuntimeSnapshot().Select(runtime => runtime.SpawnedInstance).ToList();
            var crown = npcs.OfType<CastleSiegeCrown>().Single();
            var switches = npcs.OfType<CastleSiegeSwitch>().OrderBy(siegeSwitch => siegeSwitch.SwitchIndex).ToList();

            // EncTerrain312.att at Crown176/212 blocks these relative tiles; the nearest approach is two tiles away.
            for (var y = -1; y <= 1; y++)
            {
                var width = y == 0 ? 3 : 2;
                for (var x = -width; x <= width; x++)
                {
                    fixture.SiegeMap.Terrain.ApplyTerrainAttribute((byte)(60 + x), (byte)(60 + y), TerrainAttributeType.Blocked, true);
                }
            }

            var master = await fixture.CreatePlayerAsync(AttackGuildId, "CrownUser", CastleSiegeJoinSide.Attack1, 60, (byte)(60 - distance)).ConfigureAwait(false);
            players.Add(master);
            master.GuildStatus = new GuildMemberStatus(AttackGuildId, GuildPosition.GuildMaster);
            players.Add(await fixture.CreatePlayerAsync(AttackGuildId, "SwitchOne", CastleSiegeJoinSide.Attack1, 70, 61).ConfigureAwait(false));
            players.Add(await fixture.CreatePlayerAsync(AttackGuildId, "SwitchTwo", CastleSiegeJoinSide.Attack1, 80, 61).ConfigureAwait(false));
            Assert.Multiple(() =>
            {
                Assert.That(fixture.SiegeMap.Terrain.WalkMap[60, 59], Is.False);
                Assert.That(fixture.SiegeMap.Terrain.WalkMap[60, 60 - distance], Is.True);
            });

            using var crownIntelligence = new CastleSiegeCrownIntelligence(fixture.Context) { Npc = crown };
            using var firstSwitchIntelligence = new CastleSiegeSwitchIntelligence(fixture.Context) { Npc = switches[0] };
            using var secondSwitchIntelligence = new CastleSiegeSwitchIntelligence(fixture.Context) { Npc = switches[1] };
            await firstSwitchIntelligence.TickAsync().ConfigureAwait(false);
            await secondSwitchIntelligence.TickAsync().ConfigureAwait(false);
            await crownIntelligence.TickAsync().ConfigureAwait(false);
            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);
            Assert.That(fixture.Context.SwitchUsers, Is.EqualTo(players.Skip(1)));
            Assert.That(fixture.Context.IsCrownAvailable, Is.True);
            Assert.That(fixture.Context.CrownUser, canOperate ? Is.SameAs(master) : Is.Null);

            for (var second = 1; second < 30; second++)
            {
                await fixture.CheckCrownAsync().ConfigureAwait(false);
                Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId), "The complete 30-second hold is required.");
            }

            await fixture.CheckCrownAsync().ConfigureAwait(false);
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(canOperate ? fixture.AttackPersistentGuildId : fixture.DefensePersistentGuildId));
        }
        finally
        {
            foreach (var player in players)
            {
                await fixture.GameServerContext.RemovePlayerAsync(player).ConfigureAwait(false);
            }

            await fixture.Context.NpcController.DespawnAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that battle transitions switch the terrain of players already on the siege map.
    /// </summary>
    [Test]
    public async ValueTask BattleTransitionsSynchronizeClientBattleModeAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        plugIn.ForceState(CastleSiegeState.RegisterGuild);
        await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
        var player = await fixture.CreatePlayerAsync(AttackGuildId, "Attacker", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
        player.GuildStatus = new GuildMemberStatus(AttackGuildId, GuildPosition.GuildMaster);
        player.Attributes![Stats.Level] = 400;
        await new CastleSiegeRegisterGuildAction().RegisterAsync(player, fixture.Context).ConfigureAwait(false);
        Assert.That(fixture.Context.RegisteredGuilds, Contains.Key(fixture.AttackPersistentGuildId));
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<ICastleSiegeStatusPlugIn>()!);
        try
        {
            view.Invocations.Clear();
            plugIn.ForceState(CastleSiegeState.Ready);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowBattleStateAsync(false), Times.AtLeastOnce);

            view.Invocations.Clear();
            plugIn.ForceStart();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.That(fixture.Context.CurrentState, Is.EqualTo(CastleSiegeState.Start));
            view.Verify(p => p.ShowBattleStateAsync(true), Times.AtLeastOnce);
            view.Verify(p => p.ShowBattleStateAsync(false), Times.Never);

            view.Invocations.Clear();
            plugIn.ForceState(CastleSiegeState.End);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowBattleStateAsync(false), Times.Once);
            view.Invocations.Clear();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowBattleStateAsync(It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies battle mode synchronization on map entry and after the client's map-ready handshake.
    /// </summary>
    /// <param name="state">The current server siege state.</param>
    /// <param name="isStarted">The battle mode the client must receive.</param>
    [TestCase(CastleSiegeState.Ready, false)]
    [TestCase(CastleSiegeState.Start, true)]
    [TestCase(CastleSiegeState.End, false)]
    public async ValueTask EnteringAndRejoiningSiegeMapSynchronizesBattleModeAsync(CastleSiegeState state, bool isStarted)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        plugIn.ForceState(state);
        await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
        var player = await fixture.CreatePlayerAsync(AttackGuildId, "LateJoin", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<ICastleSiegeStatusPlugIn>()!);
        try
        {
            view.Verify(p => p.ShowBattleStateAsync(isStarted), Times.AtLeastOnce);
            view.Verify(p => p.ShowBattleStateAsync(!isStarted), Times.Never);

            view.Invocations.Clear();
            await plugIn.PlayerStateChangedAsync(player, PlayerState.ChangingMap, PlayerState.EnteredWorld).ConfigureAwait(false);
            view.Verify(p => p.ShowBattleStateAsync(isStarted), Times.Once);

            await player.WarpToAsync(new ExitGate
            {
                Map = fixture.GameServerContext.Configuration.Maps.Single(map => map.Number == 0),
                X1 = 10,
                X2 = 10,
                Y1 = 10,
                Y2 = 10,
                Direction = Direction.South,
            }).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            view.Invocations.Clear();
            await plugIn.ObjectAddedToMapAsync(player.CurrentMap!, player).ConfigureAwait(false);
            await plugIn.PlayerStateChangedAsync(player, PlayerState.ChangingMap, PlayerState.EnteredWorld).ConfigureAwait(false);
            view.Verify(p => p.ShowBattleStateAsync(It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies the battle timer starts after battle mode and follows the context clock on every battle tick.
    /// </summary>
    [Test]
    public async ValueTask BattleTimerFollowsTransitionsAndClockAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        var player = await fixture.CreatePlayerAsync(AttackGuildId, "TimerUser", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<ICastleSiegeStatusPlugIn>()!);
        try
        {
            view.Invocations.Clear();
            plugIn.ForceStart();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            var initialRemaining = fixture.Context.RemainingTime;
            Assert.That(initialRemaining, Is.GreaterThan(TimeSpan.Zero));
            view.Verify(p => p.ShowRemainingTimeAsync(initialRemaining), Times.AtLeastOnce);
            var calls = view.Invocations.ToList();
            Assert.That(calls.FindIndex(call => call.Method.Name == nameof(ICastleSiegeStatusPlugIn.ShowBattleStateAsync)),
                Is.LessThan(calls.FindIndex(call => call.Method.Name == nameof(ICastleSiegeStatusPlugIn.ShowRemainingTimeAsync))), "Create battle UI before sending its timer.");

            view.Invocations.Clear();
            clock.Advance(TimeSpan.FromMinutes(1));
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(initialRemaining - TimeSpan.FromMinutes(1)), Times.Once);
            view.Verify(p => p.ShowBattleStateAsync(It.IsAny<bool>()), Times.Never);

            view.Invocations.Clear();
            plugIn.ForceState(CastleSiegeState.End);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(TimeSpan.Zero), Times.Once);
            view.Invocations.Clear();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(It.IsAny<TimeSpan>()), Times.Never);
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a pending HUD send cannot hold up the Crown tick, even when the send eventually fails.
    /// </summary>
    [Test]
    public async ValueTask PendingBattleTimerDoesNotDelayCrownTickAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        var player = await fixture.CreatePlayerAsync(AttackGuildId, "SlowTimer", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
        try
        {
            plugIn.ForceStart();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = Mock.Get(player.ViewPlugIns.GetPlugIn<ICastleSiegeStatusPlugIn>()!);
            view.Setup(p => p.ShowRemainingTimeAsync(It.IsAny<TimeSpan>())).Returns(() =>
            {
                entered.TrySetResult(true);
                return new ValueTask(pending.Task);
            });
            clock.Advance(TimeSpan.FromSeconds(1));
            var tick = plugIn.ExecuteTaskAsync(fixture.GameServerContext).AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.That(fixture.Context.LastCrownUpdateUtc, Is.EqualTo(clock.GetUtcNow().UtcDateTime),
                    "Crown logic must run before awaiting a cosmetic HUD send.");
            }
            finally
            {
                pending.TrySetException(new IOException("The pending HUD connection failed."));
                await tick.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies late entry, map-ready rejoining and the map/world audience boundary of the timer.
    /// </summary>
    /// <param name="state">The current siege phase.</param>
    [TestCase(CastleSiegeState.Ready)]
    [TestCase(CastleSiegeState.Start)]
    [TestCase(CastleSiegeState.End)]
    public async ValueTask BattleTimerSynchronizesEntryAndRejoinAsync(CastleSiegeState state)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        plugIn.ForceState(state);
        await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(40));
        var player = await fixture.CreatePlayerAsync(AttackGuildId, "LateTimer", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<ICastleSiegeStatusPlugIn>()!);
        var expected = state == CastleSiegeState.Start ? fixture.Context.RemainingTime : TimeSpan.Zero;
        try
        {
            view.Verify(p => p.ShowRemainingTimeAsync(expected), Times.AtLeastOnce);
            view.Invocations.Clear();
            await plugIn.PlayerStateChangedAsync(player, PlayerState.ChangingMap, PlayerState.EnteredWorld).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(expected), Times.Once);

            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.CharacterSelection));
            Assert.That(player.SelectedCharacter, Is.Not.Null, "A stale selected character must not bypass the world-state guard.");
            view.Invocations.Clear();
            await plugIn.ObjectAddedToMapAsync(fixture.SiegeMap, player).ConfigureAwait(false);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(It.IsAny<TimeSpan>()), Times.Never);

            await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
            await player.WarpToAsync(new ExitGate
            {
                Map = fixture.GameServerContext.Configuration.Maps.Single(map => map.Number == 0),
                X1 = 10,
                X2 = 10,
                Y1 = 10,
                Y2 = 10,
                Direction = Direction.South,
            }).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            view.Invocations.Clear();
            await plugIn.ObjectAddedToMapAsync(player.CurrentMap!, player).ConfigureAwait(false);
            await plugIn.PlayerStateChangedAsync(player, PlayerState.ChangingMap, PlayerState.EnteredWorld).ConfigureAwait(false);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            view.Verify(p => p.ShowRemainingTimeAsync(It.IsAny<TimeSpan>()), Times.Never);
        }
        finally
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies registration, selection, proximity-based capture, final rewards and the next registration cycle.
    /// </summary>
    [Test]
    public async ValueTask RegisteredGuildCapturesCrownAndCompletesServerCycleAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(clock);
        var fixture = await CreateFixtureAsync(plugIn).ConfigureAwait(false);
        var players = new List<Player>();
        try
        {
            plugIn.ForceState(CastleSiegeState.RegisterGuild);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            var crownUser = await fixture.CreatePlayerAsync(AttackGuildId, "CrownUser", CastleSiegeJoinSide.None, 60, 60, plugIn).ConfigureAwait(false);
            players.Add(crownUser);
            crownUser.GuildStatus = new GuildMemberStatus(AttackGuildId, GuildPosition.GuildMaster);
            crownUser.Attributes![Stats.Level] = 400;
            await new CastleSiegeRegisterGuildAction().RegisterAsync(crownUser, fixture.Context).ConfigureAwait(false);
            Assert.That(fixture.Context.RegisteredGuilds, Contains.Key(fixture.AttackPersistentGuildId));

            plugIn.ForceState(CastleSiegeState.RegisterMark);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            var mark = crownUser.PersistenceContext.CreateNew<Item>();
            mark.Definition = fixture.Configuration.SignOfLordItemDefinition;
            mark.Level = fixture.Configuration.SignOfLordItemLevel;
            await crownUser.Inventory!.AddItemAsync(20, mark).ConfigureAwait(false);
            await new CastleSiegeRegisterMarkAction().RegisterMarkAsync(crownUser, fixture.Context, 20).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(crownUser.Inventory.GetItem(20), Is.Null);
                Assert.That(fixture.Context.RegisteredGuilds[fixture.AttackPersistentGuildId].Marks, Is.EqualTo(1));
            });

            plugIn.ForceState(CastleSiegeState.Notify);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.FinalGuildList[AttackGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
                Assert.That(fixture.Context.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            });

            plugIn.ForceState(CastleSiegeState.Ready);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.That(fixture.Context.NpcController.GetRuntimeSnapshot().Count(runtime => runtime.SpawnedInstance is not null), Is.EqualTo(3));
            plugIn.ForceStart();
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.That(fixture.Context.CurrentState, Is.EqualTo(CastleSiegeState.Start));
            fixture.Context.SiegeData.TaxChaos = 3;
            fixture.Context.SiegeData.TributeMoney = 6_000;
            await crownUser.WarpToAsync(new ExitGate
            {
                Map = fixture.Configuration.CastleSiegeMapDefinition,
                X1 = 60,
                X2 = 60,
                Y1 = 60,
                Y2 = 60,
                Direction = Direction.South,
            }).ConfigureAwait(false);
            await crownUser.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            players.Add(await fixture.CreatePlayerAsync(AttackGuildId, "SwitchOne", CastleSiegeJoinSide.None, 70, 60, plugIn).ConfigureAwait(false));
            players.Add(await fixture.CreatePlayerAsync(AttackGuildId, "SwitchTwo", CastleSiegeJoinSide.None, 80, 60, plugIn).ConfigureAwait(false));
            var npcs = fixture.Context.NpcController.GetRuntimeSnapshot().Select(runtime => runtime.SpawnedInstance).ToList();
            var crown = npcs.OfType<CastleSiegeCrown>().Single();
            var switches = npcs.OfType<CastleSiegeSwitch>().OrderBy(siegeSwitch => siegeSwitch.SwitchIndex).ToList();
            using var crownIntelligence = new CastleSiegeCrownIntelligence(fixture.Context) { Npc = crown };
            using var firstSwitchIntelligence = new CastleSiegeSwitchIntelligence(fixture.Context) { Npc = switches[0] };
            using var secondSwitchIntelligence = new CastleSiegeSwitchIntelligence(fixture.Context) { Npc = switches[1] };
            await crownIntelligence.TickAsync().ConfigureAwait(false);
            await firstSwitchIntelligence.TickAsync().ConfigureAwait(false);
            await secondSwitchIntelligence.TickAsync().ConfigureAwait(false);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.CrownUser, Is.SameAs(crownUser));
                Assert.That(fixture.Context.SwitchUsers, Is.EqualTo(players.Skip(1)));
                Assert.That(fixture.Context.IsCrownAvailable, Is.True);
                Assert.That(crown.State, Is.EqualTo(CastleSiegeCrownState.Idle));
            });

            for (var tick = 0; tick < fixture.Configuration.CrownHoldTimeSeconds; tick++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            }

            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.MiddleOwnerGuildId, Is.EqualTo(AttackGuildId));
                Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
                Assert.That(fixture.Context.SiegeData.TaxChaos, Is.Zero);
                Assert.That(fixture.Context.SiegeData.TributeMoney, Is.Zero);
                Assert.That(fixture.Context.GetPlayerJoinSide(crownUser), Is.EqualTo(CastleSiegeJoinSide.Defense));
                Assert.That(fixture.Context.CrownUser, Is.Null);
                Assert.That(fixture.Context.SwitchUsers, Is.All.Null);
            });
            Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
                .Verify(view => view.ShowCrownAccessStateAsync(CastleSiegeCrownAccessState.Success, TimeSpan.FromSeconds(3)), Times.Once);

            clock.Advance(TimeSpan.FromSeconds(5));
            plugIn.ForceState(CastleSiegeState.End);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.That(fixture.Context.CurrentState, Is.EqualTo(CastleSiegeState.End));
            Assert.That(fixture.Context.ParticipantTracking, Is.Empty);
            foreach (var player in players)
            {
                Assert.That(player.Inventory!.Items.Count(item => item.Definition == fixture.Configuration.RewardItemDefinition), Is.EqualTo(1));
            }

            Mock.Get(fixture.GameServerContext.GuildServer)
                .Verify(server => server.IncreaseGuildScoreAsync(AttackGuildId, 10), Times.Once);
            using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, fixture.GameServerContext.Configuration))
            {
                var data = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
                Assert.That(data.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
                Assert.That(data.TaxChaos, Is.Zero);
                Assert.That(data.TributeMoney, Is.Zero);
                Assert.That(data.Guilds.Single(guild => guild.GuildId == fixture.AttackPersistentGuildId).Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            }

            plugIn.ForceState(CastleSiegeState.EndCycle);
            await plugIn.ExecuteTaskAsync(fixture.GameServerContext).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.CurrentState, Is.EqualTo(CastleSiegeState.RegisterGuild));
                Assert.That(fixture.Context.RegisteredGuilds, Is.Empty);
                Assert.That(fixture.Context.FinalGuildList, Is.Empty);
                Assert.That(fixture.Context.PlayerJoinSides, Is.Empty);
                Assert.That(fixture.Context.NpcController.GetRuntimeSnapshot(), Is.Empty);
                Assert.That(fixture.Context.MiddleOwnerGuildId, Is.Null);
            });
            using var finalPersistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(typeof(CastleSiegeData), false, fixture.GameServerContext.Configuration);
            var finalData = (await finalPersistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
            Assert.That(finalData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
            Assert.That(finalData.Guilds, Is.Empty);
            Assert.That(await finalPersistenceContext.GetAsync<CastleSiegeGuildRegistration>().ConfigureAwait(false), Is.Empty);
        }
        finally
        {
            foreach (var player in players)
            {
                await fixture.GameServerContext.RemovePlayerAsync(player).ConfigureAwait(false);
            }

            await fixture.Context.NpcController.DespawnAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that an interrupted attempt keeps capped progress and reports the failure once.
    /// </summary>
    [Test]
    public async ValueTask InterruptedCaptureKeepsCappedProgressAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "CrownUser",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchOne",
                CastleSiegeJoinSide.Attack1,
                70,
                60)
            .ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchTwo",
                CastleSiegeJoinSide.Attack1,
                80,
                60)
            .ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = firstSwitchUser;
        fixture.Context.SwitchUsers[1] = secondSwitchUser;

        await fixture.CheckCrownAsync().ConfigureAwait(false);
        fixture.Context.CrownAccumulatedTime = TimeSpan.FromSeconds(10);
        fixture.Context.SwitchUsers[1] = null;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        await fixture.CheckCrownAsync().ConfigureAwait(false);

        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Fail,
                    TimeSpan.FromSeconds(2)),
                Times.Once);
    }

    /// <summary>
    /// Verifies that only three alive and guilded players on the same attacking side can make progress.
    /// </summary>
    [Test]
    public async ValueTask CaptureRequiresThreeEligiblePlayersOnSameAttackingSideAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "CrownUser",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchOne",
                CastleSiegeJoinSide.Attack1,
                70,
                60)
            .ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchTwo",
                CastleSiegeJoinSide.Attack2,
                80,
                60)
            .ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = firstSwitchUser;
        fixture.Context.SwitchUsers[1] = secondSwitchUser;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.Zero), "Different attacking sides must not capture.");

        fixture.Context.PlayerJoinSides[secondSwitchUser.SelectedCharacter!.Id] = CastleSiegeJoinSide.Attack1;
        secondSwitchUser.IsAlive = false;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.Zero), "Dead switch users must not capture.");

        secondSwitchUser.IsAlive = true;
        secondSwitchUser.GuildStatus = null;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.Zero), "Guildless switch users must not capture.");

        secondSwitchUser.GuildStatus = new GuildMemberStatus(AttackGuildId, GuildPosition.NormalMember);
        fixture.Context.PlayerJoinSides[crownUser.SelectedCharacter!.Id] = CastleSiegeJoinSide.Defense;
        fixture.Context.PlayerJoinSides[firstSwitchUser.SelectedCharacter!.Id] = CastleSiegeJoinSide.Defense;
        fixture.Context.PlayerJoinSides[secondSwitchUser.SelectedCharacter.Id] = CastleSiegeJoinSide.Defense;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.Zero), "The defending side must not capture.");

        fixture.Context.PlayerJoinSides[crownUser.SelectedCharacter.Id] = CastleSiegeJoinSide.Attack1;
        fixture.Context.PlayerJoinSides[firstSwitchUser.SelectedCharacter.Id] = CastleSiegeJoinSide.Attack1;
        fixture.Context.PlayerJoinSides[secondSwitchUser.SelectedCharacter.Id] = CastleSiegeJoinSide.Attack1;
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Verifies that changing the Crown user fails the previous attempt without resetting its progress.
    /// </summary>
    [Test]
    public async ValueTask ChangedCrownUserContinuesCappedProgressAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var previousCrownUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "PreviousUser",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var newCrownUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "NewUser",
                CastleSiegeJoinSide.Attack1,
                61,
                60)
            .ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchOne",
                CastleSiegeJoinSide.Attack1,
                70,
                60)
            .ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchTwo",
                CastleSiegeJoinSide.Attack1,
                80,
                60)
            .ConfigureAwait(false);
        fixture.Context.CrownUser = previousCrownUser;
        fixture.Context.SwitchUsers[0] = firstSwitchUser;
        fixture.Context.SwitchUsers[1] = secondSwitchUser;
        await fixture.CheckCrownAsync().ConfigureAwait(false);

        fixture.Context.CrownUser = newCrownUser;
        await fixture.CheckCrownAsync().ConfigureAwait(false);

        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Mock.Get(previousCrownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Fail,
                    TimeSpan.FromSeconds(1)),
                Times.Once);
        Mock.Get(newCrownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Attempt,
                    TimeSpan.FromSeconds(2)),
                Times.Once);
    }

    /// <summary>
    /// Verifies a successful capture, side swap, participant update, respawn, and restart recovery.
    /// </summary>
    [Test]
    public async ValueTask SuccessfulCaptureChangesIntermediateOwnerAndSwapsSidesAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "CrownUser",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchOne",
                CastleSiegeJoinSide.Attack1,
                70,
                60)
            .ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "SwitchTwo",
                CastleSiegeJoinSide.Attack1,
                80,
                60)
            .ConfigureAwait(false);
        var formerDefender = await fixture.CreatePlayerAsync(
                DefenseGuildId,
                "Defender",
                CastleSiegeJoinSide.Defense,
                200,
                200)
            .ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = firstSwitchUser;
        fixture.Context.SwitchUsers[1] = secondSwitchUser;
        fixture.Context.IsCrownAvailable = true;
        fixture.Context.FinalGuildList[AttackGuildId].IsAllianceMaster = false;

        await fixture.CheckCrownAsync().ConfigureAwait(false);
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        await fixture.CheckCrownAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.MiddleOwnerGuildId, Is.EqualTo(AttackGuildId));
            Assert.That(fixture.Context.FinalGuildList[AttackGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.GetPlayerJoinSide(crownUser), Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.GetPlayerJoinSide(formerDefender), Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.IsCrownAvailable, Is.False);
            Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.Zero));
            Assert.That(fixture.Context.CrownUser, Is.Null);
            Assert.That(fixture.Context.SwitchUsers, Is.All.Null);
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
            Assert.That(formerDefender.Position.X, Is.InRange((byte)35, (byte)40));
            Assert.That(formerDefender.Position.Y, Is.InRange((byte)11, (byte)16));
        });
        Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Success,
                    TimeSpan.FromSeconds(3)),
                Times.Once);
        Mock.Get(formerDefender.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowOwnershipChangeAsync("Attackers"),
                Times.Once);

        using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
                   typeof(CastleSiegeData),
                   false,
                   fixture.GameServerContext.Configuration))
        {
            var persistedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
            Assert.That(persistedData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
        }

        var restartedContext = new CastleSiegeContext(fixture.GameServerContext, fixture.Configuration);
        await restartedContext.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(restartedContext.FinalGuildList[AttackGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(restartedContext.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(restartedContext.MiddleOwnerGuildId, Is.EqualTo(AttackGuildId));
        });
    }

    /// <summary>
    /// Verifies that a Crown seal by a member of an attacking alliance hands the castle to the alliance master guild,
    /// because the tax exemption and the owner alliance checks compare against the alliance master.
    /// </summary>
    [Test]
    public async ValueTask CaptureByAllianceMemberTransfersOwnershipToAllianceMasterAsync()
    {
        const uint allianceMemberGuildId = 21;
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.Context.FinalGuildList[allianceMemberGuildId] = new CastleSiegeGuildParticipant
        {
            GuildId = allianceMemberGuildId,
            PersistentGuildId = Guid.NewGuid(),
            GuildName = "AttackerAllies",
            Side = CastleSiegeJoinSide.Attack1,
            IsAllianceMaster = false,
        };
        var crownUser = await fixture.CreatePlayerAsync(
                allianceMemberGuildId,
                "AllyCrownUser",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(
                allianceMemberGuildId,
                "AllySwitchOne",
                CastleSiegeJoinSide.Attack1,
                70,
                60)
            .ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "MasterSwitchTwo",
                CastleSiegeJoinSide.Attack1,
                80,
                60)
            .ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = firstSwitchUser;
        fixture.Context.SwitchUsers[1] = secondSwitchUser;
        fixture.Context.IsCrownAvailable = true;

        await fixture.CheckCrownAsync().ConfigureAwait(false);
        await fixture.CheckCrownAsync().ConfigureAwait(false);
        await fixture.CheckCrownAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.MiddleOwnerGuildId, Is.EqualTo(AttackGuildId));
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
            Assert.That(fixture.Context.FinalGuildList[AttackGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[allianceMemberGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
        });
        Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowOwnershipChangeAsync("Attackers"),
                Times.Once);
    }

    /// <summary>
    /// Verifies switch occupant broadcasts and Crown lock-state calculation.
    /// </summary>
    [Test]
    public async ValueTask SwitchInformationControlsCrownAvailabilityAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            await fixture.Context.NpcController.PrepareAsync().ConfigureAwait(false);
            var firstSwitch = fixture.Context.ActiveNpcs
                .Select(runtime => runtime.SpawnedInstance)
                .OfType<CastleSiegeSwitch>()
                .Single(siegeSwitch => siegeSwitch.SwitchIndex == 0);
            var crown = fixture.Context.ActiveNpcs
                .Select(runtime => runtime.SpawnedInstance)
                .OfType<CastleSiegeCrown>()
                .Single();
            var firstSwitchUser = await fixture.CreatePlayerAsync(
                    AttackGuildId,
                    "SwitchOne",
                    CastleSiegeJoinSide.Attack1,
                    firstSwitch.Position.X,
                    firstSwitch.Position.Y)
                .ConfigureAwait(false);
            var secondSwitch = fixture.Context.ActiveNpcs
                .Select(runtime => runtime.SpawnedInstance)
                .OfType<CastleSiegeSwitch>()
                .Single(siegeSwitch => siegeSwitch.SwitchIndex == 1);
            var secondSwitchUser = await fixture.CreatePlayerAsync(
                    AttackGuildId,
                    "SwitchTwo",
                    CastleSiegeJoinSide.Attack1,
                    secondSwitch.Position.X,
                    secondSwitch.Position.Y)
                .ConfigureAwait(false);
            fixture.Context.SwitchUsers[0] = firstSwitchUser;
            fixture.Context.SwitchUsers[1] = secondSwitchUser;

            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.IsCrownAvailable, Is.True);
                Assert.That(crown.State, Is.EqualTo(CastleSiegeCrownState.Idle));
            });
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeSwitchInfoPlugIn>()!)
                .Verify(
                    plugIn => plugIn.ShowSwitchInfoAsync(
                        It.Is<CastleSiegeSwitchInfo>(info =>
                            info.ObjectId == firstSwitch.Id
                            && info.IsOccupied
                            && info.JoinSide == CastleSiegeJoinSide.Attack1
                            && info.GuildName == "Attackers"
                            && info.CharacterName == "SwitchOne")),
                    Times.Once);
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownStatePlugIn>()!)
                .Verify(plugIn => plugIn.ShowCrownStateAsync(true), Times.Once);

            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeSwitchInfoPlugIn>()!)
                .Verify(plugIn => plugIn.ShowSwitchInfoAsync(It.IsAny<CastleSiegeSwitchInfo>()), Times.Exactly(2));
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownStatePlugIn>()!)
                .Verify(plugIn => plugIn.ShowCrownStateAsync(true), Times.Once);

            var enteringPlayer = await fixture.CreatePlayerAsync(
                    AttackGuildId,
                    "EnteringPlayer",
                    CastleSiegeJoinSide.Attack1,
                    90,
                    60)
                .ConfigureAwait(false);
            fixture.Context.IsCrownAvailable = false;
            crown.State = CastleSiegeCrownState.Locked;
            await CastleSiegeSwitchMechanics
                .SynchronizePlayerAsync(fixture.Context, enteringPlayer)
                .ConfigureAwait(false);
            Mock.Get(enteringPlayer.ViewPlugIns.GetPlugIn<ICastleSiegeSwitchInfoPlugIn>()!)
                .Verify(plugIn => plugIn.ShowSwitchInfoAsync(It.IsAny<CastleSiegeSwitchInfo>()), Times.Exactly(2));
            Mock.Get(enteringPlayer.ViewPlugIns.GetPlugIn<ICastleSiegeCrownStatePlugIn>()!)
                .Verify(plugIn => plugIn.ShowCrownStateAsync(true), Times.Once);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.IsCrownAvailable, Is.False);
                Assert.That(crown.State, Is.EqualTo(CastleSiegeCrownState.Locked));
            });

            fixture.Context.PlayerJoinSides[secondSwitchUser.SelectedCharacter!.Id] = CastleSiegeJoinSide.Attack2;
            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.IsCrownAvailable, Is.False);
                Assert.That(crown.State, Is.EqualTo(CastleSiegeCrownState.Locked));
            });
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownStatePlugIn>()!)
                .Verify(plugIn => plugIn.ShowCrownStateAsync(false), Times.Once);
            Mock.Get(firstSwitchUser.ViewPlugIns.GetPlugIn<ICastleSiegeSwitchInfoPlugIn>()!)
                .Verify(plugIn => plugIn.ShowSwitchInfoAsync(It.IsAny<CastleSiegeSwitchInfo>()), Times.Exactly(3));
        }
        finally
        {
            await fixture.Context.NpcController.DespawnAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that duplicate configured switch spawns do not overflow the broadcast snapshot.
    /// </summary>
    [Test]
    public async ValueTask DuplicateSwitchSpawnsAreTrackedByObjectIdAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var firstSwitchDefinition = fixture.Configuration.NpcDefinitions
            .Single(definition => definition.MonsterDefinition?.Number == CastleSiegeSwitch.FirstMonsterNumber);
        fixture.Configuration.NpcDefinitions.Add(new BasicModel.CastleSiegeNpcDefinition
        {
            MonsterDefinition = firstSwitchDefinition.MonsterDefinition,
            InstanceId = 3,
            SpawnX = 71,
            SpawnY = 60,
            Direction = Direction.South,
        });

        try
        {
            await fixture.Context.NpcController.PrepareAsync().ConfigureAwait(false);
            var observer = await fixture.CreatePlayerAsync(
                    AttackGuildId,
                    "Observer",
                    CastleSiegeJoinSide.Attack1,
                    90,
                    60)
                .ConfigureAwait(false);

            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);
            await CastleSiegeSwitchMechanics.SendSwitchInfoAsync(fixture.Context).ConfigureAwait(false);

            Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeSwitchInfoPlugIn>()!)
                .Verify(
                    plugIn => plugIn.ShowSwitchInfoAsync(It.IsAny<CastleSiegeSwitchInfo>()),
                    Times.Exactly(3));
            Assert.That(fixture.Context.LastBroadcastSwitchInfos, Has.Count.EqualTo(3));
        }
        finally
        {
            await fixture.Context.NpcController.DespawnAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a new owner is persisted and the Castle Siege economy is reset.
    /// </summary>
    [Test]
    public async ValueTask FinalResultPersistsWinnerAndResetsEconomyAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var observer = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "Observer",
                CastleSiegeJoinSide.Attack1,
                100,
                100)
            .ConfigureAwait(false);
        fixture.Context.SiegeData.TaxChaos = 3;
        fixture.Context.SiegeData.TaxStore = 3;
        fixture.Context.SiegeData.TaxHunt = 300_000;
        fixture.Context.SiegeData.TributeMoney = 6_000;
        fixture.Context.SiegeData.IsHuntZoneEnabled = true;
        await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(fixture.Context, observer, CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!).Invocations.Clear();

        await CastleSiegeCrownMechanics
            .CheckResultAsync(fixture.Context)
            .ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
            Assert.That(fixture.Context.SiegeData.IsOccupied, Is.True);
            Assert.That(fixture.Context.SiegeData.TaxChaos, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TaxStore, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TaxHunt, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TributeMoney, Is.Zero);
            Assert.That(fixture.Context.SiegeData.IsHuntZoneEnabled, Is.False);
        });
        using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
                   typeof(CastleSiegeData),
                   false,
                   fixture.GameServerContext.Configuration))
        {
            var persistedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
            Assert.That(persistedData.OwnerGuildId, Is.EqualTo(fixture.AttackPersistentGuildId));
            Assert.That(persistedData.TributeMoney, Is.Zero);
        }

        Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowOwnershipChangeAsync("Attackers"),
                Times.Once);
    }

    /// <summary>
    /// Verifies that the current owner and economy are retained when the Crown was not captured.
    /// </summary>
    [Test]
    public async ValueTask FinalResultRetainsCurrentOwnerWithoutCaptureAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var observer = await fixture.CreatePlayerAsync(DefenseGuildId, "Observer", CastleSiegeJoinSide.Defense, 100, 100).ConfigureAwait(false);
        fixture.Context.MiddleOwnerGuildId = null;
        fixture.Context.SiegeData.TaxChaos = 3;
        fixture.Context.SiegeData.TaxStore = 2;
        fixture.Context.SiegeData.TaxHunt = 100_000;
        fixture.Context.SiegeData.TributeMoney = 6_000;

        await CastleSiegeCrownMechanics
            .CheckResultAsync(fixture.Context)
            .ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
            Assert.That(fixture.Context.SiegeData.TaxChaos, Is.EqualTo(3));
            Assert.That(fixture.Context.SiegeData.TaxStore, Is.EqualTo(2));
            Assert.That(fixture.Context.SiegeData.TaxHunt, Is.EqualTo(100_000));
            Assert.That(fixture.Context.SiegeData.TributeMoney, Is.EqualTo(6_000));
        });

        Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(plugIn => plugIn.ShowOwnershipChangeAsync("Defenders"), Times.Once);
    }

    /// <summary>
    /// Verifies that an unoccupied siege without a capture does not announce an empty winner.
    /// </summary>
    [Test]
    public async ValueTask FinalResultWithoutOwnerDoesNotAnnounceSuccessAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var observer = await fixture.CreatePlayerAsync(AttackGuildId, "Observer", CastleSiegeJoinSide.None, 100, 100).ConfigureAwait(false);
        fixture.Context.MiddleOwnerGuildId = null;
        fixture.Context.FinalGuildList.Clear();
        fixture.Context.SiegeData.OwnerGuildId = null;
        fixture.Context.SiegeData.IsOccupied = false;

        await CastleSiegeCrownMechanics.CheckResultAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.Null);
            Assert.That(fixture.Context.SiegeData.IsOccupied, Is.False);
        });
        Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(plugIn => plugIn.ShowOwnershipChangeAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Verifies that an unresolved owner name does not announce success or discard persisted ownership.
    /// </summary>
    /// <param name="runtimeGuildId">The result of resolving the persistent guild identifier.</param>
    /// <param name="guildName">The resolved guild name, or null for a missing guild.</param>
    [TestCase(0u, null)]
    [TestCase(DefenseGuildId, null)]
    [TestCase(DefenseGuildId, "")]
    [TestCase(DefenseGuildId, " ")]
    public async ValueTask FinalResultWithUnresolvedOwnerDoesNotAnnounceSuccessAsync(uint runtimeGuildId, string? guildName)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var observer = await fixture.CreatePlayerAsync(AttackGuildId, "Observer", CastleSiegeJoinSide.None, 100, 100).ConfigureAwait(false);
        fixture.Context.MiddleOwnerGuildId = null;
        fixture.Context.FinalGuildList.Clear();
        var guildServer = Mock.Get(fixture.GameServerContext.GuildServer);
        guildServer.Setup(server => server.GetGuildIdAsync(fixture.DefensePersistentGuildId))
            .Returns(new ValueTask<uint>(runtimeGuildId));
        guildServer.Setup(server => server.GetGuildAsync(DefenseGuildId))
            .Returns(new ValueTask<RuntimeGuild?>(guildName is null ? null : new RuntimeGuild { Name = guildName }));

        await CastleSiegeCrownMechanics.CheckResultAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
            Assert.That(fixture.Context.SiegeData.IsOccupied, Is.True);
        });
        Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
            .Verify(plugIn => plugIn.ShowOwnershipChangeAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Verifies that an unresolved intermediate owner does not abort End-state processing.
    /// </summary>
    [Test]
    public async ValueTask FinalResultRetainsPersistedOwnerWhenIntermediateOwnerIsMissingAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.Context.MiddleOwnerGuildId = uint.MaxValue;

        await CastleSiegeCrownMechanics.CheckResultAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
            Assert.That(fixture.Context.SiegeData.IsOccupied, Is.True);
        });
    }

    /// <summary>
    /// Verifies that a completed ownership tenure's economy is not restored when its guild recaptures the Crown.
    /// </summary>
    [Test]
    public async ValueTask RecaptureDoesNotRestorePreviousOwnershipEconomyAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var attacker = await fixture.CreatePlayerAsync(
                AttackGuildId,
                "Attacker",
                CastleSiegeJoinSide.Attack1,
                60,
                60)
            .ConfigureAwait(false);
        var formerDefender = await fixture.CreatePlayerAsync(
                DefenseGuildId,
                "FormerDefender",
                CastleSiegeJoinSide.Defense,
                61,
                60)
            .ConfigureAwait(false);
        fixture.Context.SiegeData.TaxChaos = 3;
        fixture.Context.SiegeData.TaxStore = 2;
        fixture.Context.SiegeData.TaxHunt = 100_000;
        fixture.Context.SiegeData.TributeMoney = 6_000;

        await CastleSiegeCrownMechanics
            .ChangeWinnerGuildAsync(fixture.Context, attacker, CastleSiegeJoinSide.Attack1)
            .ConfigureAwait(false);
        await CastleSiegeCrownMechanics
            .ChangeWinnerGuildAsync(fixture.Context, formerDefender, CastleSiegeJoinSide.Attack1)
            .ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
            Assert.That(fixture.Context.SiegeData.TaxChaos, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TaxStore, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TaxHunt, Is.Zero);
            Assert.That(fixture.Context.SiegeData.TributeMoney, Is.Zero);
        });
    }

    /// <summary>
    /// Verifies that Crown progress uses elapsed wall time when periodic ticks are delayed.
    /// </summary>
    [Test]
    public async ValueTask CrownProgressUsesElapsedTimeAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(AttackGuildId, "CrownUser", CastleSiegeJoinSide.Attack1, 60, 60).ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = await fixture.CreatePlayerAsync(AttackGuildId, "SwitchOne", CastleSiegeJoinSide.Attack1, 70, 60).ConfigureAwait(false);
        fixture.Context.SwitchUsers[1] = await fixture.CreatePlayerAsync(AttackGuildId, "SwitchTwo", CastleSiegeJoinSide.Attack1, 80, 60).ConfigureAwait(false);

        await fixture.CheckCrownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Attempt,
                    TimeSpan.FromSeconds(2)),
                Times.Once);
    }

    /// <summary>
    /// Verifies that a delayed periodic tick cannot satisfy the Crown hold duration in one update.
    /// </summary>
    [Test]
    public async ValueTask CrownProgressClampsDelayedUpdatesAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(AttackGuildId, "CrownUser", CastleSiegeJoinSide.Attack1, 60, 60).ConfigureAwait(false);
        fixture.Context.CrownUser = crownUser;
        fixture.Context.SwitchUsers[0] = await fixture.CreatePlayerAsync(AttackGuildId, "SwitchOne", CastleSiegeJoinSide.Attack1, 70, 60).ConfigureAwait(false);
        fixture.Context.SwitchUsers[1] = await fixture.CreatePlayerAsync(AttackGuildId, "SwitchTwo", CastleSiegeJoinSide.Attack1, 80, 60).ConfigureAwait(false);

        await fixture.CheckCrownAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(TimeSpan.FromSeconds(2)));
        Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
        Mock.Get(crownUser.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
            .Verify(
                plugIn => plugIn.ShowCrownAccessStateAsync(
                    CastleSiegeCrownAccessState.Attempt,
                    TimeSpan.FromSeconds(2)),
                Times.Once);
    }

    /// <summary>
    /// Verifies every partial Crown/switch occupancy combination independently of packets.
    /// </summary>
    /// <param name="hasCrownUser">Whether the Crown is occupied.</param>
    /// <param name="hasFirstSwitchUser">Whether the first switch is occupied.</param>
    /// <param name="hasSecondSwitchUser">Whether the second switch is occupied.</param>
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public async ValueTask CrownProgressRequiresCompleteOccupancyAsync(bool hasCrownUser, bool hasFirstSwitchUser, bool hasSecondSwitchUser)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var crownUser = await fixture.CreatePlayerAsync(AttackGuildId, "Crown", CastleSiegeJoinSide.Attack1, 60, 60).ConfigureAwait(false);
        var firstSwitchUser = await fixture.CreatePlayerAsync(AttackGuildId, "First", CastleSiegeJoinSide.Attack1, 70, 60).ConfigureAwait(false);
        var secondSwitchUser = await fixture.CreatePlayerAsync(AttackGuildId, "Second", CastleSiegeJoinSide.Attack1, 80, 60).ConfigureAwait(false);
        fixture.Context.CrownUser = hasCrownUser ? crownUser : null;
        fixture.Context.SwitchUsers[0] = hasFirstSwitchUser ? firstSwitchUser : null;
        fixture.Context.SwitchUsers[1] = hasSecondSwitchUser ? secondSwitchUser : null;

        await fixture.CheckCrownAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.CrownAccumulatedTime, Is.EqualTo(hasCrownUser && hasFirstSwitchUser && hasSecondSwitchUser ? TimeSpan.FromSeconds(1) : TimeSpan.Zero));
            Assert.That(fixture.Context.SiegeData.OwnerGuildId, Is.EqualTo(fixture.DefensePersistentGuildId));
        });
    }

    private static async ValueTask<TestFixture> CreateFixtureAsync(CastleSiegePlugIn? plugIn = null)
    {
        var persistenceContextProvider = new InMemoryPersistenceContextProvider();
        BasicModel.GameConfiguration gameConfiguration;
        BasicModel.CastleSiegeConfiguration configuration;
        BasicModel.GameMapDefinition siegeMap;
        Guid defensePersistentGuildId;
        Guid attackPersistentGuildId;
        using (var persistenceContext = persistenceContextProvider.CreateNewContext())
        {
            gameConfiguration = persistenceContext.CreateNew<BasicModel.GameConfiguration>();
            var normalMap = persistenceContext.CreateNew<BasicModel.GameMapDefinition>();
            normalMap.Number = 0;
            normalMap.TerrainData = new byte[65_539];
            gameConfiguration.Maps.Add(normalMap);

            siegeMap = persistenceContext.CreateNew<BasicModel.GameMapDefinition>();
            siegeMap.Number = 30;
            siegeMap.TerrainData = new byte[65_539];
            gameConfiguration.Maps.Add(siegeMap);

            configuration = persistenceContext.CreateNew<BasicModel.CastleSiegeConfiguration>();
            configuration.Enabled = true;
            configuration.CastleSiegeMapDefinition = siegeMap;
            configuration.CrownHoldTimeSeconds = 3;
            configuration.AttackRespawnArea = persistenceContext.CreateNew<BasicModel.CastleSiegeZoneDefinition>();
            configuration.AttackRespawnArea.X1 = 35;
            configuration.AttackRespawnArea.Y1 = 11;
            configuration.AttackRespawnArea.X2 = 40;
            configuration.AttackRespawnArea.Y2 = 16;
            configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry
            {
                State = CastleSiegeState.Ready,
                DayOfWeek = DayOfWeek.Monday,
            });
            configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry
            {
                State = CastleSiegeState.Start,
                DayOfWeek = DayOfWeek.Tuesday,
            });
            if (plugIn is not null)
            {
                configuration.RegisterMinMembers = 3;
                configuration.ParticipantRewardMinSeconds = 5;
                configuration.GuildScoreCastleSiege = 10;
                var sign = persistenceContext.CreateNew<BasicModel.ItemDefinition>();
                sign.Group = 14;
                sign.Number = 21;
                sign.Width = 1;
                sign.Height = 1;
                gameConfiguration.Items.Add(sign);
                configuration.SignOfLordItemDefinition = sign;
                var reward = persistenceContext.CreateNew<BasicModel.ItemDefinition>();
                reward.Group = 14;
                reward.Number = 13;
                reward.Width = 1;
                reward.Height = 1;
                reward.Durability = 1;
                gameConfiguration.Items.Add(reward);
                configuration.RewardItemDefinition = reward;
                foreach (var state in new[] { CastleSiegeState.RegisterGuild, CastleSiegeState.RegisterMark, CastleSiegeState.Notify, CastleSiegeState.End, CastleSiegeState.EndCycle })
                {
                    configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry
                    {
                        State = state,
                        DayOfWeek = state is CastleSiegeState.End or CastleSiegeState.EndCycle ? DayOfWeek.Wednesday : DayOfWeek.Sunday,
                        Hour = (byte)state,
                    });
                }
            }
            gameConfiguration.CastleSiegeConfiguration = configuration;

            AddNpc(CastleSiegeCrown.MonsterNumber, 1, 60, 60);
            AddNpc(CastleSiegeSwitch.FirstMonsterNumber, 1, 70, 60);
            AddNpc(CastleSiegeSwitch.SecondMonsterNumber, 2, 80, 60);

            var defenseGuild = persistenceContext.CreateNew<BasicModel.Guild>();
            defenseGuild.Name = "Defenders";
            defensePersistentGuildId = defenseGuild.Id;
            var attackGuild = persistenceContext.CreateNew<BasicModel.Guild>();
            attackGuild.Name = "Attackers";
            attackPersistentGuildId = attackGuild.Id;
            var siegeData = persistenceContext.CreateNew<BasicModel.CastleSiegeData>();
            siegeData.OwnerGuildId = defensePersistentGuildId;
            siegeData.IsOccupied = true;
            await persistenceContext.SaveChangesAsync().ConfigureAwait(false);

            void AddNpc(short monsterNumber, byte instanceId, byte x, byte y)
            {
                var monster = persistenceContext.CreateNew<BasicModel.MonsterDefinition>();
                monster.Number = monsterNumber;
                monster.ObjectKind = NpcObjectKind.PassiveNpc;
                gameConfiguration.Monsters.Add(monster);
                configuration.NpcDefinitions.Add(new BasicModel.CastleSiegeNpcDefinition
                {
                    MonsterDefinition = monster,
                    InstanceId = instanceId,
                    SpawnX = x,
                    SpawnY = y,
                    Direction = Direction.South,
                });
            }
        }

        var guildServer = new Mock<IGuildServer>();
        guildServer
            .Setup(server => server.GetGuildIdAsync(defensePersistentGuildId))
            .Returns(new ValueTask<uint>(DefenseGuildId));
        guildServer
            .Setup(server => server.GetGuildIdAsync(attackPersistentGuildId))
            .Returns(new ValueTask<uint>(AttackGuildId));
        guildServer
            .Setup(server => server.GetGuildAsync(DefenseGuildId))
            .Returns(new ValueTask<RuntimeGuild?>(new RuntimeGuild { Name = "Defenders" }));
        guildServer
            .Setup(server => server.GetGuildAsync(AttackGuildId))
            .Returns(new ValueTask<RuntimeGuild?>(new RuntimeGuild { Name = "Attackers" }));
        guildServer
            .Setup(server => server.GetPersistentGuildIdAsync(AttackGuildId))
            .Returns(new ValueTask<Guid?>(attackPersistentGuildId));
        guildServer
            .Setup(server => server.GetGuildListAsync(AttackGuildId))
            .Returns(new ValueTask<IImmutableList<GuildListEntry>>(ImmutableList.Create(
                new GuildListEntry { PlayerName = "CrownUser", PlayerPosition = GuildPosition.GuildMaster },
                new GuildListEntry(),
                new GuildListEntry())));
        guildServer
            .Setup(server => server.GetAllianceGuildsAsync(It.IsAny<uint>()))
            .Returns(new ValueTask<IImmutableList<AllianceGuildEntry>>(ImmutableList<AllianceGuildEntry>.Empty));

        var plugInManager = new PlugInManager([], NullLoggerFactory.Instance, null, null);
        var mapInitializer = new MapInitializer(
            gameConfiguration,
            new NullLogger<MapInitializer>(),
            NullDropGenerator.Instance,
            null);
        var gameServerContext = new GameServerContext(
            new BasicModel.GameServerDefinition
            {
                GameConfiguration = gameConfiguration,
                ServerConfiguration = new BasicModel.GameServerConfiguration(),
            },
            guildServer.Object,
            new Mock<IEventPublisher>().Object,
            new Mock<ILoginServer>().Object,
            new Mock<IFriendServer>().Object,
            persistenceContextProvider,
            mapInitializer,
            NullLoggerFactory.Instance,
            plugInManager,
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());
        mapInitializer.PlugInManager = gameServerContext.PlugInManager;
        mapInitializer.PathFinderPool = gameServerContext.PathFinderPool;

        var initializationTimeUtc = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        CastleSiegeContext context;
        if (plugIn is not null)
        {
            await plugIn.ExecuteTaskAsync(gameServerContext).ConfigureAwait(false);
            context = plugIn.GetContext(gameServerContext) ?? throw new InvalidOperationException("The Castle Siege plug-in did not initialize its context.");
        }
        else
        {
            context = new CastleSiegeContext(gameServerContext, configuration);
            await context.InitializeAsync(initializationTimeUtc).ConfigureAwait(false);
            context.CurrentState = CastleSiegeState.Start;
            context.FinalGuildList[DefenseGuildId] = new CastleSiegeGuildParticipant
            {
                GuildId = DefenseGuildId,
                PersistentGuildId = defensePersistentGuildId,
                GuildName = "Defenders",
                Side = CastleSiegeJoinSide.Defense,
                IsAllianceMaster = true,
            };
            context.FinalGuildList[AttackGuildId] = new CastleSiegeGuildParticipant
            {
                GuildId = AttackGuildId,
                PersistentGuildId = attackPersistentGuildId,
                GuildName = "Attackers",
                Side = CastleSiegeJoinSide.Attack1,
                IsAllianceMaster = true,
            };
        }

        var map = await gameServerContext.GetMapAsync(30).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("The Castle Siege test map could not be initialized.");
        return new(
            persistenceContextProvider,
            configuration,
            gameServerContext,
            context,
            map,
            defensePersistentGuildId,
            attackPersistentGuildId,
            initializationTimeUtc);
    }

    private sealed record TestFixture(
        InMemoryPersistenceContextProvider PersistenceContextProvider,
        CastleSiegeConfiguration Configuration,
        GameServerContext GameServerContext,
        CastleSiegeContext Context,
        GameMap SiegeMap,
        Guid DefensePersistentGuildId,
        Guid AttackPersistentGuildId,
        DateTime InitializationTimeUtc)
    {
        private DateTime? _crownTimeUtc;

        /// <summary>
        /// Creates and tracks a Castle Siege participant.
        /// </summary>
        internal async ValueTask<Player> CreatePlayerAsync(
            uint guildId,
            string characterName,
            CastleSiegeJoinSide side,
            byte x,
            byte y,
            CastleSiegePlugIn? plugIn = null)
        {
            var player = await PlayerTestHelper.CreatePlayerAsync(this.GameServerContext).ConfigureAwait(false);
            player.SelectedCharacter!.Id = Guid.NewGuid();
            player.SelectedCharacter.Name = characterName;
            player.GuildStatus = new GuildMemberStatus(guildId, GuildPosition.NormalMember);
            await this.GameServerContext.AddPlayerAsync(player).ConfigureAwait(false);
            await player.WarpToAsync(new ExitGate
            {
                Map = this.Configuration.CastleSiegeMapDefinition,
                X1 = x,
                X2 = x,
                Y1 = y,
                Y2 = y,
                Direction = Direction.South,
            }).ConfigureAwait(false);
            await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            player.IsAlive = true;
            this.Context.TrackPlayer(player, this.SiegeMap);
            if (plugIn is not null)
            {
                await plugIn.ObjectAddedToMapAsync(this.SiegeMap, player).ConfigureAwait(false);
            }
            else
            {
                this.Context.PlayerJoinSides[player.SelectedCharacter.Id] = side;
            }

            return player;
        }

        /// <summary>
        /// Advances the Crown mechanics clock and executes one progress check.
        /// </summary>
        /// <param name="elapsed">The elapsed time since the previous check.</param>
        /// <returns>A task that represents the asynchronous check.</returns>
        internal ValueTask CheckCrownAsync(TimeSpan? elapsed = null)
        {
            if (this._crownTimeUtc is null)
            {
                this._crownTimeUtc = this.InitializationTimeUtc;
                this.Context.LastCrownUpdateUtc = this.InitializationTimeUtc;
            }

            this._crownTimeUtc += elapsed ?? TimeSpan.FromSeconds(1);
            return CastleSiegeCrownMechanics.CheckMiddleWinnerAsync(this.Context, this._crownTimeUtc.Value);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => this._utcNow;

        public void Advance(TimeSpan elapsed) => this._utcNow += elapsed;
    }
}
