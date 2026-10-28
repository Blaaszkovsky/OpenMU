// <copyright file="IllusionTempleConfigurationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies that fresh Season 6 data describes the original Illusion Temple event instead of Renewal NPCs.
/// </summary>
[TestFixture]
internal class IllusionTempleConfigurationTests
{
    /// <summary>
    /// The six tickets must select distinct level brackets and the correct lobby gate.
    /// </summary>
    /// <param name="level">The event level.</param>
    /// <param name="minimum">The minimum regular level.</param>
    /// <param name="maximum">The maximum regular level.</param>
    /// <param name="requiresMaster">Whether a master class is required.</param>
    [TestCase(1, 220, 270, false)]
    [TestCase(2, 271, 320, false)]
    [TestCase(3, 321, 350, false)]
    [TestCase(4, 351, 380, false)]
    [TestCase(5, 381, 400, false)]
    [TestCase(6, 400, 400, true)]
    public async Task FreshSeasonSixDefinesIllusionTempleBracketAsync(byte level, short minimum, short maximum, bool requiresMaster)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var definitions = configuration.MiniGameDefinitions
            .Where(definition => definition.Type == MiniGameType.IllusionTemple && definition.GameLevel == level)
            .ToList();
        Assert.That(definitions, Has.Count.EqualTo(1));
        var definition = definitions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(definition.EnterDuration, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(definition.GameDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(definition.ExitDuration, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(definition.MaximumPlayerCount, Is.EqualTo(10));
            Assert.That(definition.MinimumCharacterLevel, Is.EqualTo(minimum));
            Assert.That(definition.MaximumCharacterLevel, Is.EqualTo(maximum));
            Assert.That(definition.MinimumSpecialCharacterLevel, Is.EqualTo(minimum));
            Assert.That(definition.MaximumSpecialCharacterLevel, Is.EqualTo(maximum));
            Assert.That(definition.RequiresMasterClass, Is.EqualTo(requiresMaster));
            Assert.That(definition.AllowParty, Is.False);
            Assert.That(definition.SaveRankingStatistics, Is.True);
            Assert.That(definition.MapCreationPolicy, Is.EqualTo(MiniGameMapCreationPolicy.Shared));
            Assert.That(definition.TicketItem?.Group, Is.EqualTo(13));
            Assert.That(definition.TicketItem?.Number, Is.EqualTo(51));
            Assert.That(definition.TicketItemLevel, Is.EqualTo(level));
            Assert.That(definition.Entrance?.Map?.Number, Is.EqualTo(44 + level));
            Assert.That(definition.Entrance?.X1, Is.EqualTo(98));
            Assert.That(definition.Entrance?.Y1, Is.EqualTo(128));
            Assert.That(definition.Entrance?.X2, Is.EqualTo(108));
            Assert.That(definition.Entrance?.Y2, Is.EqualTo(137));
        });
    }

