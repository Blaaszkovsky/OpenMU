// <copyright file="FereaMonstersDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies Ferea monster identities and hunting spawns.
/// </summary>
[TestFixture]
internal class FereaMonstersDataTest
{
    private const byte MapNumber = 112;

    private static readonly short[] MonsterNumbers = [730, 731, 732, 733, 734, 735];

    /// <summary>
    /// Verifies that fresh configurations include all identities but only ordinary hunting spawns.
    /// </summary>
    [Test]
    public async Task FreshConfigurationContainsFereaMonstersAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        AssertMonsters(configuration);
    }

    /// <summary>
    /// Verifies that an existing configuration is repaired without duplicate spawns.
    /// </summary>
    [Test]
    public async Task UpdateRepairsMissingFereaMonstersOnceAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        var archer = configuration.Monsters.Single(monster => monster.Number == 731);
        foreach (var spawn in map.MonsterSpawns.Where(area => area.MonsterDefinition?.Number == 731).ToArray())
        {
            map.MonsterSpawns.Remove(spawn);
            await context.DeleteAsync(spawn).ConfigureAwait(false);
        }

        configuration.Monsters.Remove(archer);
        await context.DeleteAsync(archer).ConfigureAwait(false);

        var update = new AddBaskiPrototypeUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertMonsters(configuration);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertMonsters(configuration);
    }

    private static void AssertMonsters(GameConfiguration configuration)
    {
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        Assert.Multiple(() =>
        {
            foreach (var number in MonsterNumbers)
            {
                Assert.That(configuration.Monsters.Count(monster => monster.Number == number), Is.EqualTo(1));
                var expectedSpawns = number <= 732 ? 6 : 0;
                Assert.That(map.MonsterSpawns.Count(area => area.MonsterDefinition?.Number == number && area.SpawnTrigger == SpawnTrigger.Automatic), Is.EqualTo(expectedSpawns));
            }

            var huntingSpawns = map.MonsterSpawns.Where(area => area.MonsterDefinition?.Number is >= 730 and <= 732 && area.SpawnTrigger == SpawnTrigger.Automatic).ToArray();
            Assert.That(huntingSpawns.Select(area => (area.X1, area.Y1)).Distinct().Count(), Is.EqualTo(huntingSpawns.Length));
        });
    }
}
