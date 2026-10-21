// <copyright file="FereaEncounterDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
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
        AssertRewards(configuration);
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        Assert.That(feature.IsActive, Is.True);
        var dataSource = new GameConfigurationDataSource(NullLogger<GameConfigurationDataSource>.Instance, provider);
        await dataSource.GetOwnerAsync(configuration.GetId()).ConfigureAwait(false);
        var referenceHandler = new ByDataSourceReferenceHandler(dataSource);
        var settings = (FereaEncounterConfiguration)feature.GetConfiguration(typeof(FereaEncounterConfiguration), referenceHandler)!;
        Assert.DoesNotThrow(settings.Validate);
        Assert.That(settings.RegenerationPercentPerPlayer, Is.EqualTo(0.05f));
        Assert.That(settings.BattleTimeout, Is.EqualTo(TimeSpan.FromMinutes(30)));
        Assert.That(configuration.Monsters.Single(m => m.Number == 734).Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth).Value, Is.EqualTo(300000));
        Assert.That(configuration.Monsters.Single(m => m.Number == 733).Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth).Value, Is.EqualTo(100000));
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
        var lord = configuration.Monsters.Single(m => m.Number == 734);
        foreach (var reward in lord.DropItemGroups.ToArray())
        {
            lord.DropItemGroups.Remove(reward);
            configuration.DropItemGroups.Remove(reward);
            await context.DeleteAsync(reward).ConfigureAwait(false);
        }

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
        AssertRewards(configuration);
        var rewardGroup = lord.DropItemGroups.Single();
        rewardGroup.Chance = 0.25;
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
            Assert.That(lord.DropItemGroups.Single(), Is.SameAs(rewardGroup));
            Assert.That(rewardGroup.Chance, Is.EqualTo(0.25));
        });
    }

    /// <summary>Verifies the version-one encounter is upgraded without changing existing plugin settings.</summary>
    [Test]
    public async Task VersionOneEncounterReceivesRewardsWithoutChangingSettingsAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var lord = configuration.Monsters.Single(m => m.Number == 734);
        foreach (var reward in lord.DropItemGroups.ToArray())
        {
            lord.DropItemGroups.Remove(reward);
            configuration.DropItemGroups.Remove(reward);
            await context.DeleteAsync(reward).ConfigureAwait(false);
        }

        var update = new AddFereaEncounterUpdatePlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(u => u.Key == update.Key);
        installed.Version = 1;
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        var settings = feature.CustomConfiguration;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertRewards(configuration);
        Assert.That(installed.Version, Is.EqualTo(3));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(settings));
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        AssertRewards(configuration);
    }

    /// <summary>Verifies every legacy upgrade path migrates only recognized default balance values.</summary>
    /// <param name="version">The installed version, or zero when the update has no record.</param>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task LegacyDefaultsUpgradeOnceAsync(int version)
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddFereaEncounterUpdatePlugIn();
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(u => u.Key == update.Key);
        if (version == 0)
        {
            await context.DeleteAsync(installed).ConfigureAwait(false);
        }
        else
        {
            installed.Version = version;
        }

        var lordHealth = configuration.Monsters.Single(m => m.Number == 734).Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth);
        var generalHealth = configuration.Monsters.Single(m => m.Number == 733).Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth);
        lordHealth.Value = 1000000;
        generalHealth.Value = 250000;
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        feature.IsActive = false;
        feature.SetConfiguration(new FereaEncounterConfiguration
        {
            RegenerationPercentPerPlayer = 0.25f, BattleTimeout = TimeSpan.FromMinutes(40), VictoryItemDropCount = 2,
        }, ReferenceHandler.Preserve);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(lordHealth.Value, Is.EqualTo(300000));
        Assert.That(generalHealth.Value, Is.EqualTo(100000));
        var settings = feature.GetConfiguration<FereaEncounterConfiguration>(ReferenceHandler.Preserve)!;
        Assert.That(settings.RegenerationPercentPerPlayer, Is.EqualTo(0.05f));
        Assert.That(settings.BattleTimeout, Is.EqualTo(TimeSpan.FromMinutes(40)));
        Assert.That(settings.VictoryItemDropCount, Is.EqualTo(2));
        Assert.That(feature.IsActive, Is.False);
        var json = feature.CustomConfiguration;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(lordHealth.Value, Is.EqualTo(300000));
        Assert.That(generalHealth.Value, Is.EqualTo(100000));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(json));

        // Values identical to the old defaults cannot be distinguished from that profile during an opt-in upgrade.
        // After version three, choosing those values is an operator edit and must survive a repeated application.
        lordHealth.Value = 1000000;
        generalHealth.Value = 250000;
        settings.RegenerationPercentPerPlayer = 0.25f;
        feature.SetConfiguration(settings, null);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(lordHealth.Value, Is.EqualTo(1000000));
        Assert.That(generalHealth.Value, Is.EqualTo(250000));
        Assert.That(feature.GetConfiguration<FereaEncounterConfiguration>(null)!.RegenerationPercentPerPlayer, Is.EqualTo(0.25f));
    }

    /// <summary>Verifies customized balance survives upgrading, while missing health is repaired idempotently.</summary>
    [Test]
    public async Task CustomBalanceSurvivesAndMissingHealthIsRepairedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        await new VersionSeasonSix.DataInitialization(provider, new NullLoggerFactory()).CreateInitialDataAsync(1, false).ConfigureAwait(false);
        using var context = provider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        var update = new AddFereaEncounterUpdatePlugIn();
        (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(u => u.Key == update.Key).Version = 2;
        var lord = configuration.Monsters.Single(m => m.Number == 734);
        var general = configuration.Monsters.Single(m => m.Number == 733);
        var lordHealth = lord.Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth);
        var generalHealth = general.Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth);
        lordHealth.Value = 444444;
        generalHealth.Value = 222222;
        var spawn = configuration.Maps.Single(m => m.Number == 112).MonsterSpawns.Single(s => s.WaveNumber == 22);
        spawn.MaximumHealthOverride = 77777;
        var feature = configuration.PlugInConfigurations.Single(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID);
        feature.SetConfiguration(new FereaEncounterConfiguration { RegenerationPercentPerPlayer = 0.2f }, null);
        var json = feature.CustomConfiguration;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(lordHealth.Value, Is.EqualTo(444444));
        Assert.That(generalHealth.Value, Is.EqualTo(222222));
        Assert.That(spawn.MaximumHealthOverride, Is.EqualTo(77777));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(json));

        lord.Attributes.Remove(lordHealth);
        general.Attributes.Remove(generalHealth);
        await context.DeleteAsync(lordHealth).ConfigureAwait(false);
        await context.DeleteAsync(generalHealth).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(lord.Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth).Value, Is.EqualTo(300000));
        Assert.That(general.Attributes.Single(a => a.AttributeDefinition == Stats.MaximumHealth).Value, Is.EqualTo(100000));
        Assert.That(spawn.MaximumHealthOverride, Is.EqualTo(77777));
        Assert.That(feature.CustomConfiguration, Is.EqualTo(json));
    }

    private static void AssertRewards(GameConfiguration configuration)
    {
        var reward = configuration.Monsters.Single(m => m.Number == 734).DropItemGroups.Single();
        Assert.That(reward.Chance, Is.EqualTo(1));
        Assert.That(reward.PossibleItems.Select(i => (i.Group, i.Number)), Is.EquivalentTo(new[] { ((byte)14, (short)13), ((byte)14, (short)14), ((byte)12, (short)15) }));
        Assert.That(configuration.DropItemGroups.Count(g => g.GetId() == reward.GetId()), Is.EqualTo(1));
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
