// <copyright file="BaskiPrototypeDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests the provisional server identity and spawn of the Baski model.
/// </summary>
[TestFixture]
internal class BaskiPrototypeDataTest
{
    private const byte MapNumber = 112;

    private const short MonsterNumber = 900;

    /// <summary>
    /// Verifies that fresh configurations contain a single Baski on Ferea.
    /// </summary>
    [Test]
    public async Task FreshConfigurationContainsBaskiAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        AssertBaski(configuration);
    }

    /// <summary>
    /// Verifies that a missing definition and spawn are added only once.
    /// </summary>
    [Test]
    public async Task UpdateAddsMissingBaskiOnceAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        var previousBaski = configuration.Monsters.Single(monster => monster.Number == MonsterNumber);
        var previousSpawn = map.MonsterSpawns.Single(area => area.MonsterDefinition?.Number == MonsterNumber);
        map.MonsterSpawns.Remove(previousSpawn);
        configuration.Monsters.Remove(previousBaski);
        await context.DeleteAsync(previousSpawn).ConfigureAwait(false);
        await context.DeleteAsync(previousBaski).ConfigureAwait(false);

        var update = new AddBaskiPrototypeUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var baski = configuration.Monsters.Single(monster => monster.Number == MonsterNumber);
        var spawn = map.MonsterSpawns.Single(area => area.MonsterDefinition?.Number == MonsterNumber);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertBaski(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.Monsters.Single(monster => monster.Number == MonsterNumber), Is.SameAs(baski));
            Assert.That(map.MonsterSpawns.Single(area => area.MonsterDefinition?.Number == MonsterNumber), Is.SameAs(spawn));
        });
    }

    private static void AssertBaski(GameConfiguration configuration)
    {
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        var baski = configuration.Monsters.Single(candidate => candidate.Number == MonsterNumber);
        var spawn = map.MonsterSpawns.Single(area => area.MonsterDefinition?.Number == MonsterNumber);
        Assert.Multiple(() =>
        {
            Assert.That(baski.Designation.ValueInNeutralLanguage, Is.EqualTo("Baski"));
            Assert.That(baski.AttackRange, Is.EqualTo(1));
            Assert.That(spawn.Quantity, Is.EqualTo(1));
            Assert.That((spawn.X1, spawn.Y1, spawn.X2, spawn.Y2), Is.EqualTo((215, 165, 215, 165)));
            Assert.That(spawn.SpawnTrigger, Is.EqualTo(SpawnTrigger.Automatic));
        });
    }
}
