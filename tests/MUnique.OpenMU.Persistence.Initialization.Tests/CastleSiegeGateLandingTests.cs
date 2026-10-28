// <copyright file="CastleSiegeGateLandingTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies the Lorencia-to-Loren landing domain and the narrowly versioned legacy-data correction.
/// </summary>
[TestFixture]
internal class CastleSiegeGateLandingTests
{
    /// <summary>
    /// Verifies fresh initialization without executing a configuration update.
    /// </summary>
    [Test]
    public async Task FreshSeasonSixEntranceHasOnlyWalkableRandomLandingsAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var target = configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0)
            .EnterGates.Single(gate => gate.Number == 102).TargetGate!;
        Assert.That((target.X1, target.Y1, target.X2, target.Y2), Is.EqualTo(((byte)29, (byte)39, (byte)30, (byte)42)));
        AssertWalkableLandings(target);
        var installed = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false))
            .Single(update => update.Key == typeof(AddCastleSiegeDataUpdatePlugIn).GUID);
        Assert.That(installed.Version, Is.EqualTo(4));
    }

    /// <summary>
    /// Verifies that the installed version-three path changes only the legacy exit's lower Y bound.
    /// </summary>
    [Test]
    public async Task VersionThreeChangesOnlyLegacyLandingAndOwnVersionAsync()
    {
        var (provider, context, configuration, entrance, target) = CreateLegacyFixture();
        using var _ = context;
        var character = context.CreateNew<Character>();
        character.Name = "KeepMe";
        character.CurrentMap = target.Map;
        character.PositionX = 86;
        character.PositionY = 192;
        var beforeCharacter = Snapshot(character);
        var before = Snapshot(configuration);
        var other = context.CreateNew<ConfigurationUpdate>();
        other.Key = Guid.NewGuid();
        other.Version = 42;
        other.Name = "Owner update";
        var otherBefore = Snapshot(other);
        var update = new AddCastleSiegeDataUpdatePlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(target.Y1, Is.EqualTo(39));
        Assert.That(entrance.TargetGate, Is.SameAs(target));
        target.Y1 = 37;
        Assert.That(Snapshot(configuration), Is.EqualTo(before), "No other configuration field may change on the version-three path.");
        Assert.That(Snapshot(character), Is.EqualTo(beforeCharacter), "The character graph includes its referenced map; exclude only the expected gate delta.");
        target.Y1 = 39;
        var afterFirstApply = Snapshot(configuration);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(Snapshot(configuration), Is.EqualTo(afterFirstApply), "Direct reapplication must not change gate or configuration payloads.");
        Assert.That(Snapshot(other), Is.EqualTo(otherBefore));
        var own = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(row => row.Key == update.Key);
        Assert.That(own.Version, Is.EqualTo(4));
        Assert.That(update.Key, Is.EqualTo(new Guid("CD201E33-37C9-4C85-95CC-16042B28E974")));
        Assert.That(update.CreatedAt, Is.EqualTo(new DateTime(2026, 7, 28, 20, 0, 0, DateTimeKind.Utc)));
        Assert.That(update.IsMandatory, Is.True);
        GC.KeepAlive(provider);
    }

    /// <summary>
    /// Preserves customized rectangles or unrelated source/target relationships.
    /// </summary>
    /// <param name="customization">The owner-defined difference from the exact legacy gate.</param>
    [TestCase("x1")]
    [TestCase("y1")]
    [TestCase("x2")]
    [TestCase("y2")]
    [TestCase("entry")]
    [TestCase("sourceMap")]
    [TestCase("targetMap")]
    [TestCase("sourceDiscriminator")]
    [TestCase("targetDiscriminator")]
    public async Task CustomizedGateIsNotRewrittenAsync(string customization)
    {
        var (provider, context, configuration, entrance, target) = CreateLegacyFixture();
        using var _ = context;
        var source = configuration.Maps.Single(map => map.Number == 0);
        switch (customization)
        {
            case "x1": target.X1 = 28; break;
            case "y1": target.Y1 = 40; break;
            case "x2": target.X2 = 31; break;
            case "y2": target.Y2 = 43; break;
            case "entry": entrance.Number = 104; break;
            case "sourceMap": source.Number = 1; break;
            case "targetMap": target.Map!.Number = 31; break;
            case "sourceDiscriminator": source.Discriminator = 1; break;
            case "targetDiscriminator": target.Map!.Discriminator = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(customization));
        }

        var before = Snapshot(configuration);
        await new AddCastleSiegeDataUpdatePlugIn().ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(Snapshot(configuration), Is.EqualTo(before));
        GC.KeepAlive(provider);
    }

    /// <summary>
    /// The fix does not reset owner-defined direction, spawn flags or entry-level settings.
    /// </summary>
    [Test]
    public async Task OtherFieldsAndSecondServicePassArePreservedAsync()
    {
        var (provider, context, configuration, entrance, target) = CreateLegacyFixture();
        using var _ = context;
        target.Direction = Direction.East;
        target.IsSpawnGate = true;
        entrance.LevelRequirement = 123;
        var manager = new PlugInManager(null, new NullLoggerFactory(), null, null);
        manager.RegisterPlugInAtPlugInPoint<IConfigurationUpdatePlugIn>(new AddCastleSiegeDataUpdatePlugIn());
        var service = new DataUpdateService(provider, manager);
        var pending = await service.DetermineAvailableUpdatesAsync().ConfigureAwait(false);
        Assert.That(pending.Select(update => update.Key), Is.EqualTo(new[] { typeof(AddCastleSiegeDataUpdatePlugIn).GUID }));
        await service.ApplyUpdatesAsync(pending.ToList(), new Progress<(Guid CurrentUpdatingKey, bool IsCompleted)>()).ConfigureAwait(false);
        Assert.That(target.Y1, Is.EqualTo(39));
        Assert.That(target.Direction, Is.EqualTo(Direction.East));
        Assert.That(target.IsSpawnGate, Is.True);
        Assert.That(entrance.LevelRequirement, Is.EqualTo(123));
        var beforeConfiguration = Snapshot(configuration);
        var beforeVersions = Snapshot((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).ToList());
        pending = await service.DetermineAvailableUpdatesAsync().ConfigureAwait(false);
        Assert.That(pending, Is.Empty);
        await service.ApplyUpdatesAsync(pending.ToList(), new Progress<(Guid CurrentUpdatingKey, bool IsCompleted)>()).ConfigureAwait(false);
        Assert.That(Snapshot(configuration), Is.EqualTo(beforeConfiguration));
        Assert.That(Snapshot((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).ToList()), Is.EqualTo(beforeVersions));
    }

    /// <summary>
    /// Previous supported versions retain their existing cumulative work and also receive the gate correction.
    /// </summary>
    /// <param name="version">The installed version, or zero for no installed update.</param>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task OlderVersionsAlsoReceiveGateCorrectionAsync(int version)
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var update = new AddCastleSiegeDataUpdatePlugIn();
        var own = (await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Single(row => row.Key == update.Key);
        if (version == 0)
        {
            await context.DeleteAsync(own).ConfigureAwait(false);
        }
        else
        {
            own.Version = version;
        }

        var target = configuration.Maps.Single(map => map.Number == 0 && map.Discriminator == 0)
            .EnterGates.Single(gate => gate.Number == 102).TargetGate!;
        target.Y1 = 37;
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        Assert.That(target.Y1, Is.EqualTo(39));
        AssertWalkableLandings(target);
    }

    private static void AssertWalkableLandings(ExitGate target)
    {
        var method = typeof(ExitGateExtensions).GetMethod("GetPossibleLandingPoints", BindingFlags.Static | BindingFlags.NonPublic)!;
        var points = ((IEnumerable<Point>)method.Invoke(null, new object[] { target })!).ToList();
        Assert.That(points.Select(point => (point.X, point.Y)), Is.EqualTo(new[] { ((byte)29, (byte)39), ((byte)29, (byte)40), ((byte)29, (byte)41) }));
        var terrain = new GameMapTerrain(target.Map ?? throw new InvalidOperationException("Landing gate has no target map."));
        Assert.That(points.All(point => terrain.WalkMap[point.X, point.Y]), Is.True);
    }

    private static (InMemoryPersistenceContextProvider Provider, IContext Context, GameConfiguration Configuration, EnterGate Entrance, ExitGate Target) CreateLegacyFixture()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        var source = context.CreateNew<GameMapDefinition>();
        source.Number = 0;
        var targetMap = context.CreateNew<GameMapDefinition>();
        targetMap.Number = 30;
        configuration.Maps.Add(source);
        configuration.Maps.Add(targetMap);
        var target = context.CreateNew<ExitGate>();
        target.Map = targetMap;
        target.X1 = 29;
        target.Y1 = 37;
        target.X2 = 30;
        target.Y2 = 42;
        targetMap.ExitGates.Add(target);
        var entrance = context.CreateNew<EnterGate>();
        entrance.Number = 102;
        entrance.TargetGate = target;
        source.EnterGates.Add(entrance);
        foreach (var rectangle in new[] { (90, 236, 99, 239), (84, 180, 100, 222), (155, 37, 158, 43) })
        {
            var unrelated = context.CreateNew<ExitGate>();
            unrelated.Map = targetMap;
            unrelated.X1 = (byte)rectangle.Item1;
            unrelated.Y1 = (byte)rectangle.Item2;
            unrelated.X2 = (byte)rectangle.Item3;
            unrelated.Y2 = (byte)rectangle.Item4;
            targetMap.ExitGates.Add(unrelated);
        }

        var installed = context.CreateNew<ConfigurationUpdate>();
        installed.Key = typeof(AddCastleSiegeDataUpdatePlugIn).GUID;
        installed.Version = 3;
        installed.CreatedAt = new DateTime(2026, 7, 28, 20, 0, 0, DateTimeKind.Utc);
        installed.InstalledAt = DateTime.UtcNow;
        var state = context.CreateNew<ConfigurationUpdateState>();
        state.InitializationKey = VersionSeasonSix.DataInitialization.Id;
        return (provider, context, configuration, entrance, target);
    }

    private static string Snapshot<T>(T value)
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve, MaxDepth = 256 };
        options.Converters.Add(new LocalizedStringJsonConverter());
        return JsonSerializer.Serialize(value, options);
    }
}
