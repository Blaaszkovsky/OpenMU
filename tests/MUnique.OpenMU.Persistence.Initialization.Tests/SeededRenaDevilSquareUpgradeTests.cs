// <copyright file="SeededRenaDevilSquareUpgradeTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Tests the narrowly recognized historical Rena layout observed in the restored canonical clone.
/// </summary>
[TestFixture]
internal class SeededRenaDevilSquareUpgradeTests
{
    /// <summary>
    /// Only the exact seeded BC-only layout receives the missing three DS bindings.
    /// </summary>
    /// <param name="customization">The owner change which must prevent automatic repair.</param>
    [TestCase(null)]
    [TestCase("identifier")]
    [TestCase("chance")]
    [TestCase("disabled")]
    [TestCase("minimum")]
    [TestCase("maximum")]
    [TestCase("level")]
    [TestCase("possibleItems")]
    [TestCase("definition")]
    [TestCase("extraMap")]
    [TestCase("missingMap")]
    [TestCase("partialDS")]
    [TestCase("global")]
    [TestCase("monsterFilter")]
    [TestCase("monsterBinding")]
    [TestCase("itemType")]
    [TestCase("missingDS")]
    [TestCase("randomPool")]
    [TestCase("alternatePlain")]
    public async Task RepairRequiresExactHistoricalSeededLayoutAsync(string? customization)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var group = configuration.DropItemGroups.Single(candidate => candidate.GetId() == GuidHelper.CreateGuid<DropItemGroup>(14, 21));
        foreach (var map in configuration.Maps)
        {
            map.DropItemGroups.Remove(group);
        }

        var bloodCastleMaps = configuration.Maps.Where(map => map.Discriminator == 0 && (map.Number is >= 11 and <= 17 or 52)).ToArray();
        Assert.That(bloodCastleMaps, Has.Length.EqualTo(8));
        foreach (var map in bloodCastleMaps)
        {
            map.DropItemGroups.Add(group);
        }

        var devilSquareMaps = configuration.Maps.Where(map => map.Number == 32 && map.Discriminator is >= 5 and <= 7).ToArray();
        Assert.That(devilSquareMaps, Has.Length.EqualTo(3));
        switch (customization)
        {
            case "identifier":
                group.SetGuid(Guid.NewGuid());
                break;
            case "chance":
                group.Chance = 0.02;
                break;
            case "disabled":
                group.Chance = 0;
                break;
            case "minimum":
                group.MinimumMonsterLevel = 31;
                break;
            case "maximum":
                group.MaximumMonsterLevel = 254;
                break;
            case "level":
                group.ItemLevel = 0;
                break;
            case "possibleItems":
                group.PossibleItems.Add(configuration.Items.Single(item => item.Group == 14 && item.Number == 13));
                break;
            case "definition":
                group.PossibleItems.Clear();
                group.PossibleItems.Add(configuration.Items.Single(item => item.Group == 14 && item.Number == 13));
                break;
            case "extraMap":
                configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0).DropItemGroups.Add(group);
                break;
            case "missingMap":
                bloodCastleMaps[0].DropItemGroups.Remove(group);
                break;
            case "partialDS":
                devilSquareMaps[0].DropItemGroups.Add(group);
                break;
            case "global":
                foreach (var map in configuration.Maps.Where(map => !map.DropItemGroups.Contains(group)))
                {
                    map.DropItemGroups.Add(group);
                }

                break;
            case "monsterFilter":
                group.Monster = configuration.Monsters.First();
                break;
            case "monsterBinding":
                configuration.Monsters.First().DropItemGroups.Add(group);
                break;
            case "itemType":
                group.ItemType = SpecialItemType.Jewel;
                break;
            case "missingDS":
                configuration.Maps.Remove(devilSquareMaps[0]);
                break;
            case "randomPool":
                group.PossibleItems.Single().DropsFromMonsters = true;
                break;
            case "alternatePlain":
                var alternate = context.CreateNew<DropItemGroup>();
                alternate.PossibleItems.Add(group.PossibleItems.Single());
                alternate.Chance = 0.07;
                configuration.DropItemGroups.Add(alternate);
                break;
        }

        var originalMaps = configuration.Maps.Where(map => map.DropItemGroups.Contains(group)).ToArray();
        var originalFields = (group.GetId(), group.Chance, group.MinimumMonsterLevel, group.MaximumMonsterLevel, group.ItemLevel, group.ItemType, group.Monster);
        var originalItems = group.PossibleItems.ToArray();
        var update = new AddCastleSiegeDataUpdatePlugIn();
        (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(entry => entry.Key == update.Key).Version = 2;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var expectedMaps = customization is null ? originalMaps.Concat(devilSquareMaps) : originalMaps;
        Assert.Multiple(() =>
        {
            Assert.That(configuration.Maps.Where(map => map.DropItemGroups.Contains(group)), Is.EquivalentTo(expectedMaps));
            Assert.That((group.GetId(), group.Chance, group.MinimumMonsterLevel, group.MaximumMonsterLevel, group.ItemLevel, group.ItemType, group.Monster), Is.EqualTo(originalFields));
            Assert.That(group.PossibleItems, Is.EquivalentTo(originalItems));
        });
        var allBindings = configuration.Maps.Select(map => (Map: map, Groups: map.DropItemGroups.ToArray())).ToArray();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        foreach (var binding in allBindings)
        {
            Assert.That(binding.Map.DropItemGroups, Is.EquivalentTo(binding.Groups), "Repeating the update must not duplicate bindings.");
        }
    }
}
