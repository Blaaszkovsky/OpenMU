// <copyright file="IllusionTempleContextTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.Pathfinding;
using Moq;
using World = MUnique.OpenMU.Tests.MiniGames.IllusionTempleLifecycleTests.TestWorld;

/// <summary>Runs the upstream event scenarios against the canonical event, through its real player and map actions.</summary>
[TestFixture]
[Timeout(90_000)]
public class IllusionTempleContextTest
{
    /// <summary>Entrance admission stops when the ordinary lobby wait ends.</summary>
    [Test]
    public async ValueTask EntranceAcceptsPlayersOnlyWhileOpenAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.OpenRegistrationAsync().ConfigureAwait(false);
        var resultWhileOpen = await world.Game.TryEnterAsync(world.Players[0]).ConfigureAwait(false);
        await WaitUntilAsync(() =>
        {
            world.Game.SkipCurrentWait();
            return world.Game.State != MiniGameState.Open;
        }).ConfigureAwait(false);
        var resultAfterClose = await world.Game.TryEnterAsync(world.Players[1]).ConfigureAwait(false);
        Assert.That(resultWhileOpen, Is.EqualTo(EnterResult.Success));
        Assert.That(resultAfterClose, Is.EqualTo(EnterResult.NotOpen));
    }

    /// <summary>An ordinary living participant leaving below the configured minimum ends the event.</summary>
    [Test]
    public async ValueTask FinishesWhenTooFewPlayersRemainAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        await world.Game.Map.RemoveAsync(world.Players[0]).ConfigureAwait(false);
        Assert.That(world.Game.PlayerCount, Is.LessThan(2));
        await WaitUntilAsync(() => world.Game.State != MiniGameState.Playing).ConfigureAwait(false);
        Assert.That(world.Game.State, Is.Not.EqualTo(MiniGameState.Playing));
    }

    /// <summary>Even participant counts produce equal teams.</summary>
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(10)]
    public async ValueTask SplitsAnEvenPlayerCountIntoEqualTeamsAsync(int playerCount)
    {
        await using var world = await World.CreateAsync(playerCount).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var counts = world.Players.GroupBy(player => world.Game.GetTeam(player)).ToDictionary(group => group.Key, group => group.Count());
        Assert.That(counts.Values, Has.All.EqualTo(playerCount / 2));
        Assert.That(counts.Keys, Is.EquivalentTo(new byte[] { 0, 1 }));
    }

    /// <summary>Odd participant counts differ by one between teams.</summary>
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(9)]
    public async ValueTask SplitsAnOddPlayerCountAsEvenlyAsPossibleAsync(int playerCount)
    {
        await using var world = await World.CreateAsync(playerCount).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var counts = world.Players.GroupBy(player => world.Game.GetTeam(player)).Select(group => group.Count()).ToArray();
        Assert.That(counts.Sum(), Is.EqualTo(playerCount));
        Assert.That(Math.Abs(counts.Max() - counts.Min()), Is.LessThanOrEqualTo(1));
    }

    /// <summary>Each team member receives a configured team gate.</summary>
    [Test]
    public async ValueTask GameStartAssignsEveryPlayerASpawnGateAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        foreach (var player in world.Players)
        {
            Assert.That(world.Game.GetSpawnGate(player), Is.Not.Null);
        }
    }

    /// <summary>The real statue channel grants a relic and announces its carrier.</summary>
    [Test]
    public async ValueTask TalkingToTheStatueGrantsTheRelicAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var player = world.Players[0];
        await CaptureAsync(world, player).ConfigureAwait(false);
        Assert.That(HasRelic(player), Is.True);
        Assert.That(Carrier(world), Is.SameAs(player));
    }

    /// <summary>A real opposing attack drops the carrier's relic, which another player can pick up normally.</summary>
    [Test]
    public async ValueTask DyingDropsTheRelicAndItCanBePickedUpAgainAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var carrier = world.Players[0];
        var other = world.Players[1];
        await CaptureAsync(world, carrier).ConfigureAwait(false);
        other.Position = carrier.Position;
        carrier.Attributes![Stats.CurrentHealth] = 1;
        carrier.Attributes[Stats.CurrentShield] = 0;
        await carrier.AttackByAsync(other, null, false).ConfigureAwait(false);
        Assert.That(carrier.IsAlive, Is.False);
        await WaitUntilAsync(() => Drops(world).Count > 0 && Carrier(world) is null).ConfigureAwait(false);
        Assert.That(HasRelic(carrier), Is.False);
        Assert.That(Carrier(world), Is.Null);
        var drop = Drops(world).Single();
        Assert.That(drop, Is.Not.Null);
        other.Position = drop.Position;
        await new PickupItemAction().PickupItemAsync(other, drop.Id).ConfigureAwait(false);
        await WaitUntilAsync(() => Carrier(world) == other).ConfigureAwait(false);
        Assert.That(HasRelic(other), Is.True);
        Assert.That(Carrier(world), Is.SameAs(other));
    }

    /// <summary>A completed deposit at the carrier's own podium scores once and releases the relic.</summary>
    [Test]
    public async ValueTask DeliveringTheRelicScoresAPointAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var carrier = world.Players.Single(player => world.Game.GetTeam(player) == 0);
        await CaptureAsync(world, carrier).ConfigureAwait(false);
        await DepositAsync(world, carrier).ConfigureAwait(false);
        await WaitUntilAsync(() => Score(world, 0) == 1 && Carrier(world) is null).ConfigureAwait(false);
        Assert.That(Score(world, 0), Is.EqualTo(1));
        Assert.That(HasRelic(carrier), Is.False);
        Assert.That(Carrier(world), Is.Null);
    }

    /// <summary>The enemy podium cannot consume the relic or score for either team.</summary>
    [Test]
    public async ValueTask DeliveringTheRelicToTheEnemyStorageDoesNotScoreAsync()
    {
        await using var world = await World.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var carrier = world.Players.Single(player => world.Game.GetTeam(player) == 0);
        await CaptureAsync(world, carrier).ConfigureAwait(false);
        var enemyPodium = Npcs(world).Single(npc => npc.Definition.Number == 384);
        carrier.Position = enemyPodium.Position;
        await world.Game.TalkToEventNpcAsync(carrier, enemyPodium).ConfigureAwait(false);
        Assert.That(Score(world, 0), Is.Zero);
        Assert.That(Score(world, 1), Is.Zero);
        Assert.That(HasRelic(carrier), Is.True);
        Assert.That(Carrier(world), Is.SameAs(carrier));
    }

    /// <summary>The natural two-point finish rewards only the winning team.</summary>
    [Test]
    public async ValueTask GameEndGrantsExperienceToTheWinningTeamAsync()
    {
        await using var world = await World.CreateAsync(2, generateRewardItems: true).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var winner = world.Players.Single(player => world.Game.GetTeam(player) == 0);
        var loser = world.Players.Single(player => world.Game.GetTeam(player) == 1);
        var winnerBefore = winner.SelectedCharacter!.Experience;
        var loserBefore = loser.SelectedCharacter!.Experience;
        await WinAsync(world, winner).ConfigureAwait(false);
        Assert.That(winner.SelectedCharacter.Experience, Is.GreaterThan(winnerBefore));
        Assert.That(loser.SelectedCharacter.Experience, Is.EqualTo(loserBefore));
        await world.Game.ClaimRewardAsync(winner).ConfigureAwait(false);
        await world.Game.ClaimRewardAsync(loser).ConfigureAwait(false);
    }

    /// <summary>The fork auto-awards its configured item before results; repeated close requests cannot award it again.</summary>
    [Test]
    public async ValueTask GameEndGrantsExperienceAndAnItemRewardToTheWinnerAsync()
    {
        await using var world = await World.CreateAsync(2, generateRewardItems: true).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var winner = world.Players.Single(player => world.Game.GetTeam(player) == 0);
        var loser = world.Players.Single(player => world.Game.GetTeam(player) == 1);
        var itemDefinition = world.Definition.Rewards.Single(reward => reward.RewardType == MiniGameRewardType.Item).ItemReward!.PossibleItems.Single();
        var experienceBefore = winner.SelectedCharacter!.Experience;
        await WinAsync(world, winner).ConfigureAwait(false);
        Assert.That(winner.SelectedCharacter.Experience, Is.GreaterThan(experienceBefore));
        Assert.That(winner.Inventory!.Items.Count(item => item.Definition == itemDefinition), Is.EqualTo(1));
        Assert.That(loser.Inventory!.Items.Any(item => item.Definition == itemDefinition), Is.False);
        var rewardedExperience = winner.SelectedCharacter.Experience;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await world.Game.ClaimRewardAsync(winner).ConfigureAwait(false);
            await world.Game.ClaimRewardAsync(loser).ConfigureAwait(false);
        }

        Assert.That(winner.Inventory.Items.Count(item => item.Definition == itemDefinition), Is.EqualTo(1));
        Assert.That(loser.Inventory.Items.Any(item => item.Definition == itemDefinition), Is.False);
        Assert.That(winner.SelectedCharacter.Experience, Is.EqualTo(rewardedExperience));
    }

    /// <summary>The real Mirage dialog remains open and reports all six event counts.</summary>
    [Test]
    public async ValueTask TalkingToTheEntranceNpcShowsTheUserCountsAsync()
    {
        await using var world = await World.CreateAsync(1).ConfigureAwait(false);
        var player = world.Players[0];
        var spawn = new MUnique.OpenMU.Persistence.BasicModel.MonsterSpawnArea
        {
            GameMap = player.CurrentMap!.Definition,
            X1 = player.Position.X, X2 = player.Position.X,
            Y1 = player.Position.Y, Y2 = player.Position.Y,
        };
        var npc = new NonPlayerCharacter(spawn, world.Context.Configuration.Monsters.Single(definition => definition.Number == 385), player.CurrentMap);
        npc.Initialize();
        await player.CurrentMap.AddAsync(npc).ConfigureAwait(false);
        await new TalkNpcAction().TalkToNpcAsync(player, npc).ConfigureAwait(false);
        Assert.That(player.OpenedNpc, Is.SameAs(npc));
        var counts = LastArguments(world, nameof(IIllusionTempleViewPlugIn.ShowPlayerCountsAsync));
        Assert.That(counts, Is.Not.Null);
        Assert.That((IReadOnlyList<byte>)counts![0], Has.Count.EqualTo(6));
    }

    private static bool HasRelic(Player player) => player.Inventory!.Items.Any(item => item.Definition is { Group: 14, Number: 64 });

    private static IList<NonPlayerCharacter> Npcs(World world) => world.Game.Map.GetNpcsInRange(new Point(128, 128), 255);

    private static IList<DroppedItem> Drops(World world) => world.Game.Map.GetDropsInRange(new Point(128, 128), 255)
        .OfType<DroppedItem>().Where(drop => drop.Item.Definition is { Group: 14, Number: 64 }).ToList();

    private static IReadOnlyList<object>? LastArguments(World world, string method)
        => Mock.Get(world.Players[0].ViewPlugIns.GetPlugIn<IIllusionTempleViewPlugIn>()!).Invocations.ToArray()
            .LastOrDefault(invocation => invocation.Method.Name == method)?.Arguments;

    private static Player? Carrier(World world) => (Player?)LastArguments(world, nameof(IIllusionTempleViewPlugIn.ShowStateAsync))?[1];

    private static byte Score(World world, byte team)
    {
        var result = LastArguments(world, nameof(IIllusionTempleViewPlugIn.ShowResultAsync));
        if (result is not null)
        {
            return (byte)result[team];
        }

        return (byte)(LastArguments(world, nameof(IIllusionTempleViewPlugIn.ShowStateAsync))?[3 + team] ?? (byte)0);
    }

    private static async ValueTask CaptureAsync(World world, Player player)
    {
        var statue = Npcs(world).First(npc => npc.Definition.Number == 380);
        player.Position = statue.Position;
        await world.Game.TalkToEventNpcAsync(player, statue).ConfigureAwait(false);
        await WaitUntilAsync(() => HasRelic(player) && Carrier(world) == player).ConfigureAwait(false);
    }

    private static async ValueTask DepositAsync(World world, Player player)
    {
        var podium = Npcs(world).Single(npc => npc.Definition.Number == 383 + world.Game.GetTeam(player));
        player.Position = podium.Position;
        await world.Game.TalkToEventNpcAsync(player, podium).ConfigureAwait(false);
        await WaitUntilAsync(() => !HasRelic(player)).ConfigureAwait(false);
    }

    private static async ValueTask WinAsync(World world, Player winner)
    {
        for (var score = 0; score < 2; score++)
        {
            await CaptureAsync(world, winner).ConfigureAwait(false);
            await DepositAsync(world, winner).ConfigureAwait(false);
        }

        await WaitUntilAsync(() => LastArguments(world, nameof(IIllusionTempleViewPlugIn.ShowResultAsync)) is not null).ConfigureAwait(false);
        Assert.That(world.Game.State, Is.EqualTo(MiniGameState.Ended));
    }

    private static async ValueTask WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, "The real event action must complete within its bounded fixture timeout.");
    }
}
