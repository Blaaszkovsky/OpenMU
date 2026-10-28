// <copyright file="CastleSiegeLoginAdmissionTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Reflection;
using System.Threading;
using System.Text.Json;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.CastleSiege.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Tests;
using NUnit.Framework;
using Moq;
using MUnique.OpenMU.DataModel.Entities;
using Basic = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>Initial Castle Siege admission and its Ready-to-Start race.</summary>
[TestFixture]
public sealed class CastleSiegeLoginAdmissionTests
{
    /// <summary>Verifies LateAttackerLoginDuringStartMustUseAttackArea.</summary>
    [Test]
    public Task LateAttackerLoginDuringStartMustUseAttackArea() => Login(CastleSiegeJoinSide.Attack1, true);

    /// <summary>Verifies LateDefenderLoginDuringStartMustUseDefenseArea.</summary>
    [Test]
    public Task LateDefenderLoginDuringStartMustUseDefenseArea() => Login(CastleSiegeJoinSide.Defense, true);

    /// <summary>Verifies OrdinaryNullGuildStatusAttackerWithPersistedMembership.</summary>
    [Test]
    public Task OrdinaryNullGuildStatusAttackerWithPersistedMembership() => Login(CastleSiegeJoinSide.Attack1, true, false);

    /// <summary>Verifies OrdinaryNullGuildStatusDefenderWithPersistedMembership.</summary>
    [Test]
    public Task OrdinaryNullGuildStatusDefenderWithPersistedMembership() => Login(CastleSiegeJoinSide.Defense, true, false);

    /// <summary>Verifies UnassignedNullGuildStatusCannotLoginAtInnerSavedPoint.</summary>
    [Test]
    public Task UnassignedNullGuildStatusCannotLoginAtInnerSavedPoint() => Login(CastleSiegeJoinSide.None, true, false);

    /// <summary>Verifies MismatchedPersistentMemberIdCannotGrantInnerAdmission.</summary>
    [Test]
    public Task MismatchedPersistentMemberIdCannotGrantInnerAdmission() => Login(CastleSiegeJoinSide.None, true, false, mismatch: true);

    /// <summary>Verifies OtherMapLoginDuringStartUnchanged.</summary>
    [Test]
    public Task OtherMapLoginDuringStartUnchanged() => Login(CastleSiegeJoinSide.None, true, false, otherMap: true);

    /// <summary>Verifies ExistingMiniGameMapAdmissionUnchanged.</summary>
    [Test]
    public Task ExistingMiniGameMapAdmissionUnchanged() => Login(CastleSiegeJoinSide.None, true, false, miniGame: true);

    /// <summary>Verifies ReadyToStartWhileCharacterInformationIsSuspended.</summary>
    [Test]
    public Task ReadyToStartWhileCharacterInformationIsSuspended() => Login(CastleSiegeJoinSide.Attack1, true, false, race: true);

    /// <summary>Verifies PostAddMissingAreaFailsClosed.</summary>
    [Test]
    public Task PostAddMissingAreaFailsClosed() => Login(CastleSiegeJoinSide.Attack1, true, false, race: true, fault: "missing");

    /// <summary>Verifies PostAddBlockedAreaFailsClosed.</summary>
    [Test]
    public Task PostAddBlockedAreaFailsClosed() => Login(CastleSiegeJoinSide.Attack1, true, false, race: true, fault: "blocked");

    /// <summary>Verifies ConsumedAdmissionAllowsReentrantOrdinarySideRespawn.</summary>
    [Test]
    public Task ConsumedAdmissionAllowsReentrantOrdinarySideRespawn() => Login(CastleSiegeJoinSide.Attack1, true, true, reentrant: true);

    /// <summary>Verifies CompletedAdmissionDoesNotRelocateLaterOrdinaryMovement.</summary>
    [Test]
    public Task CompletedAdmissionDoesNotRelocateLaterOrdinaryMovement() => Login(CastleSiegeJoinSide.Attack1, true, false, postMove: true);

    /// <summary>Verifies PreHookUninitializedSiegeDisconnectsWithoutPacket.</summary>
    [Test]
    public Task PreHookUninitializedSiegeDisconnectsWithoutPacket() => Login(CastleSiegeJoinSide.Attack1, true, false, preFault: "init");

    /// <summary>Verifies PreHookMissingAreaDisconnectsWithoutPacket.</summary>
    [Test]
    public Task PreHookMissingAreaDisconnectsWithoutPacket() => Login(CastleSiegeJoinSide.Attack1, true, false, preFault: "missing");

