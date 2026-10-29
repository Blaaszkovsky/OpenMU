// <copyright file="CastleSiegeGuildSelectionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.Views.CastleSiege;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler.CastleSiege;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;
using RuntimeGuild = MUnique.OpenMU.Interfaces.Guild;

/// <summary>
/// Tests Castle Siege guild selection, participation, and rewards.
/// </summary>
[TestFixture]
public class CastleSiegeGuildSelectionTests
{
    private const uint DefenseGuildId = 10;
    private const uint DefenseAllianceGuildId = 11;
    private const uint AlphaGuildId = 20;
    private const uint AlphaAllianceGuildId = 21;
    private const uint ReconnectedAlphaGuildId = 22;
    private const uint BravoGuildId = 30;
    private const uint CharlieGuildId = 40;
    private const uint DeltaGuildId = 50;

    /// <summary>
    /// Verifies the documented Castle Siege selection formula.
    /// </summary>
    [Test]
    public void SelectionScoreUsesMarksMembersAndCombinedLevel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CastleSiegeGuildSelector.CalculateScore(10, 2, 500), Is.EqualTo(177));
            Assert.That(CastleSiegeGuildSelector.CalculateScore(int.MaxValue, int.MaxValue, int.MaxValue), Is.EqualTo(int.MaxValue));
        });
    }

    /// <summary>
    /// Verifies zero scores, integer division, and saturation at the supported score limit.
    /// </summary>
    /// <param name="marks">The submitted marks.</param>
    /// <param name="members">The guild member count.</param>
    /// <param name="level">The combined guild master level.</param>
    /// <param name="expected">The expected score.</param>
    [TestCase(0, 0, 0, 0)]
    [TestCase(0, 1, 3, 1)]
    [TestCase(0, 1, 4, 2)]
    [TestCase(int.MaxValue, 0, 0, int.MaxValue)]
    [TestCase(0, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void SelectionScoreHandlesBoundaries(int marks, int members, int level, int expected)
    {
        Assert.That(CastleSiegeGuildSelector.CalculateScore(marks, members, level), Is.EqualTo(expected));
    }

    /// <summary>
    /// Verifies that ineligible registrations do not consume an attacking side.
    /// </summary>
    /// <param name="hasMarks">Whether the highest-ranked guild submitted marks.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask SelectionSkipsGuildsWithoutMarksOrMembersAsync(bool hasMarks)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        if (hasMarks)
        {
            fixture.GuildServer.Setup(server => server.GetGuildListAsync(AlphaGuildId))
                .ReturnsAsync(ImmutableList<GuildListEntry>.Empty);
        }
        else
        {
            fixture.Context.RegisteredGuilds[fixture.AlphaPersistentGuildId].Marks = 0;
        }

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.FinalGuildList, Does.Not.ContainKey(AlphaGuildId));
            Assert.That(fixture.Context.FinalGuildList, Does.Not.ContainKey(AlphaAllianceGuildId));
            Assert.That(fixture.Context.FinalGuildList[CharlieGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList[BravoGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack2));
            Assert.That(fixture.Context.FinalGuildList[DeltaGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack3));
        });
    }

    /// <summary>
    /// Verifies the configured attacker limit without excluding the defending alliance.
    /// </summary>
    /// <param name="maximumAttackers">The configured limit.</param>
    /// <param name="expectedAttackers">The supported number of attacking alliances.</param>
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 3)]
    [TestCase(int.MaxValue, 3)]
    public async ValueTask SelectionHonorsSupportedAttackerLimitAsync(int maximumAttackers, int expectedAttackers)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.MaxAttackingGuilds = maximumAttackers;

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.FinalGuildList.Values.Count(guild => guild.IsAllianceMaster && guild.Side != CastleSiegeJoinSide.Defense), Is.EqualTo(expectedAttackers));
            Assert.That(fixture.Context.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[DefenseAllianceGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
        });
    }

    /// <summary>
    /// Verifies ranking, tie-breaking, offline guild loading, alliance expansion, persistence, and restart loading.
    /// </summary>
    [Test]
    public async ValueTask SelectionAssignsSidesAndPersistsFinalGuildListAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.FinalGuildList, Has.Count.EqualTo(6));
            Assert.That(fixture.Context.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[DefenseAllianceGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Defense));
            Assert.That(fixture.Context.FinalGuildList[AlphaGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList[AlphaGuildId].Score, Is.EqualTo(177));
            Assert.That(fixture.Context.FinalGuildList[AlphaAllianceGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList[AlphaAllianceGuildId].Score, Is.Zero);
            Assert.That(fixture.Context.FinalGuildList[CharlieGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack2));
            Assert.That(fixture.Context.FinalGuildList[BravoGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack3));
            Assert.That(fixture.Context.FinalGuildList, Does.Not.ContainKey(DeltaGuildId));
            Assert.That(fixture.Context.MiddleOwnerGuildId, Is.EqualTo(DefenseGuildId));
        });
        fixture.GuildServer.Verify(
            server => server.GetGuildIdByNameAsync(It.IsAny<string>()),
            Times.Never);

        using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
                   typeof(CastleSiegeData),
                   false,
                   fixture.GameConfiguration))
        {
            var persistedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
            Assert.That(persistedData.Guilds, Has.Count.EqualTo(6));
            Assert.That(
                persistedData.Guilds.Single(guild => guild.GuildId == fixture.AlphaPersistentGuildId).Side,
                Is.EqualTo(CastleSiegeJoinSide.Attack1));
        }

        var restartedContext = new CastleSiegeContext(fixture.GameServerContext, fixture.CastleSiegeConfiguration);
        await restartedContext.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(restartedContext.FinalGuildList, Has.Count.EqualTo(6));
            Assert.That(restartedContext.FinalGuildList[AlphaGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(restartedContext.FinalGuildList[CharlieGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack2));
            Assert.That(restartedContext.MiddleOwnerGuildId, Is.EqualTo(DefenseGuildId));
        });
    }

    /// <summary>
    /// Verifies that another server selects current registrations instead of its admission-time cache.
    /// </summary>
    [Test]
    public async ValueTask SelectionRefreshesRegistrationsAcrossGameContextsAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var peerGame = new Mock<IGameServerContext>();
        peerGame.SetupGet(game => game.Configuration).Returns(fixture.GameConfiguration);
        peerGame.SetupGet(game => game.PersistenceContextProvider).Returns(fixture.PersistenceContextProvider);
        peerGame.SetupGet(game => game.GuildServer).Returns(fixture.GuildServer.Object);
        peerGame.SetupGet(game => game.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        peerGame.Setup(game => game.GetPlayersAsync()).ReturnsAsync(Array.Empty<Player>());
        var peer = new CastleSiegeContext(peerGame.Object, fixture.CastleSiegeConfiguration);
        await peer.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);
        peer.RegisteredGuilds.Clear(); // Its cache predates registrations submitted to the other game server.

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        using var read = fixture.PersistenceContextProvider.CreateNewContext();
        var selected = (await read.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        var identities = selected.Guilds.ToDictionary(guild => guild.GuildId, guild => guild.Id);
        await CastleSiegeGuildSelector.SelectGuildsAsync(peer).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(peer.FinalGuildList.Keys, Is.EquivalentTo(fixture.Context.FinalGuildList.Keys));
            Assert.That(selected.Guilds.Select(guild => (guild.GuildId, guild.Id)), Is.EquivalentTo(identities.Select(pair => (pair.Key, pair.Value))));
        });
    }

    /// <summary>
    /// Verifies that an unchanged empty peer cannot erase a list selected by another server.
    /// </summary>
    [Test]
    public async ValueTask UnchangedPeerDoesNotClearSelectedGuildsAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var peerGame = new Mock<IGameContext>();
        peerGame.SetupGet(game => game.Configuration).Returns(fixture.GameConfiguration);
        peerGame.SetupGet(game => game.PersistenceContextProvider).Returns(fixture.PersistenceContextProvider);
        peerGame.SetupGet(game => game.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        peerGame.Setup(game => game.GetPlayersAsync()).ReturnsAsync(Array.Empty<Player>());
        var peer = new CastleSiegeContext(peerGame.Object, fixture.CastleSiegeConfiguration);
        await peer.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        await peer.SaveFinalGuildListAsync().ConfigureAwait(false);

        using var read = fixture.PersistenceContextProvider.CreateNewContext();
        Assert.That((await read.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single().Guilds, Has.Count.EqualTo(6));
    }

    /// <summary>
    /// Verifies that a delayed real Crown capture cannot change either ownership or selected sides after a peer capture.
    /// </summary>
    /// <param name="throughTick">Whether the rejected capture enters through the Crown progress callback.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask StaleCapturePreservesOwnerAndAllGuildSidesAsync(bool throughTick)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var store = new CastleSiegeStateMachineTests.DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var delayed = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var originalOwner = delayed.SiegeData.OwnerGuildId;
        var originalMiddle = delayed.MiddleOwnerGuildId;
        var originalSides = delayed.FinalGuildList.ToDictionary(pair => pair.Key, pair => pair.Value.Side);
        var firstPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "FirstSeal").ConfigureAwait(false);
        var delayedPlayer = await CreateSiegePlayerAsync(fixture, BravoGuildId, "LateSeal").ConfigureAwait(false);
        await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(first, firstPlayer, CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        var firstSaved = await store.ReadAsync().ConfigureAwait(false);
        var expectedOwner = firstSaved.OwnerGuildId;
        var expectedSides = firstSaved.Guilds.Select(guild => (guild.GuildId, guild.Side, guild.Id)).ToArray();
        var beforeRejectedSave = store.SuccessfulSaves;
        Exception? failure = null;
        try
        {
            if (throughTick)
            {
                var firstSwitch = await CreateSiegePlayerAsync(fixture, BravoGuildId, "LateSwitch1").ConfigureAwait(false);
                var secondSwitch = await CreateSiegePlayerAsync(fixture, BravoGuildId, "LateSwitch2").ConfigureAwait(false);
                delayedPlayer.IsAlive = firstSwitch.IsAlive = secondSwitch.IsAlive = true;
                delayed.CrownUser = delayedPlayer;
                delayed.SwitchUsers[0] = firstSwitch;
                delayed.SwitchUsers[1] = secondSwitch;
                delayed.CrownAccumulatedTime = TimeSpan.FromSeconds(fixture.CastleSiegeConfiguration.CrownHoldTimeSeconds);
                await CastleSiegeCrownMechanics.CheckMiddleWinnerAsync(delayed, fixture.InitializationTimeUtc).ConfigureAwait(false);
            }
            else
            {
                await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(delayed, delayedPlayer, CastleSiegeJoinSide.Attack3).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<PersistenceConflictException>());
            Assert.That(saved.OwnerGuildId, Is.EqualTo(expectedOwner));
            Assert.That(saved.Guilds.Select(guild => (guild.GuildId, guild.Side, guild.Id)), Is.EquivalentTo(expectedSides));
            Assert.That(store.SuccessfulSaves, Is.EqualTo(beforeRejectedSave));
            Assert.That(beforeRejectedSave, Is.EqualTo(1), "Owner and guild sides must commit together.");
            Assert.That(delayed.SiegeData.OwnerGuildId, Is.EqualTo(originalOwner));
            Assert.That(delayed.MiddleOwnerGuildId, Is.EqualTo(originalMiddle));
            Assert.That(delayed.FinalGuildList.Select(pair => (pair.Key, pair.Value.Side)), Is.EquivalentTo(originalSides.Select(pair => (pair.Key, pair.Value))));
            Mock.Get(delayedPlayer.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
                .Verify(view => view.ShowCrownAccessStateAsync(CastleSiegeCrownAccessState.Success, It.IsAny<TimeSpan>()), Times.Never);
        });
    }

    /// <summary>
    /// Verifies that returning ownership cannot make an old Crown capture's partial side changes current again.
    /// </summary>
    /// <param name="captureCount">The number of real peer captures before the delayed request.</param>
    [TestCase(3)]
    [TestCase(4)]
    public async ValueTask RepeatedCapturesRejectStaleOwnerAndGuildSideBaselineAsync(int captureCount)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var store = new CastleSiegeStateMachineTests.DetachedSiegeStore(fixture.PersistenceContextProvider);
        var active = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var delayed = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var localOwner = (delayed.SiegeData.OwnerGuildId, delayed.SiegeData.IsOccupied, delayed.SiegeData.TaxChaos,
            delayed.SiegeData.TaxStore, delayed.SiegeData.TaxHunt, delayed.SiegeData.TributeMoney, delayed.SiegeData.IsHuntZoneEnabled);
        var localMiddle = delayed.MiddleOwnerGuildId;
        var localSides = delayed.FinalGuildList.Select(pair => (pair.Key, pair.Value.Side)).ToArray();
        var players = new Dictionary<uint, Player>
        {
            [AlphaGuildId] = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "ActiveA").ConfigureAwait(false),
            [BravoGuildId] = await CreateSiegePlayerAsync(fixture, BravoGuildId, "ActiveB").ConfigureAwait(false),
            [DefenseGuildId] = await CreateSiegePlayerAsync(fixture, DefenseGuildId, "ActiveO").ConfigureAwait(false),
        };
        uint[] captures = [AlphaGuildId, BravoGuildId, DefenseGuildId, AlphaGuildId];
        foreach (var guildId in captures.Take(captureCount))
        {
            await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(active, players[guildId], active.FinalGuildList[guildId].Side).ConfigureAwait(false);
        }

        active.SiegeData.TaxChaos = 3;
        active.SiegeData.TaxStore = 2;
        active.SiegeData.TaxHunt = 123;
        active.SiegeData.TributeMoney = 777;
        active.SiegeData.IsHuntZoneEnabled = true;
        await active.SaveOwnerAsync().ConfigureAwait(false);
        var before = await store.ReadAsync().ConfigureAwait(false);
        var expectedOwner = (before.OwnerGuildId, before.IsOccupied, before.TaxChaos,
            before.TaxStore, before.TaxHunt, before.TributeMoney, before.IsHuntZoneEnabled);
        var expectedSides = before.Guilds.Select(guild => (guild.GuildId, guild.Side, guild.Id)).ToArray();
        var saves = store.SuccessfulSaves;
        Assert.Multiple(() =>
        {
            Assert.That(before.OwnerGuildId, Is.EqualTo(captureCount == 3 ? localOwner.OwnerGuildId : fixture.AlphaPersistentGuildId));
            Assert.That(active.FinalGuildList[BravoGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(active.FinalGuildList[DefenseGuildId].Side, Is.EqualTo(captureCount == 3 ? CastleSiegeJoinSide.Defense : CastleSiegeJoinSide.Attack3));
            Assert.That(active.FinalGuildList[AlphaGuildId].Side, Is.EqualTo(captureCount == 3 ? CastleSiegeJoinSide.Attack3 : CastleSiegeJoinSide.Defense));
            Assert.That(saves, Is.EqualTo(captureCount + 1));
        });
        var delayedPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "DelayedA").ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(delayed, delayedPlayer, CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<PersistenceConflictException>());
            Assert.That((saved.OwnerGuildId, saved.IsOccupied, saved.TaxChaos, saved.TaxStore, saved.TaxHunt, saved.TributeMoney, saved.IsHuntZoneEnabled), Is.EqualTo(expectedOwner));
            Assert.That(saved.Guilds.Select(guild => (guild.GuildId, guild.Side, guild.Id)), Is.EquivalentTo(expectedSides));
            Assert.That(store.SuccessfulSaves, Is.EqualTo(saves));
            Assert.That((delayed.SiegeData.OwnerGuildId, delayed.SiegeData.IsOccupied, delayed.SiegeData.TaxChaos, delayed.SiegeData.TaxStore, delayed.SiegeData.TaxHunt, delayed.SiegeData.TributeMoney, delayed.SiegeData.IsHuntZoneEnabled), Is.EqualTo(localOwner));
            Assert.That(delayed.MiddleOwnerGuildId, Is.EqualTo(localMiddle));
            Assert.That(delayed.FinalGuildList.Select(pair => (pair.Key, pair.Value.Side)), Is.EquivalentTo(localSides));
            Mock.Get(delayedPlayer.ViewPlugIns.GetPlugIn<ICastleSiegeCrownAccessStatePlugIn>()!)
                .Verify(view => view.ShowCrownAccessStateAsync(CastleSiegeCrownAccessState.Success, It.IsAny<TimeSpan>()), Times.Never);
        });
    }

    /// <summary>
    /// Verifies that a follower's normal End callback retains and announces the owner already captured by a peer.
    /// </summary>
    /// <param name="refreshBeforeEnd">Whether an earlier no-op save already refreshed the follower's owner.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask PeerEndRetainsAndAnnouncesFreshCapturedOwnerAsync(bool refreshBeforeEnd)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var store = new CastleSiegeStateMachineTests.DetachedSiegeStore(fixture.PersistenceContextProvider);
        var active = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var follower = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        Assert.That(follower.SiegeData.OwnerGuildId, Is.Not.Null);
        var capturingPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "CaptureA").ConfigureAwait(false);
        var observer = await CreateSiegePlayerAsync(fixture, DefenseGuildId, "EndObserver").ConfigureAwait(false);
        follower.TrackPlayer(observer, observer.CurrentMap!);
        await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(active, capturingPlayer, CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        if (refreshBeforeEnd)
        {
            await follower.SaveOwnerAsync().ConfigureAwait(false);
        }

        await CastleSiegeCrownMechanics.CheckResultAsync(follower).ConfigureAwait(false);

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(saved.OwnerGuildId, Is.EqualTo(fixture.AlphaPersistentGuildId));
            Assert.That(follower.SiegeData.OwnerGuildId, Is.EqualTo(fixture.AlphaPersistentGuildId));
            Assert.That(follower.MiddleOwnerGuildId, Is.EqualTo(AlphaGuildId));
            Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
                .Verify(view => view.ShowOwnershipChangeAsync("Alpha"), Times.Once);
            Mock.Get(observer.ViewPlugIns.GetPlugIn<ICastleSiegeOwnershipChangePlugIn>()!)
                .Verify(view => view.ShowOwnershipChangeAsync("Defend"), Times.Never);
        });
    }

    /// <summary>
    /// Verifies that a rejected administrative handover restores the exact local economy, including pending tribute.
    /// </summary>
    [Test]
    public async ValueTask RejectedAdminOwnerChangeRestoresPendingEconomyAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var store = new CastleSiegeStateMachineTests.DetachedSiegeStore(fixture.PersistenceContextProvider);
        var active = await CreatePeerContextAsync(fixture, store.Provider).ConfigureAwait(false);
        var plugin = new CastleSiegePlugIn(new FixtureTimeProvider(fixture.InitializationTimeUtc));
        var follower = await CreatePeerContextAsync(fixture, store.Provider, plugin).ConfigureAwait(false);
        var capturingPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "CaptureA").ConfigureAwait(false);
        follower.SiegeData.TaxChaos = 3;
        follower.SiegeData.TaxStore = 2;
        follower.SiegeData.TaxHunt = 123;
        follower.SiegeData.TributeMoney = 777;
        follower.SiegeData.IsHuntZoneEnabled = true;
        follower.IsEconomyPersistencePending = true;
        var before = (follower.SiegeData.OwnerGuildId, follower.SiegeData.IsOccupied, follower.SiegeData.TaxChaos,
            follower.SiegeData.TaxStore, follower.SiegeData.TaxHunt, follower.SiegeData.TributeMoney, follower.SiegeData.IsHuntZoneEnabled);
        var bravoId = follower.FinalGuildList[BravoGuildId].PersistentGuildId;
        fixture.GuildServer.Setup(server => server.GetPersistentGuildIdByNameAsync("Bravo")).ReturnsAsync(bravoId);
        fixture.GuildServer.Setup(server => server.GetPersistentGuildNameAsync(bravoId)).ReturnsAsync("Bravo");
        await CastleSiegeCrownMechanics.ChangeWinnerGuildAsync(active, capturingPlayer, CastleSiegeJoinSide.Attack1).ConfigureAwait(false);

        Assert.ThrowsAsync<PersistenceConflictException>(async () => await new CastleSiegeAdministration(plugin)
            .SetOwnerAsync(follower.GameContext, "Bravo").ConfigureAwait(false));

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(saved.OwnerGuildId, Is.EqualTo(fixture.AlphaPersistentGuildId));
            Assert.That((follower.SiegeData.OwnerGuildId, follower.SiegeData.IsOccupied, follower.SiegeData.TaxChaos,
                follower.SiegeData.TaxStore, follower.SiegeData.TaxHunt, follower.SiegeData.TributeMoney, follower.SiegeData.IsHuntZoneEnabled), Is.EqualTo(before));
            Assert.That(follower.IsEconomyPersistencePending, Is.True);
        });
    }

    /// <summary>
    /// Verifies that an incomplete alliance response cannot consume an empty attacking side.
    /// </summary>
    [Test]
    public async ValueTask SelectionAddsAllianceMasterWhenAllianceResponseOmitsItAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.GuildServer
            .Setup(server => server.GetAllianceGuildsAsync(AlphaGuildId))
            .ReturnsAsync([
                new AllianceGuildEntry(
                    AlphaAllianceGuildId,
                    "AlphaAl",
                    1,
                    Memory<byte>.Empty),
            ]);

        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.FinalGuildList[AlphaGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList[AlphaGuildId].IsAllianceMaster, Is.True);
            Assert.That(fixture.Context.FinalGuildList[AlphaAllianceGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList[CharlieGuildId].Side, Is.EqualTo(CastleSiegeJoinSide.Attack2));
        });
    }

    /// <summary>
    /// Verifies that an unavailable guild server cannot leave a stale final guild list in persistence.
    /// </summary>
    [Test]
    public async ValueTask SelectionWithoutGameServerClearsPersistedFinalGuildListAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        var gameContext = new Mock<IGameContext>();
        gameContext.SetupGet(context => context.Configuration).Returns(fixture.GameConfiguration);
        gameContext.SetupGet(context => context.PersistenceContextProvider).Returns(fixture.PersistenceContextProvider);
        gameContext.SetupGet(context => context.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        gameContext.Setup(context => context.GetPlayersAsync()).ReturnsAsync(Array.Empty<Player>());
        var context = new CastleSiegeContext(gameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);

        await CastleSiegeGuildSelector.SelectGuildsAsync(context).ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var persistedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(context.FinalGuildList, Is.Empty);
            Assert.That(persistedData.Guilds, Is.Empty);
        });
    }

    /// <summary>
    /// Verifies that persistence and packet views use one deterministic final guild ordering.
    /// </summary>
    [Test]
    public void FinalGuildOrderingUsesSideAllianceMasterAndName()
    {
        var guilds = new[]
        {
            new CastleSiegeGuildParticipant { GuildId = 1, GuildName = "Zulu", Side = CastleSiegeJoinSide.Attack1 },
            new CastleSiegeGuildParticipant { GuildId = 2, GuildName = "Alpha", Side = CastleSiegeJoinSide.Defense },
            new CastleSiegeGuildParticipant { GuildId = 3, GuildName = "Bravo", Side = CastleSiegeJoinSide.Attack1, IsAllianceMaster = true },
            new CastleSiegeGuildParticipant { GuildId = 4, GuildName = "alpha", Side = CastleSiegeJoinSide.Attack1 },
        };

        Assert.That(
            CastleSiegeGuildSelector.OrderFinalGuilds(guilds).Select(guild => guild.GuildId),
            Is.EqualTo(new uint[] { 2, 3, 4, 1 }));
    }

    /// <summary>
    /// Verifies join-side notification, reconnect mapping, magic effects, and participant accumulation.
    /// </summary>
    [Test]
    public async ValueTask JoinSideAssignmentAndParticipantTrackingUseCastleSiegeMapAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var player = await CreateSiegePlayerAsync(fixture, ReconnectedAlphaGuildId, "Fighter").ConfigureAwait(false);
        var view = player.ViewPlugIns.GetPlugIn<ICastleSiegeJoinSidePlugIn>()!;

        await fixture.Context.SetPlayerJoinSideAsync().ConfigureAwait(false);
        Mock.Get(view).Verify(
            plugIn => plugIn.ShowJoinSideAsync(CastleSiegeJoinSide.Attack1),
            Times.Once);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.GetPlayerJoinSide(player), Is.EqualTo(CastleSiegeJoinSide.Attack1));
            Assert.That(fixture.Context.FinalGuildList, Does.ContainKey(ReconnectedAlphaGuildId));
            Assert.That(fixture.Context.FinalGuildList, Does.Not.ContainKey(AlphaGuildId));
            Assert.That(
                player.MagicEffectList.GetActiveEffectsSnapshot().Select(effect => effect.Id),
                Does.Contain((short)CastleSiegeMagicEffectNumber.Attack1));
        });

        Assert.That(
            await player.PlayerState.TryAdvanceToAsync(PlayerState.NpcDialogOpened).ConfigureAwait(false),
            Is.True);
        await fixture.Context.SetPlayerJoinSideAsync().ConfigureAwait(false);
        Assert.That(fixture.Context.GetPlayerJoinSide(player), Is.EqualTo(CastleSiegeJoinSide.Attack1));
        Mock.Get(view).Verify(
            plugIn => plugIn.ShowJoinSideAsync(CastleSiegeJoinSide.Attack1),
            Times.Once);
        Assert.That(
            await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false),
            Is.True);

        fixture.Context.CurrentState = CastleSiegeState.Start;
        var firstUpdateUtc = fixture.InitializationTimeUtc;
        await CastleSiegeParticipantTracker.TrackAsync(fixture.Context, firstUpdateUtc).ConfigureAwait(false);
        await CastleSiegeParticipantTracker.TrackAsync(fixture.Context, firstUpdateUtc.AddSeconds(17)).ConfigureAwait(false);
        CastleSiegeParticipantTracker.StartTracking(fixture.Context, player, firstUpdateUtc.AddSeconds(23));

        var participant = fixture.Context.ParticipantTracking[player.SelectedCharacter!.Id];
        Assert.Multiple(() =>
        {
            Assert.That(participant.CharacterName, Is.EqualTo("Fighter"));
            Assert.That(participant.GuildId, Is.EqualTo(ReconnectedAlphaGuildId));
            Assert.That(participant.ParticipationTime, Is.EqualTo(TimeSpan.FromSeconds(23)));
        });

        fixture.Context.UntrackPlayer(player);
        await player.WarpToAsync(new ExitGate
        {
            Map = fixture.NormalMap,
            X1 = 1,
            X2 = 1,
            Y1 = 1,
            Y2 = 1,
            Direction = Direction.South,
        }).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        CastleSiegeParticipantTracker.StopTracking(fixture.Context, player, firstUpdateUtc.AddSeconds(25));
        CastleSiegeParticipantTracker.StartTracking(fixture.Context, player, firstUpdateUtc.AddSeconds(100));

        await player.WarpToAsync(new ExitGate
        {
            Map = fixture.CastleSiegeMap,
            X1 = 1,
            X2 = 1,
            Y1 = 1,
            Y2 = 1,
            Direction = Direction.South,
        }).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        fixture.Context.TrackPlayer(player, player.CurrentMap!);
        await fixture.Context.SynchronizePlayerJoinSideAsync(player).ConfigureAwait(false);
        CastleSiegeParticipantTracker.StartTracking(fixture.Context, player, firstUpdateUtc.AddSeconds(100));
        await CastleSiegeParticipantTracker.TrackAsync(
                fixture.Context,
                firstUpdateUtc.AddSeconds(105))
            .ConfigureAwait(false);
        Assert.That(
            fixture.Context.ParticipantTracking[player.SelectedCharacter.Id].ParticipationTime,
            Is.EqualTo(TimeSpan.FromSeconds(30)));

        fixture.Context.UntrackPlayer(player);
        await player.WarpToAsync(new ExitGate
        {
            Map = fixture.NormalMap,
            X1 = 1,
            X2 = 1,
            Y1 = 1,
            Y2 = 1,
            Direction = Direction.South,
        }).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        await fixture.Context.SetPlayerJoinSideAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Context.GetPlayerJoinSide(player), Is.EqualTo(CastleSiegeJoinSide.None));
            Assert.That(
                player.MagicEffectList.GetActiveEffectsSnapshot().Select(effect => effect.Id),
                Does.Not.Contain((short)CastleSiegeMagicEffectNumber.Attack1));
        });
    }

    /// <summary>
    /// Verifies online and offline item rewards and winning-alliance guild score awards.
    /// </summary>
    [Test]
    public async ValueTask RewardsEligibleParticipantsAndWinningAllianceAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var player = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "Winner").ConfigureAwait(false);
        var itemAppearView = player.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!;
        fixture.Context.MiddleOwnerGuildId = AlphaGuildId;
        fixture.Context.ParticipantTracking[player.SelectedCharacter!.Id] = new CastleSiegeParticipant
        {
            CharacterId = player.SelectedCharacter.Id,
            CharacterName = player.Name,
            GuildId = AlphaGuildId,
            ParticipationTime = TimeSpan.FromSeconds(fixture.CastleSiegeConfiguration.ParticipantRewardMinSeconds),
        };
        fixture.Context.ParticipantTracking[fixture.OfflineCharacterId] = new CastleSiegeParticipant
        {
            CharacterId = fixture.OfflineCharacterId,
            CharacterName = "Offline",
            GuildId = AlphaGuildId,
            ParticipationTime = TimeSpan.FromSeconds(fixture.CastleSiegeConfiguration.ParticipantRewardMinSeconds),
        };

        await CastleSiegeParticipantTracker.AwardRewardsAsync(fixture.Context).ConfigureAwait(false);

        Assert.That(
            player.Inventory!.Items.Count(item => item.Definition == fixture.RewardItemDefinition),
            Is.EqualTo(1));
        Mock.Get(itemAppearView).Verify(
            plugIn => plugIn.ItemAppearAsync(It.Is<Item>(item => item.Definition == fixture.RewardItemDefinition)),
            Times.Once);
        using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
                   typeof(CastleSiegePendingReward),
                   false,
                   fixture.GameConfiguration))
        {
            var pendingReward = (await persistenceContext
                    .GetAsync<CastleSiegePendingReward>()
                    .ConfigureAwait(false))
                .Single();
            Assert.That(pendingReward.CharacterId, Is.EqualTo(fixture.OfflineCharacterId));
            Assert.That(pendingReward.ItemDefinitionId, Is.EqualTo(fixture.RewardItemDefinition.GetId()));
        }

        fixture.GuildServer.Verify(
            server => server.IncreaseGuildScoreAsync(
                AlphaGuildId,
                fixture.CastleSiegeConfiguration.GuildScoreCastleSiege),
            Times.Once);
        fixture.GuildServer.Verify(
            server => server.IncreaseGuildScoreAsync(
                AlphaAllianceGuildId,
                fixture.CastleSiegeConfiguration.GuildScoreCastleSiegeMembers),
            Times.Once);
    }

    /// <summary>
    /// Verifies the exact participation threshold for online delivery and offline queuing.
    /// </summary>
    /// <param name="secondsBelowThreshold">The number of seconds missing from eligibility.</param>
    [TestCase(1)]
    [TestCase(0)]
    public async ValueTask ParticipantRewardThresholdAppliesOnlineAndOfflineAsync(int secondsBelowThreshold)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var player = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "Boundary").ConfigureAwait(false);
        var participation = TimeSpan.FromSeconds(fixture.CastleSiegeConfiguration.ParticipantRewardMinSeconds - secondsBelowThreshold);
        foreach (var characterId in new[] { player.SelectedCharacter!.Id, fixture.OfflineCharacterId })
        {
            fixture.Context.ParticipantTracking[characterId] = new CastleSiegeParticipant
            {
                CharacterId = characterId,
                GuildId = AlphaGuildId,
                ParticipationTime = participation,
            };
        }

        await CastleSiegeParticipantTracker.AwardRewardsAsync(fixture.Context).ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegePendingReward), false, fixture.GameConfiguration);
        var queuedRewards = await persistenceContext.GetAsync<CastleSiegePendingReward>().ConfigureAwait(false);
        var expectedCount = secondsBelowThreshold == 0 ? 1 : 0;
        Assert.Multiple(() =>
        {
            Assert.That(player.Inventory!.Items.Count(item => item.Definition == fixture.RewardItemDefinition), Is.EqualTo(expectedCount));
            Assert.That(queuedRewards, Has.Count.EqualTo(expectedCount));
            Assert.That(queuedRewards.All(reward => reward.CharacterId == fixture.OfflineCharacterId), Is.True);
        });
    }

    /// <summary>
    /// Verifies that the defending owner is credited when no attacking guild captures the Crown.
    /// </summary>
    [Test]
    public async ValueTask RewardsCreditDefendingOwnerWithoutCrownCaptureAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);

        await CastleSiegeParticipantTracker.AwardRewardsAsync(fixture.Context).ConfigureAwait(false);

        fixture.GuildServer.Verify(
            server => server.IncreaseGuildScoreAsync(
                DefenseGuildId,
                fixture.CastleSiegeConfiguration.GuildScoreCastleSiege),
            Times.Once);
        fixture.GuildServer.Verify(
            server => server.IncreaseGuildScoreAsync(
                DefenseAllianceGuildId,
                fixture.CastleSiegeConfiguration.GuildScoreCastleSiegeMembers),
            Times.Once);
    }

    /// <summary>
    /// Verifies that overlapping player objects during a reconnect do not abort reward delivery.
    /// </summary>
    [Test]
    public async ValueTask RewardsTolerateOverlappingReconnectInstancesAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await CastleSiegeGuildSelector.SelectGuildsAsync(fixture.Context).ConfigureAwait(false);
        var firstPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "ReconnectA").ConfigureAwait(false);
        var secondPlayer = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "ReconnectB").ConfigureAwait(false);
        secondPlayer.SelectedCharacter!.Id = firstPlayer.SelectedCharacter!.Id;
        fixture.Context.MiddleOwnerGuildId = AlphaGuildId;
        fixture.Context.ParticipantTracking[firstPlayer.SelectedCharacter.Id] = new CastleSiegeParticipant
        {
            CharacterId = firstPlayer.SelectedCharacter.Id,
            CharacterName = firstPlayer.SelectedCharacter.Name,
            GuildId = AlphaGuildId,
            ParticipationTime = TimeSpan.FromSeconds(fixture.CastleSiegeConfiguration.ParticipantRewardMinSeconds),
        };

        await CastleSiegeParticipantTracker.AwardRewardsAsync(fixture.Context).ConfigureAwait(false);

        var deliveredRewards = firstPlayer.Inventory!.Items.Count(item => item.Definition == fixture.RewardItemDefinition)
                               + secondPlayer.Inventory!.Items.Count(item => item.Definition == fixture.RewardItemDefinition);
        Assert.That(deliveredRewards, Is.EqualTo(1));
    }

    /// <summary>
    /// Verifies that a queued reward is delivered and removed when the character enters the game.
    /// </summary>
    [Test]
    public async ValueTask PendingRewardIsDeliveredOnNextLoginAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var player = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "Returning").ConfigureAwait(false);
        var itemAppearView = player.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!;
        await CastleSiegeRewardDelivery
            .QueueAsync(fixture.GameServerContext, [player.SelectedCharacter!.Id], fixture.RewardItemDefinition)
            .ConfigureAwait(false);

        await new CastleSiegePendingRewardPlugIn()
            .PlayerStateChangedAsync(player, PlayerState.CharacterSelection, PlayerState.EnteredWorld)
            .ConfigureAwait(false);

        Assert.That(
            player.Inventory!.Items.Count(item => item.Definition == fixture.RewardItemDefinition),
            Is.EqualTo(1));
        Mock.Get(itemAppearView).Verify(
            plugIn => plugIn.ItemAppearAsync(It.Is<Item>(item => item.Definition == fixture.RewardItemDefinition)),
            Times.Once);
        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegePendingReward),
            false,
            fixture.GameConfiguration);
        Assert.That(
            await persistenceContext.GetAsync<CastleSiegePendingReward>().ConfigureAwait(false),
            Is.Empty);
    }

    /// <summary>
    /// Verifies that an irrecoverable pending reward is removed instead of retried on every login.
    /// </summary>
    [Test]
    public async ValueTask PendingRewardWithUnknownItemDefinitionIsDiscardedAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var player = await CreateSiegePlayerAsync(fixture, AlphaGuildId, "InvalidReward").ConfigureAwait(false);
        using (var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
                   typeof(CastleSiegePendingReward),
                   false,
                   fixture.GameConfiguration))
        {
            var pendingReward = persistenceContext.CreateNew<CastleSiegePendingReward>();
            pendingReward.CharacterId = player.SelectedCharacter!.Id;
            pendingReward.ItemDefinitionId = Guid.NewGuid();
            await persistenceContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await new CastleSiegePendingRewardPlugIn()
            .PlayerStateChangedAsync(player, PlayerState.CharacterSelection, PlayerState.EnteredWorld)
            .ConfigureAwait(false);

        using var verificationContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegePendingReward),
            false,
            fixture.GameConfiguration);
        Assert.That(
            await verificationContext.GetAsync<CastleSiegePendingReward>().ConfigureAwait(false),
            Is.Empty);
    }

    /// <summary>
    /// Verifies the two direct packet request codes handled by this issue.
    /// </summary>
    [Test]
    public void GuildListRequestHandlersUseExpectedCodes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CastleSiegeRegisteredGuildListHandlerPlugIn().Key, Is.EqualTo(0xB4));
            Assert.That(new CastleSiegeGuildListHandlerPlugIn().Key, Is.EqualTo(0xB5));
        });
    }

    private static async ValueTask<Player> CreateSiegePlayerAsync(
        TestFixture fixture,
        uint guildId,
        string characterName)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(fixture.GameServerContext).ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        player.SelectedCharacter.Name = characterName;
        player.GuildStatus = new GuildMemberStatus(guildId, GuildPosition.NormalMember);
        await fixture.GameServerContext.AddPlayerAsync(player).ConfigureAwait(false);
        await player.WarpToAsync(new ExitGate
        {
            Map = fixture.CastleSiegeMap,
            X1 = 1,
            X2 = 1,
            Y1 = 1,
            Y2 = 1,
            Direction = Direction.South,
        }).ConfigureAwait(false);
        await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        fixture.Context.TrackPlayer(player, player.CurrentMap!);
        return player;
    }

    private static async ValueTask<CastleSiegeContext> CreatePeerContextAsync(TestFixture fixture, IPersistenceContextProvider provider, CastleSiegePlugIn? plugin = null)
    {
        var game = new Mock<IGameServerContext>();
        game.SetupGet(value => value.Configuration).Returns(fixture.GameConfiguration);
        game.SetupGet(value => value.PersistenceContextProvider).Returns(provider);
        game.SetupGet(value => value.GuildServer).Returns(fixture.GuildServer.Object);
        game.SetupGet(value => value.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        game.SetupGet(value => value.LoggerFactory).Returns(NullLoggerFactory.Instance);
        game.Setup(value => value.GetPlayersAsync()).ReturnsAsync(Array.Empty<Player>());
        if (plugin is not null)
        {
            await plugin.ExecuteTaskAsync(game.Object).ConfigureAwait(false);
            return plugin.GetContext(game.Object) ?? throw new InvalidOperationException("The test Castle Siege context was not initialized.");
        }

        var context = new CastleSiegeContext(game.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(fixture.InitializationTimeUtc).ConfigureAwait(false);
        return context;
    }

    private static async ValueTask<TestFixture> CreateFixtureAsync()
    {
        var persistenceContextProvider = new InMemoryPersistenceContextProvider();
        BasicModel.GameConfiguration gameConfiguration;
        BasicModel.CastleSiegeConfiguration castleSiegeConfiguration;
        BasicModel.GameMapDefinition normalMap;
        BasicModel.GameMapDefinition castleSiegeMap;
        BasicModel.ItemDefinition rewardItemDefinition;
        Guid offlineCharacterId;
        var persistentGuildIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var initializationTimeUtc = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        using (var persistenceContext = persistenceContextProvider.CreateNewContext())
        {
            gameConfiguration = persistenceContext.CreateNew<BasicModel.GameConfiguration>();
            normalMap = persistenceContext.CreateNew<BasicModel.GameMapDefinition>();
            normalMap.Number = 0;
            normalMap.TerrainData = new byte[ushort.MaxValue + 3];
            gameConfiguration.Maps.Add(normalMap);

            castleSiegeMap = persistenceContext.CreateNew<BasicModel.GameMapDefinition>();
            castleSiegeMap.Number = 30;
            castleSiegeMap.TerrainData = new byte[ushort.MaxValue + 3];
            gameConfiguration.Maps.Add(castleSiegeMap);

            rewardItemDefinition = persistenceContext.CreateNew<BasicModel.ItemDefinition>();
            rewardItemDefinition.Group = 14;
            rewardItemDefinition.Number = 13;
            rewardItemDefinition.Width = 1;
            rewardItemDefinition.Height = 1;
            rewardItemDefinition.Durability = 1;
            gameConfiguration.Items.Add(rewardItemDefinition);

            foreach (var effectNumber in Enum.GetValues<CastleSiegeMagicEffectNumber>())
            {
                gameConfiguration.MagicEffects.Add(new BasicModel.MagicEffectDefinition
                {
                    Number = (short)effectNumber,
                });
            }

            castleSiegeConfiguration = persistenceContext.CreateNew<BasicModel.CastleSiegeConfiguration>();
            castleSiegeConfiguration.Enabled = true;
            castleSiegeConfiguration.CastleSiegeMapDefinition = castleSiegeMap;
            castleSiegeConfiguration.RewardItemDefinition = rewardItemDefinition;
            castleSiegeConfiguration.MaxAttackingGuilds = 3;
            castleSiegeConfiguration.ParticipantRewardMinSeconds = 10;
            castleSiegeConfiguration.GuildScoreCastleSiege = 2;
            castleSiegeConfiguration.GuildScoreCastleSiegeMembers = 1;
            castleSiegeConfiguration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry
            {
                State = CastleSiegeState.RegisterGuild,
                DayOfWeek = DayOfWeek.Monday,
            });
            castleSiegeConfiguration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry
            {
                State = CastleSiegeState.Notify,
                DayOfWeek = DayOfWeek.Tuesday,
            });
            gameConfiguration.CastleSiegeConfiguration = castleSiegeConfiguration;

            BasicModel.Guild AddGuild(string name)
            {
                var guild = persistenceContext.CreateNew<BasicModel.Guild>();
                guild.Name = name;
                persistentGuildIds[name] = guild.Id;
                return guild;
            }

            var defenseGuild = AddGuild("Defend");
            AddGuild("DefAlly");
            var alphaGuild = AddGuild("Alpha");
            AddGuild("AlphaAl");
            var bravoGuild = AddGuild("Bravo");
            var charlieGuild = AddGuild("Charlie");
            var deltaGuild = AddGuild("Delta");

            var siegeData = persistenceContext.CreateNew<BasicModel.CastleSiegeData>();
            siegeData.IsOccupied = true;
            siegeData.OwnerGuildId = defenseGuild.Id;

            void AddRegistration(BasicModel.Guild guild, int marks, int order)
            {
                var registration = persistenceContext.CreateNew<BasicModel.CastleSiegeGuildRegistration>();
                registration.GuildId = guild.Id;
                registration.GuildName = guild.Name!;
                registration.Marks = marks;
                registration.RegistrationOrder = order;
            }

            AddRegistration(defenseGuild, 100, 0);
            AddRegistration(alphaGuild, 10, 4);
            AddRegistration(bravoGuild, 20, 2);
            AddRegistration(charlieGuild, 20, 1);
            AddRegistration(deltaGuild, 1, 3);

            var alphaGuildMaster = persistenceContext.CreateNew<BasicModel.Character>();
            alphaGuildMaster.Name = "AlphaGM";
            alphaGuildMaster.RawAttributes.Add(new BasicModel.StatAttribute(Stats.Level, 400));
            alphaGuildMaster.RawAttributes.Add(new BasicModel.StatAttribute(Stats.MasterLevel, 100));
            var alphaAccount = persistenceContext.CreateNew<BasicModel.Account>();
            alphaAccount.LoginName = "alpha-account";
            alphaAccount.Characters.Add(alphaGuildMaster);
            var offlineCharacter = persistenceContext.CreateNew<BasicModel.Character>();
            offlineCharacter.Name = "Offline";
            offlineCharacterId = offlineCharacter.Id;
            await persistenceContext.SaveChangesAsync().ConfigureAwait(false);
        }

        var runtimeGuildIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Defend"] = DefenseGuildId,
            ["DefAlly"] = DefenseAllianceGuildId,
            ["Alpha"] = AlphaGuildId,
            ["AlphaAl"] = AlphaAllianceGuildId,
            ["Bravo"] = BravoGuildId,
            ["Charlie"] = CharlieGuildId,
            ["Delta"] = DeltaGuildId,
        };
        var persistentToRuntime = persistentGuildIds.ToDictionary(
            pair => pair.Value,
            pair => runtimeGuildIds[pair.Key]);
        var runtimeToPersistent = persistentToRuntime.ToDictionary(pair => pair.Value, pair => pair.Key);
        runtimeToPersistent[ReconnectedAlphaGuildId] = persistentGuildIds["Alpha"];
        var runtimeGuildNames = runtimeGuildIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        var guildMembers = new Dictionary<uint, List<GuildListEntry>>
        {
            [DefenseGuildId] = [new() { PlayerName = "DefenseGM", PlayerPosition = GuildPosition.GuildMaster }],
            [AlphaGuildId] =
            [
                new() { PlayerName = "AlphaGM", PlayerPosition = GuildPosition.GuildMaster },
                new() { PlayerName = "AlphaMember", PlayerPosition = GuildPosition.NormalMember },
            ],
            [BravoGuildId] = [new() { PlayerName = "BravoGM", PlayerPosition = GuildPosition.GuildMaster }],
            [CharlieGuildId] = [new() { PlayerName = "CharlieGM", PlayerPosition = GuildPosition.GuildMaster }],
            [DeltaGuildId] = [new() { PlayerName = "DeltaGM", PlayerPosition = GuildPosition.GuildMaster }],
        };
        var alliances = new Dictionary<uint, IImmutableList<AllianceGuildEntry>>
        {
            [DefenseGuildId] =
            [
                new(DefenseGuildId, "Defend", 1, Memory<byte>.Empty),
                new(DefenseAllianceGuildId, "DefAlly", 1, Memory<byte>.Empty),
            ],
            [AlphaGuildId] =
            [
                new(AlphaGuildId, "Alpha", 2, Memory<byte>.Empty),
                new(AlphaAllianceGuildId, "AlphaAl", 1, Memory<byte>.Empty),
            ],
        };
        var guildServer = new Mock<IGuildServer>();
        guildServer
            .Setup(server => server.GetGuildIdAsync(It.IsAny<Guid>()))
            .Returns<Guid>(guildId => new ValueTask<uint>(persistentToRuntime.GetValueOrDefault(guildId)));
        guildServer
            .Setup(server => server.GetPersistentGuildIdAsync(It.IsAny<uint>()))
            .Returns<uint>(guildId => new ValueTask<Guid?>(runtimeToPersistent.GetValueOrDefault(guildId)));
        guildServer
            .Setup(server => server.GetGuildAsync(It.IsAny<uint>()))
            .Returns<uint>(guildId => new ValueTask<RuntimeGuild?>(
                runtimeGuildNames.TryGetValue(guildId, out var name)
                    ? new RuntimeGuild { Name = name }
                    : null));
        guildServer
            .Setup(server => server.GetGuildListAsync(It.IsAny<uint>()))
            .Returns<uint>(guildId => new ValueTask<IImmutableList<GuildListEntry>>(
                guildMembers.GetValueOrDefault(guildId, []).ToImmutableList()));
        guildServer
            .Setup(server => server.GetAllianceGuildsAsync(It.IsAny<uint>()))
            .Returns<uint>(guildId => new ValueTask<IImmutableList<AllianceGuildEntry>>(
                alliances.GetValueOrDefault(guildId, ImmutableList<AllianceGuildEntry>.Empty)));
        guildServer
            .Setup(server => server.IncreaseGuildScoreAsync(It.IsAny<uint>(), It.IsAny<int>()))
            .Returns(ValueTask.CompletedTask);

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
            new PlugInManager([], NullLoggerFactory.Instance, null, null),
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());
        mapInitializer.PlugInManager = gameServerContext.PlugInManager;
        mapInitializer.PathFinderPool = gameServerContext.PathFinderPool;

        var context = new CastleSiegeContext(gameServerContext, castleSiegeConfiguration);
        await context.InitializeAsync(initializationTimeUtc).ConfigureAwait(false);
        return new(
            persistenceContextProvider,
            gameConfiguration,
            castleSiegeConfiguration,
            normalMap,
            castleSiegeMap,
            rewardItemDefinition,
            gameServerContext,
            context,
            guildServer,
            persistentGuildIds["Alpha"],
            offlineCharacterId,
            initializationTimeUtc);
    }

    private sealed record TestFixture(
        InMemoryPersistenceContextProvider PersistenceContextProvider,
        GameConfiguration GameConfiguration,
        CastleSiegeConfiguration CastleSiegeConfiguration,
        GameMapDefinition NormalMap,
        GameMapDefinition CastleSiegeMap,
        ItemDefinition RewardItemDefinition,
        GameServerContext GameServerContext,
        CastleSiegeContext Context,
        Mock<IGuildServer> GuildServer,
        Guid AlphaPersistentGuildId,
        Guid OfflineCharacterId,
        DateTime InitializationTimeUtc);

    private sealed class FixtureTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