    /// <summary>
    /// All six maps need the client-supported statues, team generals and relic storages.
    /// </summary>
    /// <param name="mapNumber">The map number.</param>
    [TestCase(45)]
    [TestCase(46)]
    [TestCase(47)]
    [TestCase(48)]
    [TestCase(49)]
    [TestCase(50)]
    public async Task FreshMapsUseSeasonSixNpcContractAsync(short mapNumber)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var map = configuration.Maps.Single(candidate => candidate.Number == mapNumber && candidate.Discriminator == 0);
        var eventSpawns = map.MonsterSpawns.Where(spawn => spawn.SpawnTrigger == SpawnTrigger.AutomaticDuringEvent).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(eventSpawns.Where(spawn => spawn.MonsterDefinition?.Number == 380).Sum(spawn => spawn.Quantity), Is.EqualTo(2));
            Assert.That(eventSpawns.Select(spawn => spawn.MonsterDefinition?.Number), Is.EquivalentTo(new short?[] { 380, 380, 381, 382, 383, 384 }));
            Assert.That(map.MonsterSpawns.Any(spawn => spawn.MonsterDefinition?.Number is >= 658 and <= 668), Is.False);
            Assert.That(eventSpawns.SingleOrDefault(spawn => spawn.MonsterDefinition?.Number == 381),
                Has.Property(nameof(MonsterSpawnArea.X1)).EqualTo(138).And.Property(nameof(MonsterSpawnArea.Y1)).EqualTo(44));
            Assert.That(eventSpawns.SingleOrDefault(spawn => spawn.MonsterDefinition?.Number == 383),
                Has.Property(nameof(MonsterSpawnArea.X1)).EqualTo(138).And.Property(nameof(MonsterSpawnArea.Y1)).EqualTo(58));
            Assert.That(eventSpawns.SingleOrDefault(spawn => spawn.MonsterDefinition?.Number == 384),
                Has.Property(nameof(MonsterSpawnArea.X1)).EqualTo(192).And.Property(nameof(MonsterSpawnArea.Y1)).EqualTo(113));
            Assert.That(eventSpawns.SingleOrDefault(spawn => spawn.MonsterDefinition?.Number == 382),
                Has.Property(nameof(MonsterSpawnArea.X1)).EqualTo(193).And.Property(nameof(MonsterSpawnArea.Y1)).EqualTo(126));
        });
    }

    /// <summary>
    /// The sacred relic must permit normal ground recovery without requiring an unrelated active quest.
    /// </summary>
    /// <param name="reapplyItemRules">Whether to restore the flags through the general item rules update.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task FreshSeasonSixContainsBoundedRelicAsync(bool reapplyItemRules)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var definitions = configuration.Items.Where(item => item.Group == 14 && item.Number == 64).ToList();
        Assert.That(definitions, Has.Count.EqualTo(1));
        var relic = definitions.Single();
        if (reapplyItemRules)
        {
            relic.IsTradable = relic.IsDroppable = relic.IsStorable = relic.IsSellableToNpc = relic.IsPersonalStoreSellable = relic.IsRepairable = true;
            await new AddItemRuleFlagsPlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        }

        Assert.Multiple(() =>
        {
            Assert.That(relic.IsQuestItem, Is.False, "The normal pickup action rejects quest items without an ActiveQuest.");
            Assert.That(relic.IsBoundToCharacter, Is.False, "Either team must be able to recover an ephemeral relic after death or disconnect.");
            Assert.That(relic.StorageLimitPerCharacter, Is.EqualTo(1));
            Assert.That(relic.Width, Is.EqualTo(1));
            Assert.That(relic.Height, Is.EqualTo(1));
            Assert.That(relic.DropsFromMonsters, Is.False);
            Assert.That(relic.IsTradable, Is.False);
            Assert.That(relic.IsStorable, Is.False);
            Assert.That(relic.IsSellableToNpc, Is.False);
            Assert.That(relic.IsPersonalStoreSellable, Is.False);
            Assert.That(relic.IsDroppable, Is.False);
            Assert.That(relic.IsRepairable, Is.False);
        });
    }

    /// <summary>
    /// Winning living team members receive the explicit fork defaults, without introducing a global drop.
    /// </summary>
    /// <param name="level">The event level.</param>
    /// <param name="experience">The configured experience reward.</param>
    [TestCase(1, 25000)]
    [TestCase(2, 30000)]
    [TestCase(3, 35000)]
    [TestCase(4, 35000)]
    [TestCase(5, 35000)]
    [TestCase(6, 40000)]
    public async Task WinningTeamHasConfiguredForkRewardsAsync(byte level, int experience)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var definition = configuration.MiniGameDefinitions.Single(candidate => candidate.Type == MiniGameType.IllusionTemple && candidate.GameLevel == level);
        var itemReward = definition.Rewards.Single(reward => reward.RewardType == MiniGameRewardType.Item);
        var itemGroup = itemReward.ItemReward!;
        Assert.Multiple(() =>
        {
            Assert.That(definition.Rewards, Has.Count.EqualTo(2));
            Assert.That(definition.Rewards.All(reward => reward.Rank == 1 && reward.RequiredSuccess == MiniGameSuccessFlags.Alive), Is.True);
            Assert.That(definition.Rewards.Single(reward => reward.RewardType == MiniGameRewardType.Experience).RewardAmount, Is.EqualTo(experience));
            Assert.That(itemReward.RewardAmount, Is.EqualTo(1));
            Assert.That(itemGroup.PossibleItems, Has.Count.EqualTo(1));
            Assert.That(itemGroup.PossibleItems.Single().Group, Is.EqualTo(12));
            Assert.That(itemGroup.PossibleItems.Single().Number, Is.EqualTo(15));
            Assert.That(itemGroup.ItemLevel, Is.EqualTo(0));
            Assert.That(configuration.Maps.SelectMany(map => map.DropItemGroups), Does.Not.Contain(itemGroup));
        });
    }

    /// <summary>
    /// A pre-feature Season 6 installation gets the six definitions, the relic and the original client NPCs once.
    /// </summary>
    [Test]
    public async Task ExistingSeasonSixIsUpgradedIdempotentlyAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var versions = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false))
            .Single(installed => installed.Key == typeof(SetIntroducedGameVersionsPlugInSeason6).GUID);
        Assert.That(versions.Version, Is.EqualTo(2));
        foreach (var definition in configuration.MiniGameDefinitions.Where(definition => definition.Type == MiniGameType.IllusionTemple).ToList())
        {
            configuration.MiniGameDefinitions.Remove(definition);
            foreach (var reward in definition.Rewards)
            {
                if (reward.ItemReward is { } itemGroup)
                {
                    configuration.DropItemGroups.Remove(itemGroup);
                    await context.DeleteAsync(itemGroup).ConfigureAwait(false);
                }

                await context.DeleteAsync(reward).ConfigureAwait(false);
            }

            await context.DeleteAsync(definition).ConfigureAwait(false);
        }

        var relic = configuration.Items.Single(item => item.Group == 14 && item.Number == 64);
        configuration.Items.Remove(relic);
        await context.DeleteAsync(relic).ConfigureAwait(false);
        (short Number, byte X, byte Y)[] renewalSpawns =
        [
            (658, 169, 85), (659, 136, 101), (660, 151, 119), (661, 150, 88),
            (662, 165, 102), (663, 173, 67), (664, 187, 81), (665, 187, 51),
            (666, 203, 67), (667, 133, 121), (668, 206, 48),
        ];
        var maps = configuration.Maps.Where(map => map.Number is >= 45 and <= 50 && map.Discriminator == 0).ToList();
        foreach (var map in maps)
        {
            foreach (var spawn in map.MonsterSpawns.ToList())
            {
                map.MonsterSpawns.Remove(spawn);
                await context.DeleteAsync(spawn).ConfigureAwait(false);
            }

            foreach (var old in renewalSpawns)
            {
                var spawn = context.CreateNew<MonsterSpawnArea>();
                spawn.GameMap = map;
                spawn.MonsterDefinition = configuration.Monsters.Single(monster => monster.Number == old.Number);
                spawn.X1 = spawn.X2 = old.X;
                spawn.Y1 = spawn.Y2 = old.Y;
                spawn.Quantity = 1;
                spawn.Direction = Direction.Undefined;
                spawn.SpawnTrigger = SpawnTrigger.AutomaticDuringEvent;
                map.MonsterSpawns.Add(spawn);
            }
        }

        var update = new AddIllusionTempleDataUpdatePlugIn();
        foreach (var installed in (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Where(installed => installed.Key == update.Key))
        {
            await context.DeleteAsync(installed).ConfigureAwait(false);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var definitions = configuration.MiniGameDefinitions.Where(definition => definition.Type == MiniGameType.IllusionTemple).OrderBy(definition => definition.GameLevel).ToArray();
        var itemsCount = configuration.Items.Count;
        var groupsCount = configuration.DropItemGroups.Count;
        var spawns = maps.SelectMany(map => map.MonsterSpawns).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(definitions, Has.Length.EqualTo(6));
            Assert.That(definitions.Select(definition => definition.IntroducedIn),
                Is.EqualTo(definitions.Select(definition => IntroducedGameVersions.MiniGames[(definition.Type, definition.GameLevel)])));
            Assert.That(configuration.Items.Count(item => item.Group == 14 && item.Number == 64), Is.EqualTo(1));
            Assert.That(spawns.Any(spawn => spawn.MonsterDefinition?.Number is >= 658 and <= 668), Is.False);
            Assert.That(maps.All(map => map.MonsterSpawns.Count == 6), Is.True);
            Assert.That(update.IsMandatory, Is.False);
        });

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.Items, Has.Count.EqualTo(itemsCount));
            Assert.That(configuration.DropItemGroups, Has.Count.EqualTo(groupsCount));
            Assert.That(configuration.MiniGameDefinitions.Where(definition => definition.Type == MiniGameType.IllusionTemple), Is.EquivalentTo(definitions));
            Assert.That(maps.SelectMany(map => map.MonsterSpawns), Is.EquivalentTo(spawns));
        });
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(installed => installed.Key == update.Key), Is.EqualTo(1));
    }

    /// <summary>
    /// An owner-defined event and moved NPCs must survive the content update unchanged.
    /// </summary>
    [Test]
    public async Task UpgradePreservesOwnerDefinitionsAndMovedSpawnsAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var definition = configuration.MiniGameDefinitions.Single(candidate => candidate.Type == MiniGameType.IllusionTemple && candidate.GameLevel == 1);
        definition.GameDuration = TimeSpan.FromMinutes(7);
        definition.MaximumPlayerCount = 8;
        var rewards = definition.Rewards.ToArray();
        rewards.Single(reward => reward.RewardType == MiniGameRewardType.Experience).RewardAmount = 1234;
        var map = definition.Entrance!.Map!;
        var general = map.MonsterSpawns.Single(spawn => spawn.MonsterDefinition?.Number == 381);
        general.X1 = general.X2 = 137;
        var customRenewal = context.CreateNew<MonsterSpawnArea>();
        customRenewal.GameMap = map;
        customRenewal.MonsterDefinition = configuration.Monsters.Single(monster => monster.Number == 658);
        customRenewal.X1 = customRenewal.X2 = 170;
        customRenewal.Y1 = customRenewal.Y2 = 85;
        customRenewal.Quantity = 1;
        customRenewal.Direction = Direction.Undefined;
        customRenewal.SpawnTrigger = SpawnTrigger.AutomaticDuringEvent;
        map.MonsterSpawns.Add(customRenewal);
        var customHealth = context.CreateNew<MonsterSpawnArea>();
        customHealth.GameMap = map;
        customHealth.MonsterDefinition = customRenewal.MonsterDefinition;
        customHealth.X1 = customHealth.X2 = 169;
        customHealth.Y1 = customHealth.Y2 = 85;
        customHealth.Quantity = 1;
        customHealth.Direction = Direction.Undefined;
        customHealth.SpawnTrigger = SpawnTrigger.AutomaticDuringEvent;
        customHealth.MaximumHealthOverride = 9999;
        map.MonsterSpawns.Add(customHealth);
        await context.SaveChangesAsync().ConfigureAwait(false);

        await new AddIllusionTempleDataUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(definition.GameDuration, Is.EqualTo(TimeSpan.FromMinutes(7)));
            Assert.That(definition.MaximumPlayerCount, Is.EqualTo(8));
            Assert.That(definition.Rewards, Is.EquivalentTo(rewards));
            Assert.That(rewards.Single(reward => reward.RewardType == MiniGameRewardType.Experience).RewardAmount, Is.EqualTo(1234));
            Assert.That(map.MonsterSpawns.Single(spawn => spawn.MonsterDefinition?.Number == 381), Is.SameAs(general));
            Assert.That(general.X1, Is.EqualTo(137));
            Assert.That(map.MonsterSpawns, Does.Contain(customRenewal));
            Assert.That(map.MonsterSpawns, Does.Contain(customHealth));
            Assert.That(customHealth.MaximumHealthOverride, Is.EqualTo(9999));
        });
    }
}