    /// <summary>Verifies LoginOutsideBattlePreservesWalkableSavedPoint.</summary>
    [Test]
    public Task LoginOutsideBattlePreservesWalkableSavedPoint() => Login(CastleSiegeJoinSide.Attack1, false);

    private static async Task Login(CastleSiegeJoinSide side, bool battle, bool runtimeGuildKnown = true, bool mismatch = false, bool otherMap = false, bool miniGame = false, bool race = false, string? fault = null, bool reentrant = false, bool postMove = false, string? preFault = null)
    {
        var fixture = await CastleSiegeNpcTests.CreateFixtureAsync().ConfigureAwait(false);
        object Property(string name) => fixture.GetType().GetProperty(name)!.GetValue(fixture)!;
        var game = (GameContext)Property("GameServerContext");
        var definition = (Basic.CastleSiegeConfiguration)Property("Configuration");
        definition.AttackRespawnArea = new Basic.CastleSiegeZoneDefinition { X1 = 35, Y1 = 11, X2 = 144, Y2 = 48 };
        definition.DefenseRespawnArea = new Basic.CastleSiegeZoneDefinition { X1 = 74, Y1 = 144, X2 = 115, Y2 = 154 };
        var source = (Player)Property("Player");
        var character = source.SelectedCharacter!;
        character.Name = "LateLogin";
        character.CurrentMap = otherMap ? game.Configuration.Maps.First(m => m.Number == 0) : definition.CastleSiegeMapDefinition;
        character.PositionX = 86;
        character.PositionY = 192;
        character.Attributes.Single(a => a.Definition == Stats.CurrentHealth).Value = 42;
        var retainedItem = new Basic.Item { Definition = (Basic.ItemDefinition)Property("JewelOfGuardian"), Durability = 3, ItemSlot = MUnique.OpenMU.DataModel.InventoryConstants.EquippableSlotsCount };
        character.Inventory!.Items.Add(retainedItem);
        var retainedInventory = character.Inventory.Items.ToArray();
        var accountMock = new Mock<Account>();
        accountMock.SetupAllProperties();
        accountMock.Setup(a => a.Characters).Returns(new List<Character> { character });
        accountMock.Setup(a => a.Attributes).Returns(new List<MUnique.OpenMU.AttributeSystem.StatAttribute>());
        accountMock.Setup(a => a.UnlockedCharacterClasses).Returns(new List<CharacterClass>());
        var account = accountMock.Object;
        account.LoginName = "ordinary";
        account.IsBot = false;
        var now = battle && !race ? new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var plugin = (CastleSiegePlugIn)Activator.CreateInstance(typeof(CastleSiegePlugIn), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new FixedTime(now) }, null)!;
        // The existing fixture manager([]) discovers default instances. Replace, do not add a
        // second uninitialized CastleSiege instance at the new entering-world plugin point.
        game.PlugInManager.DeactivatePlugIn(typeof(CastleSiegePlugIn));
        game.PlugInManager.RegisterPlugInAtPlugInPoint<IObjectAddedToMapPlugIn>(plugin);
        game.PlugInManager.RegisterPlugInAtPlugInPoint<IObjectRemovedFromMapPlugIn>(plugin);
        game.PlugInManager.RegisterPlugInAtPlugInPoint<IPlayerStateChangedPlugIn>(plugin);
        game.PlugInManager.RegisterPlugInAtPlugInPoint<IPlayerSpawnGateSelectionPlugIn>(plugin);
        // Same executable supports RED1136 and the isolated proposal, with the real plugin registry.
        if (typeof(Player).Assembly.GetType("MUnique.OpenMU.GameLogic.PlugIns.IPlayerEnteringWorldPlugIn") is { } admissionPoint)
        {
            typeof(PlugInManager).GetMethod("RegisterPlugInAtPlugInPoint")!.MakeGenericMethod(admissionPoint).Invoke(game.PlugInManager, new object[] { plugin });
        }
        await plugin.ExecuteTaskAsync(game).ConfigureAwait(false);
        var context = plugin.GetContext(game)!;
        var executionLock = (SemaphoreSlim)typeof(CastleSiegeContext).GetProperty("ExecutionLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        const uint runtimeGuild = 123;
        var persistentGuild = Guid.NewGuid();
        if (side != CastleSiegeJoinSide.None || mismatch)
        {
            context.FinalGuildList[runtimeGuild] = new CastleSiegeGuildParticipant { GuildId = runtimeGuild, GuildName = "Ordinary", PersistentGuildId = persistentGuild, Side = mismatch ? CastleSiegeJoinSide.Defense : side };
        }
        using (var persistence = game.PersistenceContextProvider.CreateNewContext())
        {
            // Real in-memory membership lookup joins Character(Name/Id) to GuildMember(Id).
            // This identity-only stored row is not the mocked playable character's gameplay graph.
            var storedIdentity = persistence.CreateNew<Basic.Character>();
            storedIdentity.Name = character.Name;
            character.Id = mismatch ? Guid.NewGuid() : storedIdentity.Id;
            if (side != CastleSiegeJoinSide.None || mismatch)
            {
                var membership = persistence.CreateNew<Basic.GuildMember>();
                membership.Id = storedIdentity.Id;
                membership.GuildId = persistentGuild;
                membership.Status = GuildPosition.GuildMaster;
            }
            await persistence.SaveChangesAsync().ConfigureAwait(false);
        }
        using (var guildContext = game.PersistenceContextProvider.CreateNewGuildContext())
        {
            var membership = await guildContext.GetGuildMembershipAsync(character.Name).ConfigureAwait(false);
            Assert.That(membership?.GuildId, side == CastleSiegeJoinSide.None && !mismatch ? Is.Null : Is.EqualTo(persistentGuild));
            if (membership is not null) Assert.That(membership.CharacterId, mismatch ? Is.Not.EqualTo(character.Id) : Is.EqualTo(character.Id));
        }
        var player = new OrdinaryPlayer(game) { Account = account, GuildStatus = runtimeGuildKnown ? new GuildMemberStatus(runtimeGuild, GuildPosition.GuildMaster) : null };
        player.GateCharacterInformation = race || reentrant;
        MiniGameContext? miniGameContext = null;
        if (miniGame)
        {
            var miniMap = new Basic.GameMapDefinition { Number = 50, TerrainData = new byte[65_539] };
            var miniDefinition = new Basic.MiniGameDefinition { Entrance = new Basic.ExitGate { Map = miniMap }, EnterDuration = TimeSpan.FromMinutes(5) };
            var miniMapInitializer = new Mock<IMapInitializer>();
            miniMapInitializer.Setup(m => m.CreateGameMap(miniMap)).Returns(new GameMap(miniMap, TimeSpan.FromMinutes(1), 8));
            miniGameContext = new MiniGameContext(new MiniGameMapKey(50, 1, "test"), miniDefinition, game, miniMapInitializer.Object);
            player.CurrentMiniGame = miniGameContext;
        }
        try
        {
            await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            var map = await game.GetMapAsync(30).ConfigureAwait(false) ?? throw new InvalidOperationException("Map missing");
            Assert.That(map.Terrain.WalkMap[86, 192], Is.True, "Saved inner point must be walkable; blocked recovery is not this test");
            if (battle)
            {
                Assert.That(context.CurrentState, Is.EqualTo(race ? CastleSiegeState.Ready : CastleSiegeState.Start));
                Assert.That(context.ActiveNpcs.Any(npc => npc.SpawnedInstance is CastleSiegeGate { IsClosed: true, IsAlive: true }), Is.True, "Actual Ready/Start production gate spawning and terrain close required");
            }
            if (preFault is not null)
            {
                if (preFault == "missing") definition.AttackRespawnArea = null;
                if (preFault == "init") typeof(CastleSiegeContext).GetProperty("IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, false);
                try { await new SelectCharacterAction().SelectCharacterAsync(player, character.Name).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                Assert.Multiple(() =>
                {
                    Assert.That(player.IsConnected, Is.False);
                    Assert.That(player.SelectedCharacter, Is.Null);
                    Assert.That(player.CurrentMap, Is.Null);
                    Assert.That(map.GetPlayers().Any(p => ReferenceEquals(p, player)), Is.False);
                    Assert.That(player.CharacterInformationPositions, Is.Empty);
                    Assert.That(character.Inventory!.Items, Is.EqualTo(retainedInventory));
                    Assert.That(retainedItem.Durability, Is.EqualTo(3));
                });
                // Recovery is a fresh ordinary connection, not a second selection on a Finished player.
                definition.AttackRespawnArea = new Basic.CastleSiegeZoneDefinition { X1 = 35, Y1 = 11, X2 = 144, Y2 = 48 };
                typeof(CastleSiegeContext).GetProperty("IsInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, true);
                player = new OrdinaryPlayer(game) { Account = account };
                await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
                await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
                await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
                await new SelectCharacterAction().SelectCharacterAsync(player, character.Name).ConfigureAwait(false);
                Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
                Assert.That(player.Position.Y, Is.InRange((byte)11, (byte)48));
                Assert.That(player.CharacterInformationPositions.Single().Y, Is.InRange((byte)11, (byte)48));
                Assert.That(player.Attributes![Stats.CurrentHealth], Is.EqualTo(42));
                Assert.That(character.Inventory!.Items, Is.EqualTo(retainedInventory));
                return;
            }
            if (race)
            {
                var selecting = new SelectCharacterAction().SelectCharacterAsync(player, character.Name).AsTask();
                await player.CharacterInformationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.That(player.CurrentMap, Is.Null, "Start must occur before Map.Add tracks this pending admission");
                plugin.ForceStart();
                await plugin.ExecuteTaskAsync(game).ConfigureAwait(false);
                Assert.That(context.CurrentState, Is.EqualTo(CastleSiegeState.Start));
                if (fault == "missing") definition.AttackRespawnArea = null;
                if (fault == "blocked")
                {
                    for (var x = 35; x <= 144; x++) for (var y = 11; y <= 48; y++) map.Terrain.WalkMap[x, y] = false;
                }
                player.CharacterInformationRelease.TrySetResult();
                try { await selecting.ConfigureAwait(false); }
                catch (Exception ex) when (fault is not null) { TestContext.WriteLine($"EXPECTED CLOSED ADMISSION ERROR {fault}: {ex.GetType().Name}"); }
                if (fault is not null)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(player.IsConnected, Is.False);
                        Assert.That(player.CurrentMap, Is.Null);
                        Assert.That(player.SelectedCharacter, Is.Null);
                        Assert.That(map.GetPlayers().Any(p => ReferenceEquals(p, player)), Is.False);
                        Assert.That(executionLock.CurrentCount, Is.EqualTo(1), "Disconnect must run after execution lock release");
                        Assert.That(character.Inventory!.Items, Is.EqualTo(retainedInventory));
                        Assert.That(retainedItem.Durability, Is.EqualTo(3));
                    });
                    return;
                }
            }
            else if (reentrant)
            {
                var selecting = new SelectCharacterAction().SelectCharacterAsync(player, character.Name).AsTask();
                await player.CharacterInformationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await executionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    player.CharacterInformationRelease.TrySetResult();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var pendingField = typeof(CastleSiegePlugIn).GetField("_initialAdmissions", BindingFlags.Instance | BindingFlags.NonPublic);
                    bool HasMarker()
                    {
                        if (pendingField?.GetValue(plugin) is not { } table) return false;
                        return (bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, new object?[] { player, null })!;
                    }
                    bool IsTracked() => ((IReadOnlyList<Player>)typeof(CastleSiegeContext).GetMethod("GetSiegePlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(context, null)!).Contains(player);
                    while (!map.GetPlayers().Any(p => ReferenceEquals(p, player)) || HasMarker() || !IsTracked()) await Task.Delay(1, deadline.Token).ConfigureAwait(false);
                    // The real Start body respawns tracked players while holding ExecutionLock.
                    // A nested ObjectAdded callback must not reacquire that same lock.
                    var move = typeof(CastleSiegePlugIn).GetMethod("MovePlayersToTheirSidesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                    var moving = (ValueTask)move.Invoke(null, new object[] { context })!;
                    await moving.AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.That(player.Respawns, Is.GreaterThan(0), "The normal side respawn must actually re-enter Map.Add");
                }
                finally { executionLock.Release(); }
                await selecting.ConfigureAwait(false);
            }
            else await new SelectCharacterAction().SelectCharacterAsync(player, character.Name).ConfigureAwait(false);
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(player.CurrentMap?.Definition.Number, Is.EqualTo(miniGame ? 50 : otherMap ? 0 : 30));
            Assert.That(account.IsBot, Is.False);
            Assert.That(player.Attributes![Stats.CurrentHealth], Is.EqualTo(42), "Admission must not refill HP");
            Assert.That(character.Inventory!.Items, Is.EqualTo(retainedInventory));
            Assert.That(retainedItem.Durability, Is.EqualTo(3));
            Assert.That(player.CurrentMap!.GetPlayers().Count(p => ReferenceEquals(p, player)), Is.EqualTo(1), "Exactly one map registration");
            if (runtimeGuildKnown) Assert.That(context.GetPlayerJoinSide(player), Is.EqualTo(side));
            TestContext.WriteLine($"OBSERVED {side} battle={battle} runtimeGuildKnown={runtimeGuildKnown}: actual={player.Position} selected=({character.PositionX},{character.PositionY}) resolvedSide={context.GetPlayerJoinSide(player)} closed={context.ActiveNpcs.Count(npc => npc.SpawnedInstance is CastleSiegeGate { IsClosed: true })}");
            if (battle && !otherMap && !miniGame)
            {
                var area = side == CastleSiegeJoinSide.Defense ? definition.DefenseRespawnArea! : definition.AttackRespawnArea!;
                Assert.Multiple(() =>
                {
                    Assert.That(player.Position.X, Is.InRange(area.X1, area.X2));
                    Assert.That(player.Position.Y, Is.InRange(area.Y1, area.Y2));
                    if (!race) Assert.That(player.CharacterInformationPositions.First().Y, Is.InRange(area.Y1, area.Y2), "The first character information must already carry the admitted location");
                    else Assert.That(player.InstantMoves, Is.GreaterThan(0), "The racing admission must notify its own client of the corrected position");
                });
            }
            else { Assert.That((player.Position.X, player.Position.Y), Is.EqualTo(((byte)86, (byte)192))); }
            if (postMove)
            {
                await player.MoveAsync(new MUnique.OpenMU.Pathfinding.Point(86, 192)).ConfigureAwait(false);
                await plugin.ObjectAddedToMapAsync(map, player).ConfigureAwait(false);
                Assert.That(player.Position, Is.EqualTo(new MUnique.OpenMU.Pathfinding.Point(86, 192)), "One-shot admission cannot affect later map callbacks");
            }
        }
        finally
        {
            if (player.CurrentMap is { } map) await map.RemoveAsync(player).ConfigureAwait(false);
            player.CurrentMiniGame = null;
            if (miniGameContext is not null) await miniGameContext.DisposeAsync().ConfigureAwait(false);
            await context.NpcController.DespawnAllAsync().ConfigureAwait(false);
            var originalContext = (CastleSiegeContext)Property("Context");
            await originalContext.NpcController.DespawnAllAsync().ConfigureAwait(false);
        }
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(utc); }
    private sealed class OrdinaryPlayer(IGameContext context) : Player(context)
    {
        public List<MUnique.OpenMU.Pathfinding.Point> CharacterInformationPositions { get; } = new();
        public bool GateCharacterInformation { get; set; }
        public TaskCompletionSource CharacterInformationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CharacterInformationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InstantMoves { get; set; }
        public int Respawns { get; set; }
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new CapturingViews(this);
        private sealed class CapturingViews(OrdinaryPlayer player) : ICustomPlugInContainer<IViewPlugIn>
        {
            private readonly MockViewPlugInContainer fallback = new();
            public T GetPlugIn<T>() where T : class, IViewPlugIn
            {
                if (typeof(T) == typeof(IRespawnAfterDeathPlugIn))
                {
                    var respawn = new Mock<IRespawnAfterDeathPlugIn>();
                    respawn.Setup(m => m.RespawnAsync()).Returns(() => { player.Respawns++; return ValueTask.CompletedTask; });
                    return (T)(object)respawn.Object;
                }
                if (typeof(T) == typeof(MUnique.OpenMU.GameLogic.Views.World.IObjectMovedPlugIn))
                {
                    var moved = new Mock<MUnique.OpenMU.GameLogic.Views.World.IObjectMovedPlugIn>();
                    moved.Setup(m => m.ObjectMovedAsync(player, MoveType.Instant)).Returns(() => { player.InstantMoves++; return ValueTask.CompletedTask; });
                    return (T)(object)moved.Object;
                }
                if (typeof(T) != typeof(IUpdateCharacterStatsPlugIn)) return fallback.GetPlugIn<T>();
                var mock = new Mock<IUpdateCharacterStatsPlugIn>();
                mock.Setup(m => m.UpdateCharacterStatsAsync()).Returns(async () =>
                {
                    player.CharacterInformationPositions.Add(player.Position);
                    if (player.GateCharacterInformation)
                    {
                        player.CharacterInformationEntered.TrySetResult();
                        await player.CharacterInformationRelease.Task.ConfigureAwait(false);
                    }
                });
                return (T)(object)mock.Object;
            }
        }
    }
}
