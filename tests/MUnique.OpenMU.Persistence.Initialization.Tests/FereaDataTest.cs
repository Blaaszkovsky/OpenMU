// <copyright file="FereaDataTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Verifies Ferea collision data and migration of existing configurations.
/// </summary>
[TestFixture]
internal class FereaDataTest
{
    private const byte MapNumber = 112;

    /// <summary>
    /// Verifies that a fresh Season 6 configuration contains Ferea with a passable entrance.
    /// </summary>
    [Test]
    public async Task FreshConfigurationContainsFereaAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(contextProvider, new NullLoggerFactory());
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);

        using var context = contextProvider.CreateNewContext();
        var configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        AssertFerea(configuration);
        var servers = await context.GetAsync<GameServerConfiguration>().ConfigureAwait(false);
        Assert.That(servers.Any(server => server.Maps.Any(map => map.Number == MapNumber)), Is.True);
    }

    /// <summary>
    /// Verifies that the update adds missing Ferea data and is idempotent.
    /// </summary>
    [Test]
    public async Task UpdateRepairsMissingFereaOnceAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        using var context = contextProvider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var server = context.CreateNew<GameServerConfiguration>();
        var serverDefinition = context.CreateNew<GameServerDefinition>();
        serverDefinition.GameConfiguration = configuration;
        serverDefinition.ServerConfiguration = server;
        var update = new AddFereaDataUpdatePlugIn();

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        var gate = map.ExitGates.Single(candidate => candidate.IsSpawnGate);
        var warp = configuration.WarpList.Single(candidate => candidate.Index == 50);

        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertFerea(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(configuration.Maps.Count(candidate => candidate.Number == MapNumber), Is.EqualTo(1));
            Assert.That(map.ExitGates.Single(candidate => candidate.IsSpawnGate), Is.SameAs(gate));
            Assert.That(configuration.WarpList.Single(candidate => candidate.Index == 50), Is.SameAs(warp));
            Assert.That(server.Maps.Count(candidate => candidate.Number == MapNumber), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Verifies that a partially configured map is repaired on the intended server only.
    /// </summary>
    [Test]
    public async Task UpdateRepairsTerrainAndUsesOnlyTheOwningServerAsync()
    {
        var contextProvider = new InMemoryPersistenceContextProvider();
        using var context = contextProvider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var map = context.CreateNew<GameMapDefinition>();
        map.Number = MapNumber;
        configuration.Maps.Add(map);

        var orphan = context.CreateNew<GameServerConfiguration>();
        var unrelatedLorencia = context.CreateNew<GameMapDefinition>();
        unrelatedLorencia.Number = 0;
        orphan.Maps.Add(unrelatedLorencia);
        var unrelated = context.CreateNew<GameServerConfiguration>();
        var unrelatedConfiguration = context.CreateNew<GameConfiguration>();
        var unrelatedDefinition = context.CreateNew<GameServerDefinition>();
        unrelatedDefinition.GameConfiguration = unrelatedConfiguration;
        unrelatedDefinition.ServerConfiguration = unrelated;

        var host = context.CreateNew<GameServerConfiguration>();
        var ownDefinition = context.CreateNew<GameServerDefinition>();
        ownDefinition.GameConfiguration = configuration;
        ownDefinition.ServerConfiguration = host;

        var update = new AddFereaDataUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        var terrain = map.TerrainData;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);

        AssertFerea(configuration);
        Assert.Multiple(() =>
        {
            Assert.That(map.TerrainData, Is.SameAs(terrain));
            Assert.That(host.Maps.Count(candidate => candidate.Number == MapNumber), Is.EqualTo(1));
            Assert.That(orphan.Maps.Any(candidate => candidate.Number == MapNumber), Is.False);
            Assert.That(unrelated.Maps.Any(candidate => candidate.Number == MapNumber), Is.False);
        });
    }

    private static void AssertFerea(GameConfiguration configuration)
    {
        var map = configuration.Maps.Single(candidate => candidate.Number == MapNumber);
        var gate = map.ExitGates.Single(candidate => candidate.IsSpawnGate);
        var warp = configuration.WarpList.Single(candidate => candidate.Index == 50);
        Assert.Multiple(() =>
        {
            Assert.That(map.TerrainData, Has.Length.EqualTo(65539));
            Assert.That(map.TerrainData![3 + (152 * 256) + 236], Is.EqualTo(1));
            Assert.That(map.TerrainData[3 + (153 * 256) + 237], Is.EqualTo(1));
            Assert.That(map.TerrainData[3 + (165 * 256) + 215], Is.EqualTo(0));
            Assert.That(map.SafezoneMap, Is.SameAs(map));
            Assert.That((gate.X1, gate.Y1, gate.X2, gate.Y2), Is.EqualTo((236, 152, 237, 153)));
            Assert.That(warp.Gate, Is.SameAs(gate));
        });
    }
}
