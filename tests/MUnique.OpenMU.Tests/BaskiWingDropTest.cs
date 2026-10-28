// <copyright file="BaskiWingDropTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Exercises the configured Baski reward through the production drop generator.
/// </summary>
[TestFixture]
internal class BaskiWingDropTest
{
    /// <summary>Verifies the configured five percent reward and the shared one-drop cap.</summary>
    [TestCase(0.0, false, true)]
    [TestCase(0.049999, false, true)]
    [TestCase(0.05, false, false)]
    [TestCase(0.999999, false, false)]
    [TestCase(0.0, true, false)]
    [TestCase(0.999999, true, false)]
    public async Task BaskiRewardRespectsChanceAndDropCapAsync(double randomValue, bool guaranteedMapReward, bool dropsWing)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, true).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var baski = configuration.Monsters.Single(monster => monster.Number == 900);
        var randomizer = new Mock<IRandomizer>();
        randomizer.Setup(random => random.NextDouble()).Returns(randomValue);
        randomizer.Setup(random => random.NextInt(It.IsAny<int>(), It.IsAny<int>())).Returns((int minimum, int maximum) => minimum);
        var generator = new DefaultDropGenerator(configuration, randomizer.Object);
        await using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        if (guaranteedMapReward)
        {
            var mapReward = new Mock<DropItemGroup>();
            mapReward.SetupAllProperties();
            mapReward.Object.Chance = 1;
            mapReward.Object.ItemType = SpecialItemType.Money;
            player.CurrentMap!.Definition.DropItemGroups.Add(mapReward.Object);
        }

        var (items, money) = await generator.GenerateItemDropsAsync(baski, 100, player).ConfigureAwait(false);
        var droppedItems = items.ToList();
        Assert.Multiple(() =>
        {
            Assert.That(baski.DropItemGroups.Single().Chance, Is.EqualTo(0.05));
            Assert.That(baski.NumberOfMaximumItemDrops, Is.EqualTo(1));
            Assert.That(droppedItems, Has.Count.EqualTo(dropsWing ? 1 : 0));
            Assert.That(money.HasValue, Is.EqualTo(guaranteedMapReward));
        });
        if (!dropsWing)
        {
            return;
        }

        var item = droppedItems.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Definition, Is.SameAs(configuration.Items.Single(definition => definition is { Group: 12, Number: 245 })));
            Assert.That(item.Level, Is.EqualTo(0));
            Assert.That(item.Durability, Is.GreaterThan(0));
            Assert.That(money, Is.Null);
        });
    }
}
