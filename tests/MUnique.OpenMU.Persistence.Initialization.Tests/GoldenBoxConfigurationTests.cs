// <copyright file="GoldenBoxConfigurationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.GameLogic;

/// <summary>
/// Validates the configured Golden monster to box to reward chain.
/// </summary>
[TestFixture]
public class GoldenBoxConfigurationTests
{
    /// <summary>
    /// Golden rewards refer to supported box levels, and each box generates a configured item or money reward.
    /// </summary>
    [Test]
    public async Task GoldenBoxesGenerateConfiguredRewardsAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var contextLifetime = context;
        var box = configuration.Items.Single(item => item.Group == 14 && item.Number == 11);
        var generator = new DefaultDropGenerator(configuration, Rand.GetRandomizer());
        foreach (var (monsterNumber, boxLevel) in new (short Monster, byte BoxLevel)[] { (43, 0), (78, 8), (53, 9), (79, 10), (80, 11), (82, 12) })
        {
            var monster = configuration.Monsters.Single(definition => definition.Number == monsterNumber);
            var boxDrop = monster.DropItemGroups.Single(group => group.PossibleItems.Contains(box));
            Assert.Multiple(() =>
            {
                Assert.That(monster.NumberOfMaximumItemDrops, Is.EqualTo(1));
                Assert.That(boxDrop.Chance, Is.EqualTo(1));
            });
            var generatedBox = generator.GenerateItemDrop(boxDrop);
            Assert.That(generatedBox, Is.Not.Null);
            Assert.That(generatedBox!.Definition, Is.SameAs(box));
            Assert.That(generatedBox.Level, Is.EqualTo(boxLevel));

            var groups = box.DropItems.Where(group => group.SourceItemLevel == boxLevel).ToArray();
            Assert.That(groups, Is.Not.Empty);
            for (var draw = 0; draw < 16; draw++)
            {
                var (item, money, _) = generator.GenerateItemDrop(groups);
                Assert.That(item is not null ^ money is not null, Is.True, $"Box level {boxLevel} must produce one reward.");
                if (item is not null)
                {
                    Assert.That(groups.SelectMany(group => group.PossibleItems), Does.Contain(item.Definition));
                }
                else
                {
                    Assert.That(groups.Any(group => group.MoneyAmount == money), Is.True);
                }
            }
        }
    }
}
