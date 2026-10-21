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
    /// <summary>Verifies the guaranteed local wing consumes the sole drop before generic map rewards.</summary>
    [TestCase(0.0)]
    [TestCase(0.999999)]
    public async Task BaskiDropsExactlyOneWingWithoutGenericDropsAsync(double randomValue)
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
        var mapReward = new Mock<DropItemGroup>();
        mapReward.SetupAllProperties();
        mapReward.Object.Chance = 1;
        mapReward.Object.ItemType = SpecialItemType.Money;
        player.CurrentMap!.Definition.DropItemGroups.Add(mapReward.Object);

        var (items, money) = await generator.GenerateItemDropsAsync(baski, 100, player).ConfigureAwait(false);
        var item = items.Single();

        Assert.Multiple(() =>
        {
            Assert.That(item.Definition, Is.SameAs(configuration.Items.Single(definition => definition is { Group: 12, Number: 245 })));
            Assert.That(item.Level, Is.EqualTo(0));
            Assert.That(item.Durability, Is.GreaterThan(0));
            Assert.That(money, Is.Null);
        });
    }
}
