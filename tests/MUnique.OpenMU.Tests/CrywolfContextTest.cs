// <copyright file="CrywolfContextTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Crywolf;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
using MonsterAttribute = MUnique.OpenMU.Persistence.BasicModel.MonsterAttribute;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;

/// <summary>
/// Tests the run of the <see cref="CrywolfContext"/>.
/// </summary>
[TestFixture]
public class CrywolfContextTest
{
    private static readonly Point AltarPosition = new(125, 27);

    /// <summary>
    /// Defends the contracted altar through actual army and Balgass deaths, then checks rewards, persistence, and cleanup.
    /// </summary>
    [Test]
    public async Task DefendedBattleRewardsHeroesOnceAndRestoresMapAsync()
    {
        var (context, contractor, gameContext) = await CreateContextAsync(configure: definition =>
        {
            definition.BalgassAppearanceDelay = TimeSpan.Zero;
            definition.EndDuration = TimeSpan.Zero;
            definition.EndCycleDuration = TimeSpan.Zero;
            definition.MonsterGroups.Add(new CrywolfMonsterGroup { WaveNumber = 1 });
        }).ConfigureAwait(false);
        await using var ownedGameContext = gameContext;
        using var ownedEvent = context;
        gameContext.StopPeriodicTasks();
        gameContext.Configuration.MaximumLevel = 400;
        gameContext.Configuration.ExperienceFormula = "level * 1000000000";
        await ((IConfigurationChangeMediatorListener)gameContext.ConfigurationChangeMediator).HandleConfigurationChangedAsync(typeof(GameConfiguration), gameContext.Configuration.GetId(), gameContext.Configuration).ConfigureAwait(false);
        var map = context.Map!;
        contractor.SelectedCharacter!.Name = "CwElf";
        var fighter = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        fighter.SelectedCharacter!.Name = "CwFighter";
        await fighter.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        fighter.IsAlive = true;
        fighter.Position = new Point(110, 79);
        await map.AddAsync(fighter).ConfigureAwait(false);
        await gameContext.AddPlayerAsync(contractor).ConfigureAwait(false);
        await gameContext.AddPlayerAsync(fighter).ConfigureAwait(false);
        var rewardDefinition = new Persistence.BasicModel.ItemDefinition { Group = 14, Number = 13 };
        gameContext.Configuration.Items.Add(rewardDefinition);
        var leaderDefinition = new MonsterDefinition { Id = Guid.NewGuid(), Number = 340, ObjectKind = NpcObjectKind.Monster, AttackDelay = TimeSpan.FromHours(1) };
        leaderDefinition.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 100 });
        map.Definition.MonsterSpawns.Add(new Persistence.BasicModel.MonsterSpawnArea
        {
            MonsterDefinition = leaderDefinition,
            GameMap = map.Definition,
            X1 = 110,
            X2 = 110,
            Y1 = 78,
            Y2 = 78,
            Quantity = 1,
            SpawnTrigger = SpawnTrigger.OnceAtWaveStart,
            WaveNumber = 1,
        });
        var contractorView = Mock.Get(contractor.ViewPlugIns.GetPlugIn<ICrywolfEventViewPlugIn>()!);
        var fighterView = Mock.Get(fighter.ViewPlugIns.GetPlugIn<ICrywolfEventViewPlugIn>()!);
        var contractorExperience = contractor.SelectedCharacter.Experience;
        var fighterExperience = fighter.SelectedCharacter.Experience;

        await ProceedToAsync(context, CrywolfState.Ready).ConfigureAwait(false);
        var leader = map.GetNpcsInRange(new Point(110, 78), 1).OfType<Monster>().Single();
        await context.ContractAltarAsync(contractor, context.Altars[0].Npc.Id).ConfigureAwait(false);
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.Altars[0].State, Is.EqualTo(CrywolfAltarState.Contracted));
        await ProceedToAsync(context, CrywolfState.Start).ConfigureAwait(false);
        await leader.ReflectDamageAsync(fighter, 1000).ConfigureAwait(false);
        Assert.That(leader.IsAlive, Is.False);
        await context.TickAsync().ConfigureAwait(false);
        var balgass = map.GetNpcsInRange(new Point(110, 79), 3).OfType<Monster>().Single(monster => monster.Definition.Number == 349);
        Assert.That(balgass.IsAlive, Is.True);
        await balgass.ReflectDamageAsync(fighter, 1000).ConfigureAwait(false);
        Assert.That(balgass.IsAlive, Is.False);
        await context.TickAsync().ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(context.State, Is.EqualTo(CrywolfState.End));
            Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Peace));
            Assert.That(context.AreBenefitsApplied, Is.True);
            Assert.That(map.GetDropsInRange(AltarPosition, byte.MaxValue).OfType<DroppedItem>().Count(item => item.Item.Definition == rewardDefinition), Is.EqualTo(1), "The contractor receives the battle reward.");
        });
        contractorView.Verify(view => view.ShowPersonalRankAsync(3, 1_200_000), Times.Once);
        fighterView.Verify(view => view.ShowPersonalRankAsync(3, 1_200_000), Times.Once);
        Assert.That(contractor.SelectedCharacter.Experience - contractorExperience, Is.EqualTo(1_200_000));
        Assert.That(fighter.SelectedCharacter.Experience - fighterExperience, Is.EqualTo(1_200_000));
        fighterView.Verify(view => view.ShowHeroListAsync(It.Is<IReadOnlyList<CrywolfHero>>(heroes => heroes.Count == 2 && heroes[0].Name == "CwFighter" && heroes[0].Score == 10000 && heroes[1].Name == "CwElf" && heroes[1].Score == 6000)), Times.Once);
        var data = await LoadDataAsync(gameContext).ConfigureAwait(false);
        Assert.That(data, Is.Not.Null);
        Assert.That(data!.IsOccupied, Is.False);
        Assert.That(data.IsWarRunning, Is.False);
        Assert.That(data.LastBattleEnd, Is.Not.Null);

        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(CrywolfState.EndCycle));
        Assert.That(map.GetNpcsInRange(new Point(110, 79), byte.MaxValue).OfType<Monster>().Any(monster => monster.IsAlive), Is.False);
        Assert.That(map.GetDropsInRange(AltarPosition, byte.MaxValue).OfType<DroppedItem>().Count(item => item.Item.Definition == rewardDefinition), Is.EqualTo(3), "Each of the two heroes receives one additional reward.");
        await context.TickAsync().ConfigureAwait(false);
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(CrywolfState.None));
        Assert.That(map.GetDropsInRange(AltarPosition, byte.MaxValue).OfType<DroppedItem>().Count(item => item.Item.Definition == rewardDefinition), Is.EqualTo(3));
        contractorView.Verify(view => view.ShowPersonalRankAsync(3, 1_200_000), Times.Once);
        fighterView.Verify(view => view.ShowPersonalRankAsync(3, 1_200_000), Times.Once);
    }

    /// <summary>
    /// Tests that the battle is lost, when no altar is contracted when it starts, and that the occupation is saved in the database.
    /// </summary>
    [Test]
    public async Task BattleIsLostWithoutContractedAltarAsync()
    {
        var (context, _, gameContext) = await CreateContextAsync().ConfigureAwait(false);

        await ProceedToAsync(context, CrywolfState.Start).ConfigureAwait(false);

        Assert.That(context.State, Is.EqualTo(CrywolfState.End));
        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Occupied));
        using var persistenceContext = gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CrywolfData), false, gameContext.Configuration);
        var data = (await persistenceContext.GetAsync<CrywolfData>().ConfigureAwait(false)).ToList();
        Assert.That(data, Has.Count.EqualTo(1), "The occupation is saved.");
        Assert.That(data[0].IsOccupied, Is.True);
        Assert.That(data[0].IsWarRunning, Is.False);
        Assert.That(data[0].LastBattleEnd, Is.Not.Null);
    }

    /// <summary>
    /// Tests that the start of the war is saved, so that the other game servers take it over.
    /// </summary>
    [Test]
    public async Task WarIsSavedForTheOtherServersAsync()
    {
        var (context, _, gameContext) = await CreateContextAsync().ConfigureAwait(false);

        await ProceedToAsync(context, CrywolfState.Notify2).ConfigureAwait(false);

        var data = await LoadDataAsync(gameContext).ConfigureAwait(false);
        Assert.That(data?.IsWarRunning, Is.True);
        Assert.That(data?.IsOccupied, Is.False);
    }

    /// <summary>
    /// Tests that the saved occupation is loaded, so that it survives a restart of the server.
    /// </summary>
    [Test]
    public async Task SavedOccupationIsLoadedAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data => data.IsOccupied = true).ConfigureAwait(false);

        var (context, _, _) = await CreateContextAsync(gameContext).ConfigureAwait(false);

        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Occupied));
    }

    /// <summary>
    /// Tests that a war, which was interrupted by a restart of the server, is reset, and the result of the last battle stays.
    /// </summary>
    [Test]
    public async Task InterruptedWarIsResetAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data => data.IsWarRunning = true).ConfigureAwait(false);

        var (context, _, _) = await CreateContextAsync(gameContext).ConfigureAwait(false);

        Assert.That(context.State, Is.EqualTo(CrywolfState.None));
        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Peace));
        var data = await LoadDataAsync(gameContext).ConfigureAwait(false);
        Assert.That(data?.IsWarRunning, Is.False);
    }

    /// <summary>
    /// Tests that a game server, which doesn't run the event, takes over the war of the game server which runs it,
    /// and doesn't start the event by itself.
    /// </summary>
    [Test]
    public async Task OtherServerTakesOverTheWarAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data => data.IsWarRunning = true).ConfigureAwait(false);

        var (context, _, _) = await CreateContextAsync(gameContext, serverId: 1).ConfigureAwait(false);
        context.SkipWaitingTime();
        await context.TickAsync().ConfigureAwait(false);

        Assert.That(context.IsEventServer, Is.False);
        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.War));
        Assert.That(context.State, Is.EqualTo(CrywolfState.None));
        Assert.That(context.AreBenefitsApplied, Is.False);
        Assert.That(context.ArePenaltiesApplied, Is.False);
    }

    /// <summary>
    /// Tests that a game server, which takes over the event, e.g. after a change of the configuration,
    /// resets the war of the previous game server instead of staying in it.
    /// </summary>
    [Test]
    public async Task TakeOverResetsTheWarOfTheOtherServerAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data => data.IsWarRunning = true).ConfigureAwait(false);
        var (context, _, _) = await CreateContextAsync(gameContext, serverId: 1).ConfigureAwait(false);
        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.War));

        var definition = context.Definition;
        definition.GameServerId = 1;
        context.UpdateDefinition(definition);
        await context.TickAsync().ConfigureAwait(false);

        Assert.That(context.IsEventServer, Is.True);
        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Peace));
        var data = await LoadDataAsync(gameContext).ConfigureAwait(false);
        Assert.That(data?.IsWarRunning, Is.False);
    }

    /// <summary>
    /// Tests that the other game servers don't stay in the war, when the game server which runs the event
    /// stopped during the war, and that they fall back to the result of the last battle.
    /// </summary>
    [Test]
    public async Task OverdueWarEndsOnTheOtherServersAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data =>
        {
            data.IsWarRunning = true;
            data.WarStart = DateTime.UtcNow - TimeSpan.FromHours(2);
            data.IsOccupied = true;
            data.LastBattleEnd = DateTime.UtcNow - TimeSpan.FromDays(3);
        }).ConfigureAwait(false);

        var (context, _, _) = await CreateContextAsync(gameContext, serverId: 1).ConfigureAwait(false);

        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Occupied));
    }

    /// <summary>
    /// Tests that the terrain of the map is replaced by its variant of the occupation state during the war,
    /// and that the normal terrain is loaded again when the fortress is in peace.
    /// </summary>
    [Test]
    public async Task WarUsesTheTerrainOfTheWarAsync()
    {
        var (context, _, _) = await CreateContextAsync().ConfigureAwait(false);
        var map = context.Map!;
        var blocked = new Point(100, 100);
        var warTerrain = new byte[(256 * 256) + 3];
        warTerrain[3 + blocked.X + (blocked.Y << 8)] = (byte)TerrainAttributeType.Blocked;
        map.Definition.TerrainVariants.Add(new Persistence.BasicModel.GameMapTerrainVariant { Number = (short)CrywolfOccupationState.War, TerrainData = warTerrain });
        Assert.That(map.Terrain.WalkMap[blocked.X, blocked.Y], Is.True, "precondition");

        await ProceedToAsync(context, CrywolfState.Notify2).ConfigureAwait(false);
        Assert.That(map.Terrain.WalkMap[blocked.X, blocked.Y], Is.False);
    }

    /// <summary>
    /// Tests that the terrain of the map is replaced by its variant of the peace, when the fortress is in peace.
    /// </summary>
    [Test]
    public async Task PeaceUsesTheTerrainOfPeaceAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        var map = (await gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        var blocked = new Point(100, 100);
        var peaceTerrain = new byte[(256 * 256) + 3];
        peaceTerrain[3 + blocked.X + (blocked.Y << 8)] = (byte)TerrainAttributeType.Blocked;
        map.Definition.TerrainVariants.Add(new Persistence.BasicModel.GameMapTerrainVariant { Number = (short)CrywolfOccupationState.Peace, TerrainData = peaceTerrain });
        Assert.That(map.Terrain.WalkMap[blocked.X, blocked.Y], Is.True, "precondition");

        var (context, _, _) = await CreateContextAsync(gameContext).ConfigureAwait(false);

        Assert.That(context.Occupation, Is.EqualTo(CrywolfOccupationState.Peace));
        Assert.That(map.Terrain.WalkMap[blocked.X, blocked.Y], Is.False);
    }

    /// <summary>
    /// Tests that the benefits apply after the fortress has been defended, but not before the first battle.
    /// </summary>
    [Test]
    public async Task BenefitsApplyAfterTheFortressHasBeenDefendedAsync()
    {
        var (withoutBattle, _, _) = await CreateContextAsync().ConfigureAwait(false);
        Assert.That(withoutBattle.ChaosRateBenefit, Is.Zero);
        Assert.That(withoutBattle.MonsterHealthMultiplier, Is.EqualTo(1f));

        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data => data.LastBattleEnd = DateTime.UtcNow).ConfigureAwait(false);
        var (context, _, _) = await CreateContextAsync(gameContext).ConfigureAwait(false);

        Assert.That(context.ChaosRateBenefit, Is.EqualTo(5));
        Assert.That(context.MonsterHealthMultiplier, Is.EqualTo(0.9f));
        Assert.That(context.ExperienceMultiplier, Is.EqualTo(1f));
    }

    /// <summary>
    /// Tests that the penalties apply while the fortress is occupied, when they're activated.
    /// </summary>
    [Test]
    public async Task PenaltiesApplyWhileOccupiedAsync()
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        await SaveDataAsync(gameContext, data =>
        {
            data.IsOccupied = true;
            data.LastBattleEnd = DateTime.UtcNow;
        }).ConfigureAwait(false);

        var (context, _, _) = await CreateContextAsync(gameContext, configure: d =>
        {
            d.IsPenaltyActive = true;
            d.ExperiencePenaltyPercentage = 80;
        }).ConfigureAwait(false);

        Assert.That(context.ArePenaltiesApplied, Is.True);
        Assert.That(context.ExperienceMultiplier, Is.EqualTo(0.8f));
        Assert.That(context.ChaosRateBenefit, Is.Zero);
        Assert.That(context.MonsterHealthMultiplier, Is.EqualTo(1f));
    }

    /// <summary>
    /// Tests that an altar can't be contracted while the event isn't running.
    /// </summary>
    [Test]
    public async Task AltarCantBeContractedOutsideTheEventAsync()
    {
        var (context, player, _) = await CreateContextAsync().ConfigureAwait(false);
        var altar = context.Altars[0];

        await context.ContractAltarAsync(player, altar.Npc.Id).ConfigureAwait(false);

        Assert.That(altar.State, Is.EqualTo(CrywolfAltarState.Free));
        Assert.That(altar.ContractCount, Is.Zero);
    }

    /// <summary>
    /// Tests that a contracted altar keeps the battle running, and that a repeated request doesn't change the contract.
    /// </summary>
    [Test]
    public async Task ContractedAltarKeepsTheBattleRunningAsync()
    {
        var (context, player, _) = await CreateContextAsync().ConfigureAwait(false);
        var altar = context.Altars[0];

        await ProceedToAsync(context, CrywolfState.Ready).ConfigureAwait(false);
        await context.ContractAltarAsync(player, altar.Npc.Id).ConfigureAwait(false);
        await context.ContractAltarAsync(player, altar.Npc.Id).ConfigureAwait(false);
        Assert.That(altar.ContractCount, Is.EqualTo(1), "The repeated request doesn't count as contract.");

        await context.TickAsync().ConfigureAwait(false);
        Assert.That(altar.State, Is.EqualTo(CrywolfAltarState.Contracted));

        await ProceedToAsync(context, CrywolfState.Start).ConfigureAwait(false);
        await context.TickAsync().ConfigureAwait(false);
        Assert.That(context.State, Is.EqualTo(CrywolfState.Start));
        Assert.That(context.ArmyStage, Is.EqualTo(CrywolfArmyStage.Advancing));
    }

    /// <summary>
    /// Tests that the game master command lets Balgass appear during the battle, instead of ending it.
    /// </summary>
    [Test]
    public async Task SkipDuringTheBattleLetsBalgassAppearAsync()
    {
        var (context, player, _) = await CreateContextAsync().ConfigureAwait(false);
        var altar = context.Altars[0];

        await ProceedToAsync(context, CrywolfState.Ready).ConfigureAwait(false);
        await context.ContractAltarAsync(player, altar.Npc.Id).ConfigureAwait(false);
        await context.TickAsync().ConfigureAwait(false);
        await ProceedToAsync(context, CrywolfState.Start).ConfigureAwait(false);

        context.SkipWaitingTime();
        await context.TickAsync().ConfigureAwait(false);

        Assert.That(context.State, Is.EqualTo(CrywolfState.Start));
        Assert.That(context.ArmyStage, Is.EqualTo(CrywolfArmyStage.AttackingStatue));
        var balgass = context.Map!.GetNpcsInRange(new Point(110, 79), 3).OfType<Monster>().FirstOrDefault(monster => monster.Definition.Number == 349);
        Assert.That(balgass, Is.Not.Null);
    }

    private static async ValueTask ProceedToAsync(CrywolfContext context, CrywolfState state)
    {
        for (var i = 0; i < 10 && context.State != state; i++)
        {
            context.SkipWaitingTime();
            await context.TickAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask SaveDataAsync(GameContext gameContext, Action<CrywolfData> change)
    {
        using var persistenceContext = gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CrywolfData), false, gameContext.Configuration);
        change(persistenceContext.CreateNew<CrywolfData>());
        await persistenceContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async ValueTask<CrywolfData?> LoadDataAsync(GameContext gameContext)
    {
        using var persistenceContext = gameContext.PersistenceContextProvider.CreateNewTypedContext(typeof(CrywolfData), false, gameContext.Configuration);
        return (await persistenceContext.GetAsync<CrywolfData>().ConfigureAwait(false)).SingleOrDefault();
    }

    private static async ValueTask<(CrywolfContext Context, Player Player, GameContext GameContext)> CreateContextAsync(
        GameContext? gameContext = null,
        byte? serverId = null,
        Action<CrywolfEventDefinition>? configure = null)
    {
        gameContext ??= (GameContext)GameContextTestHelper.CreateGameContext();
        var map = (await gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        var definition = new CrywolfEventDefinition
        {
            MapNumber = 0,
            ContractCharacterClassNumbers = new List<byte> { 0 },
            MinimumContractLevel = 0,
            ContractDelay = TimeSpan.Zero,
            MonsterGroups = new List<CrywolfMonsterGroup>(),
        };
        configure?.Invoke(definition);

        await AddNpcAsync(map, definition.StatueNumber, new Point(121, 31)).ConfigureAwait(false);
        for (var i = 0; i < definition.AltarNumbers.Count; i++)
        {
            await AddNpcAsync(map, definition.AltarNumbers[i], new Point((byte)(AltarPosition.X - (i * 2)), AltarPosition.Y)).ConfigureAwait(false);
        }

        var balgass = new MonsterDefinition { Id = Guid.NewGuid(), Number = 349, ObjectKind = NpcObjectKind.Monster, AttackDelay = TimeSpan.FromHours(1) };
        balgass.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 100 });
        map.Definition.MonsterSpawns.Add(new Persistence.BasicModel.MonsterSpawnArea
        {
            MonsterDefinition = balgass,
            GameMap = map.Definition,
            X1 = 110,
            X2 = 110,
            Y1 = 79,
            Y2 = 79,
            Quantity = 1,
            SpawnTrigger = SpawnTrigger.OnceAtWaveStart,
            WaveNumber = definition.BalgassWaveNumber,
        });

        var context = new CrywolfContext(gameContext, definition, serverId);
        await context.InitializeAsync().ConfigureAwait(false);

        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        player.IsAlive = true;
        player.Position = AltarPosition;
        await player.CurrentMap!.AddAsync(player).ConfigureAwait(false);
        return (context, player, gameContext);
    }

    private static async ValueTask AddNpcAsync(GameMap map, short number, Point position)
    {
        var npcDefinition = new MonsterDefinition { Id = Guid.NewGuid(), Number = number, ObjectKind = NpcObjectKind.PassiveNpc };
        var spawnArea = new MonsterSpawnArea
        {
            MonsterDefinition = npcDefinition,
            GameMap = map.Definition,
            X1 = position.X,
            X2 = position.X,
            Y1 = position.Y,
            Y2 = position.Y,
            Quantity = 1,
        };
        var npc = new NonPlayerCharacter(spawnArea, npcDefinition, map);
        npc.Initialize();
        await map.AddAsync(npc).ConfigureAwait(false);
    }
}
