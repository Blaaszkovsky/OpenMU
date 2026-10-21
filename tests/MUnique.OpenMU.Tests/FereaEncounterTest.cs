// <copyright file="FereaEncounterTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Ferea;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MonsterAttribute = MUnique.OpenMU.Persistence.BasicModel.MonsterAttribute;
using MonsterDefinition = MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition;
using MonsterSpawnArea = MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea;

/// <summary>
/// Exercises real map objects, damage paths, phase transitions and cleanup with a controlled clock.
/// </summary>
[TestFixture]
public class FereaEncounterTest
{
    private GameContext _gameContext = null!;
    private GameMap _map = null!;
    private Player _player = null!;
    private FereaEncounter _encounter = null!;
    private TestClock _clock = null!;
    private Mock<IDropGenerator> _dropGenerator = null!;

    /// <summary>Creates a server and a minimal encounter.</summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        this._dropGenerator = new Mock<IDropGenerator>(MockBehavior.Strict);
        this._gameContext = (GameContext)GameContextTestHelper.CreateGameContext(dropGenerator: this._dropGenerator.Object);
        this._map = (await this._gameContext.GetMapAsync(0).ConfigureAwait(false))!;
        this._clock = new TestClock();
        var configuration = new FereaEncounterConfiguration
        {
            MapNumber = 0,
            VictoryItemDropCount = 0,
            RegenerationPercentPerPlayer = 1,
            VictoryCooldownMinimum = TimeSpan.FromSeconds(10),
            VictoryCooldownMaximum = TimeSpan.FromSeconds(10),
        };
        AddSpawn(this._map, 735, configuration.OrbWave, 30, 120);
        AddSpawn(this._map, 730, configuration.GuardianWave, 28, 120);
        AddSpawn(this._map, 734, configuration.LordWave, 30, 120);
        AddSpawn(this._map, 733, configuration.GeneralWave, 38, 120);
        AddSpawn(this._map, 732, configuration.ReinforcementWave, 40, 120);
        this._encounter = new FereaEncounter(this._gameContext, configuration, this._clock);
        this._player = await this.CreatePlayerAsync(new Point(31, 120)).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
    }

    /// <summary>Releases timers and map objects after every test.</summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        await this._encounter.DisposeAsync().ConfigureAwait(false);
        foreach (var player in this._map.GetPlayers())
        {
            await this._map.RemoveAsync(player).ConfigureAwait(false);
            await player.DisposeAsync().ConfigureAwait(false);
        }

        await this._gameContext.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Verifies guardians protect the passive Orb and its death closes entry.</summary>
    [Test]
    public async Task GuardiansProtectOrbAndDeathStartsBattleAsync()
    {
        var orb = this.GetMonster(735);
        await orb.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        Assert.That(orb.Health, Is.EqualTo(1000));
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Orb));
        var gate = new MUnique.OpenMU.DataModel.Configuration.ExitGate
        {
            Map = this._map.Definition, X1 = 46, X2 = 46, Y1 = 122, Y2 = 122,
        };
        Assert.That(this._encounter.CanEnter(this._player, gate), Is.True);

        await this.StartBattleAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Battle));
        Assert.That(this._encounter.CanEnter(this._player, gate), Is.False);
        Assert.That(this.GetMonsters().Count(m => m.Definition.Number == 734), Is.EqualTo(1));
    }

    /// <summary>Verifies lethal bursts stop at a health gate and all damage paths obey General protection.</summary>
    [Test]
    public async Task GeneralProtectsLordAndRegenerationStopsWhenKilledAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        var lord = this._encounter.Lord!;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => lord.ReflectDamageAsync(this._player, 10000).AsTask())).ConfigureAwait(false);
        Assert.That(lord.Health, Is.EqualTo(750));

        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.General));
        await lord.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await lord.ApplyBleedingDamageAsync(this._player, 10000).ConfigureAwait(false);
        await lord.ReflectDamageAsync(this._player, 10000).ConfigureAwait(false);
        Assert.That(lord.Health, Is.EqualTo(750));

        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(lord.Health, Is.EqualTo(760));
        await this._encounter.General!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(lord.Health, Is.EqualTo(760));
        await lord.ApplyBleedingDamageAsync(this._player, 10).ConfigureAwait(false);
        Assert.That(lord.Health, Is.EqualTo(750));
        Assert.That(this._encounter.Phase, Is.EqualTo(1));
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Battle));
    }

    /// <summary>Verifies health gates happen once even after healing, and victory cleans up all summons.</summary>
    [Test]
    public async Task AllPhasesCompleteAndVictoryReopensAfterCooldownAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        var lord = this._encounter.Lord!;
        for (var phase = 1; phase <= 3; phase++)
        {
            await lord.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
            Assert.That(this._encounter.Phase, Is.EqualTo(phase));
            lord.RecoverHealth(1000);
            Assert.That(lord.Health, Is.EqualTo(1000));
            await this._encounter.General!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
        }

        await lord.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Cooldown));
        Assert.That(this.GetMonsters(), Is.Empty);
        Assert.That(this._player.Position, Is.EqualTo(new Point(31, 120)));
        this._clock.Advance(TimeSpan.FromSeconds(9));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this.GetMonsters(), Is.Empty);
        Assert.That(this._player.Position, Is.EqualTo(new Point(31, 120)));
        var outsider = await this.CreatePlayerAsync(new Point(31, 120)).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(outsider.Position, Is.EqualTo(new Point(54, 122)));
        Assert.That(this._player.Position, Is.EqualTo(new Point(31, 120)));
        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Orb));
        Assert.That(this.GetMonsters().Count, Is.EqualTo(2));
        Assert.That(this._encounter.Phase, Is.Zero);
    }

    /// <summary>Verifies continuous absence, including dead players, resets the encounter.</summary>
    /// <param name="die">Whether the participant dies rather than leaves.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AbandonmentResetsAfterSixtySecondsAsync(bool die)
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        if (die)
        {
            this._player.IsAlive = false;
        }
        else
        {
            await this._map.RemoveAsync(this._player).ConfigureAwait(false);
        }

        await this._encounter.TickAsync().ConfigureAwait(false);
        this._clock.Advance(TimeSpan.FromSeconds(59));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.Lord, Is.Not.Null);
        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Cooldown));
        Assert.That(this.GetMonsters(), Is.Empty);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Orb));
    }

    /// <summary>Verifies late arrivals cannot damage monsters and are moved outside the arena.</summary>
    [Test]
    public async Task LateArrivalCannotFightAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        var outsider = await this.CreatePlayerAsync(new Point(31, 120)).ConfigureAwait(false);
        await this._encounter.Lord!.ApplyPoisonDamageAsync(outsider, 100).ConfigureAwait(false);
        Assert.That(this._encounter.Lord.Health, Is.EqualTo(1000));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(outsider.Position, Is.EqualTo(new Point(54, 122)));
    }

    /// <summary>Verifies the existing client can enter by stepping into the server-side entrance trigger.</summary>
    [Test]
    public async Task EntranceMovesPlayerIntoArenaOnlyWhileOpenAsync()
    {
        await this._player.MoveAsync(new Point(112, 60)).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._player.Position, Is.EqualTo(new Point(46, 122)));
        await this.StartBattleAsync().ConfigureAwait(false);
        await this._player.MoveAsync(new Point(112, 60)).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._player.Position, Is.EqualTo(new Point(112, 60)));
    }

    /// <summary>Verifies incomplete wave data fails closed and can be repaired without a server restart.</summary>
    [Test]
    public async Task MissingSpawnCleansUpAndCanBeRetriedAsync()
    {
        await this._encounter.DisposeAsync().ConfigureAwait(false);
        this._map.Definition.MonsterSpawns.Remove(this._map.Definition.MonsterSpawns.Single(s => s.WaveNumber == 22));
        this._encounter = new FereaEncounter(this._gameContext, new FereaEncounterConfiguration { MapNumber = 0 }, this._clock);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await this._encounter.TickAsync().ConfigureAwait(false));
        Assert.That(this.GetMonsters(), Is.Empty);
        AddSpawn(this._map, 734, 22, 30, 120);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Orb));
    }

    /// <summary>Verifies disabled rewards cannot reach the item or money generator, including its delayed execution.</summary>
    [Test]
    public async Task EncounterDoesNotGenerateItemsOrZenAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        this._encounter.Lord!.Health = 1;
        for (var phase = 0; phase < 3; phase++)
        {
            await this._encounter.Lord.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
            await this._encounter.General!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
        }

        await this._encounter.Lord.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        this._dropGenerator.VerifyNoOtherCalls();
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Cooldown));
    }

    /// <summary>Verifies one configured item roll and WCoin award happen only once after victory.</summary>
    /// <param name="minimum">The inclusive minimum award.</param>
    /// <param name="maximum">The inclusive maximum award.</param>
    [TestCase(7, 7)]
    [TestCase(1, 3)]
    [TestCase(int.MaxValue, int.MaxValue)]
    public async Task VictoryAwardsConfiguredRewardsOnceAsync(int minimum, int maximum)
    {
        this._encounter.Configuration.VictoryItemDropCount = 1;
        this._encounter.Configuration.VictoryWCoinCChance = 1;
        this._encounter.Configuration.VictoryWCoinCMinimum = minimum;
        this._encounter.Configuration.VictoryWCoinCMaximum = maximum;
        var item = new MUnique.OpenMU.Persistence.BasicModel.Item
        {
            Definition = new MUnique.OpenMU.Persistence.BasicModel.ItemDefinition { Width = 1, Height = 1 },
        };
        this._dropGenerator.Setup(g => g.GenerateItemDrop(It.IsAny<IEnumerable<MUnique.OpenMU.DataModel.Configuration.DropItemGroup>>()))
            .Returns((item, null, MUnique.OpenMU.DataModel.Configuration.ItemDropEffect.Undefined));
        await this.StartBattleAsync().ConfigureAwait(false);
        await this.DefeatLordAsync().ConfigureAwait(false);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => this._encounter.TickAsync().AsTask())).ConfigureAwait(false);
        var drop = this._map.GetDropsInRange(new Point(30, 120), 255).OfType<DroppedItem>().Single();
        Assert.That(drop.Item, Is.SameAs(item));
        Assert.That(drop.IsPlayerAnOwner(this._player), Is.True);
        var outsider = await this.CreatePlayerAsync(new Point(31, 120)).ConfigureAwait(false);
        Assert.That(drop.IsPlayerAnOwner(outsider), Is.False);
        Assert.That(this._player.Account!.WCoinC, Is.InRange(minimum, maximum));
        this._dropGenerator.Verify(g => g.GenerateItemDrop(It.IsAny<IEnumerable<MUnique.OpenMU.DataModel.Configuration.DropItemGroup>>()), Times.Once);
        this._dropGenerator.VerifyNoOtherCalls();
    }

    /// <summary>Verifies an item-generation failure cannot duplicate coins or retain summoned monsters.</summary>
    [Test]
    public async Task ItemRewardFailureStillCompletesVictoryOnceAsync()
    {
        this._encounter.Configuration.VictoryItemDropCount = 1;
        this._encounter.Configuration.VictoryWCoinCChance = 1;
        this._encounter.Configuration.VictoryWCoinCMinimum = 1;
        this._encounter.Configuration.VictoryWCoinCMaximum = 1;
        this._dropGenerator.Setup(g => g.GenerateItemDrop(It.IsAny<IEnumerable<MUnique.OpenMU.DataModel.Configuration.DropItemGroup>>()))
            .Throws(new InvalidOperationException("Test reward generation failure."));
        await this.StartBattleAsync().ConfigureAwait(false);
        await this.DefeatLordAsync().ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Cooldown));
        Assert.That(this.GetMonsters(), Is.Empty);
        Assert.That(this._player.Account!.WCoinC, Is.EqualTo(1));
        this._dropGenerator.Verify(g => g.GenerateItemDrop(It.IsAny<IEnumerable<MUnique.OpenMU.DataModel.Configuration.DropItemGroup>>()), Times.Once);
        this._dropGenerator.VerifyNoOtherCalls();
    }

    /// <summary>Verifies resets never award items or coins, even with rewards enabled.</summary>
    [Test]
    public async Task FailedBattleDoesNotAwardRewardsAsync()
    {
        this._encounter.Configuration.VictoryItemDropCount = 1;
        this._encounter.Configuration.VictoryWCoinCChance = 1;
        this._encounter.Configuration.VictoryWCoinCMinimum = 7;
        this._encounter.Configuration.VictoryWCoinCMaximum = 7;
        await this.StartBattleAsync().ConfigureAwait(false);
        this._clock.Advance(this._encounter.Configuration.BattleTimeout);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._player.Account!.WCoinC, Is.Zero);
        Assert.That(this._map.GetDropsInRange(new Point(30, 120), 255), Is.Empty);
        this._dropGenerator.VerifyNoOtherCalls();
    }

    /// <summary>Verifies dead and revoked participants do not receive rewards and balances cannot overflow.</summary>
    [Test]
    public async Task OnlySurvivingParticipantsReceiveCoinsAsync()
    {
        this._encounter.Configuration.VictoryWCoinCChance = 1;
        this._encounter.Configuration.VictoryWCoinCMinimum = 7;
        this._encounter.Configuration.VictoryWCoinCMaximum = 7;
        var dead = await this.CreatePlayerAsync(new Point(32, 120)).ConfigureAwait(false);
        var departed = await this.CreatePlayerAsync(new Point(33, 120)).ConfigureAwait(false);
        await this.StartBattleAsync().ConfigureAwait(false);
        dead.IsAlive = false;
        this._encounter.Revoke(departed);
        this._player.Account!.WCoinC = int.MaxValue - 1;
        await this.DefeatLordAsync().ConfigureAwait(false);
        Assert.That(this._player.Account.WCoinC, Is.EqualTo(int.MaxValue));
        Assert.That(dead.Account!.WCoinC, Is.Zero);
        Assert.That(departed.Account!.WCoinC, Is.Zero);
    }

    /// <summary>Verifies no regeneration is awarded when Roar hits no participants.</summary>
    [Test]
    public async Task RoarWithoutVictimsDoesNotHealAsync()
    {
        await this._player.MoveAsync(new Point(46, 130)).ConfigureAwait(false);
        await this.StartBattleAsync().ConfigureAwait(false);
        await this._encounter.Lord!.ReflectDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.Lord.Health, Is.EqualTo(750));
    }

    /// <summary>Verifies missing reinforcements fail closed before any monsters are spawned.</summary>
    [Test]
    public async Task MissingReinforcementWaveFailsClosedAsync()
    {
        await this._encounter.DisposeAsync().ConfigureAwait(false);
        this._map.Definition.MonsterSpawns.Remove(this._map.Definition.MonsterSpawns.Single(s => s.WaveNumber == 24));
        this._encounter = new FereaEncounter(this._gameContext, new FereaEncounterConfiguration { MapNumber = 0 }, this._clock);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await this._encounter.TickAsync().ConfigureAwait(false));
        Assert.That(this.GetMonsters(), Is.Empty);
    }

    /// <summary>Verifies live data updates cannot make the ordinary initializer spawn encounter waves.</summary>
    [Test]
    public async Task LiveWaveAdditionDoesNotSpawnBossAutomaticallyAsync()
    {
        var initializer = new MapInitializer(
            this._gameContext.Configuration,
            NullLogger<MapInitializer>.Instance,
            this._dropGenerator.Object,
            this._gameContext.ConfigurationChangeMediator)
        {
            PlugInManager = this._gameContext.PlugInManager,
            PathFinderPool = this._gameContext.PathFinderPool,
        };
        await initializer.InitializeStateAsync(this._map).ConfigureAwait(false);
        var spawn = this._map.Definition.MonsterSpawns.Single(s => s.WaveNumber == 22);
        var listener = (IConfigurationChangeMediatorListener)this._gameContext.ConfigurationChangeMediator;
        await listener.HandleConfigurationAddedAsync(spawn.GetType(), Guid.NewGuid(), spawn).ConfigureAwait(false);
        Assert.That(this.GetMonsters().Any(m => m.Definition.Number == 734), Is.False);
    }

    /// <summary>Verifies a departed participant remains excluded after returning during the same fight.</summary>
    [Test]
    public async Task RevokedParticipantCannotReenterBattleAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        this._encounter.Revoke(this._player);
        await this._encounter.Lord!.ReflectDamageAsync(this._player, 1000).ConfigureAwait(false);
        Assert.That(this._encounter.Lord.Health, Is.EqualTo(1000));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._player.Position, Is.EqualTo(new Point(54, 122)));
    }

    /// <summary>Verifies the battle deadline also resets an arena with living participants.</summary>
    [Test]
    public async Task BattleTimeoutCleansUpLivingEncounterAsync()
    {
        await this.StartBattleAsync().ConfigureAwait(false);
        this._clock.Advance(TimeSpan.FromMinutes(30));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.State, Is.EqualTo(FereaEncounterState.Cooldown));
        Assert.That(this.GetMonsters(), Is.Empty);
    }

    /// <summary>Verifies Roar drains only nearby characters and recovery scales with its victims.</summary>
    [Test]
    public async Task RoarRecoveryCountsNearbyVictimsAsync()
    {
        var near = await this.CreatePlayerAsync(new Point(32, 120)).ConfigureAwait(false);
        var far = await this.CreatePlayerAsync(new Point(46, 130)).ConfigureAwait(false);
        near.Attributes![Stats.CurrentMana] = 10;
        far.Attributes![Stats.CurrentMana] = 10;
        await this.StartBattleAsync().ConfigureAwait(false);
        await this._encounter.Lord!.ReflectDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(near.Attributes[Stats.CurrentMana], Is.Zero);
        Assert.That(far.Attributes[Stats.CurrentMana], Is.EqualTo(10));
        this._clock.Advance(TimeSpan.FromSeconds(1));
        await this._encounter.TickAsync().ConfigureAwait(false);
        Assert.That(this._encounter.Lord.Health, Is.EqualTo(770));
    }

    private static void AddSpawn(GameMap map, short number, byte wave, byte x, byte y)
    {
        var monster = new MonsterDefinition
        {
            Id = Guid.NewGuid(), Number = number,
            ObjectKind = MUnique.OpenMU.DataModel.Configuration.NpcObjectKind.Monster,
            AttackDelay = TimeSpan.FromDays(1), MoveDelay = TimeSpan.FromSeconds(1),
            AttackRange = 2, ViewRange = 10,
        };
        monster.Attributes.Add(new MonsterAttribute { AttributeDefinition = Stats.MaximumHealth, Value = 1000 });
        map.Definition.MonsterSpawns.Add(new MonsterSpawnArea
        {
            Id = Guid.NewGuid(), MonsterDefinition = monster, GameMap = map.Definition, Quantity = 1,
            SpawnTrigger = MUnique.OpenMU.DataModel.Configuration.SpawnTrigger.OnceAtWaveStart,
            WaveNumber = wave, X1 = x, X2 = x, Y1 = y, Y2 = y,
        });
    }

    private async ValueTask<Player> CreatePlayerAsync(Point point)
    {
        var player = await PlayerTestHelper.CreatePlayerAsync(this._gameContext).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        player.IsAlive = true;
        player.Position = point;
        await this._map.AddAsync(player).ConfigureAwait(false);
        return player;
    }

    private async ValueTask StartBattleAsync()
    {
        await this.GetMonster(730).ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this.GetMonster(735).ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
    }

    private async ValueTask DefeatLordAsync()
    {
        for (var phase = 0; phase < 3; phase++)
        {
            await this._encounter.Lord!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
            await this._encounter.General!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
            await this._encounter.TickAsync().ConfigureAwait(false);
        }

        await this._encounter.Lord!.ApplyPoisonDamageAsync(this._player, 10000).ConfigureAwait(false);
        await this._encounter.TickAsync().ConfigureAwait(false);
    }

    private Monster GetMonster(short number) => this.GetMonsters().Single(m => m.Definition.Number == number);

    private List<Monster> GetMonsters() => this._map.GetNpcsInRange(new Point(30, 120), 255).OfType<Monster>().ToList();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this._now;

        public void Advance(TimeSpan duration) => this._now += duration;
    }
}
