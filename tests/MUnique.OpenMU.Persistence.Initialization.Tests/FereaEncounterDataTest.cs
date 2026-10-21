// <copyright file="FereaEncounterDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Ferea;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies fresh initialization and idempotent repairs of an existing installation.
/// </summary>
[TestFixture]
internal class FereaEncounterDataTest
{
    /// <summary>Verifies fresh databases already have usable encounter waves and feature configuration.</summary>
    [Test]
    public async Task FreshDatabaseContainsPlayableEncounterAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        AssertEncounter(configuration);
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        Assert.That(feature.IsActive, Is.True);
        var dataSource = new GameConfigurationDataSource(NullLogger<GameConfigurationDataSource>.Instance, provider);
        await dataSource.GetOwnerAsync(configuration.GetId()).ConfigureAwait(false);
        var referenceHandler = new ByDataSourceReferenceHandler(dataSource);
        var settings = (FereaEncounterConfiguration)feature.GetConfiguration(typeof(FereaEncounterConfiguration), referenceHandler)!;
        Assert.DoesNotThrow(settings.Validate);
    }

    /// <summary>Verifies a pre-encounter database is repaired once, while owner settings and hunting spawns survive.</summary>
    [Test]
    public async Task UpdateRepairsMissingDataAndPreservesCustomizationAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var map = configuration.Maps.Single(m => m.Number == 112);
        var hunting = map.MonsterSpawns.Where(s => s.SpawnTrigger == SpawnTrigger.Automatic).ToArray();
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        configuration.PlugInConfigurations.Remove(feature);
        await context.DeleteAsync(feature).ConfigureAwait(false);
        foreach (var spawn in map.MonsterSpawns.Where(s => s.SpawnTrigger == SpawnTrigger.OnceAtWaveStart).ToArray())
        {
            map.MonsterSpawns.Remove(spawn);
            await context.DeleteAsync(spawn).ConfigureAwait(false);
        }

        var update = new AddFereaEncounterUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertEncounter(configuration);
        feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        feature.IsActive = false;
        feature.SetConfiguration(new FereaEncounterConfiguration { RegenerationPercentPerPlayer = 0.5f }, null);
        var customJson = feature.CustomConfiguration;
        var firstSpawn = map.MonsterSpawns.Single(s => s.WaveNumber == 21 && s.MonsterDefinition?.Number == 730);
        firstSpawn.Quantity = 2;
        var identities = map.MonsterSpawns.Select(s => s.GetId()).ToArray();

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(map.MonsterSpawns.Select(s => s.GetId()), Is.EquivalentTo(identities));
            Assert.That(map.MonsterSpawns.Where(s => s.SpawnTrigger == SpawnTrigger.Automatic), Is.EquivalentTo(hunting));
            Assert.That(configuration.PlugInConfigurations.Count(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID), Is.EqualTo(1));
            Assert.That(feature.IsActive, Is.False);
            Assert.That(feature.CustomConfiguration, Is.EqualTo(customJson));
            Assert.That(firstSpawn.Quantity, Is.EqualTo(2));
        });
    }

    private static void AssertEncounter(GameConfiguration configuration)
    {
        var settings = new FereaEncounterConfiguration();
        var map = configuration.Maps.Single(m => m.Number == settings.MapNumber);
        var terrain = new GameMapTerrain(map);
        var eventSpawns = map.MonsterSpawns.Where(s => s.SpawnTrigger == SpawnTrigger.OnceAtWaveStart).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(eventSpawns.Length, Is.EqualTo(9));
            Assert.That(eventSpawns.Where(s => s.WaveNumber == settings.OrbWave).Single().MonsterDefinition?.Number, Is.EqualTo(735));
            Assert.That(eventSpawns.Where(s => s.WaveNumber == settings.LordWave).Single().MonsterDefinition?.Number, Is.EqualTo(734));
            Assert.That(eventSpawns.Where(s => s.WaveNumber == settings.GeneralWave).Single().MonsterDefinition?.Number, Is.EqualTo(733));
            Assert.That(eventSpawns.Count(s => s.WaveNumber == settings.GuardianWave), Is.EqualTo(3));
            foreach (var spawn in eventSpawns)
            {
                Assert.That(terrain.AIgrid[spawn.X1, spawn.Y1], Is.EqualTo(1), $"Spawn {spawn.MonsterDefinition?.Number} is blocked.");
                Assert.That(settings.Contains(new MUnique.OpenMU.Pathfinding.Point(spawn.X1, spawn.Y1)), Is.True);
                Assert.That(spawn.MonsterDefinition?.NumberOfMaximumItemDrops, Is.Zero);
            }

            Assert.That(terrain.WalkMap[settings.ArrivalX, settings.ArrivalY], Is.True);
            Assert.That(terrain.WalkMap[settings.EjectionX, settings.EjectionY], Is.True);
        });
    }
}
