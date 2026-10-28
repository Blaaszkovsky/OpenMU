// <copyright file="WhiteWizardConfigurationUpdateTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Verifies fresh White Wizard settings and the version-one configuration upgrade without replacing owner settings.
/// </summary>
[TestFixture]
internal class WhiteWizardConfigurationUpdateTests
{
    /// <summary>
    /// Registers the localized-string serializer which the production host installs at startup.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        if (!JsonConverterRegistry.Converters.Any(converter => converter is LocalizedStringJsonConverter))
        {
            JsonConverterRegistry.RegisterConverter(new LocalizedStringJsonConverter());
        }
    }

    /// <summary>
    /// Fresh initialization must persist valid helper map lists without requiring an update.
    /// </summary>
    [Test]
    public async Task FreshSeasonSixPersistsValidWizardSpawnsAsync()
    {
        var (context, gameConfiguration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var _ = context;
        var configuration = gameConfiguration.PlugInConfigurations.Single(plugIn => plugIn.TypeId == typeof(WhiteWizardInvasionPlugIn).GUID);
        var settings = JsonNode.Parse(configuration.CustomConfiguration!)!;
        var mobs = GetArray(settings[nameof(PeriodicInvasionConfiguration.Mobs)]);
        Assert.That(mobs, Has.Count.EqualTo(3));
        foreach (var mob in mobs)
        {
            var spawn = new InvasionSpawnConfiguration
            {
                MonsterId = mob![nameof(InvasionSpawnConfiguration.MonsterId)]!.GetValue<ushort>(),
                Count = mob[nameof(InvasionSpawnConfiguration.Count)]!.GetValue<ushort>(),
                MapIds = GetArray(mob[nameof(InvasionSpawnConfiguration.MapIds)]).Select(map => map!.GetValue<ushort>()).ToList(),
            };
            var errors = new List<ValidationResult>();
            Assert.That(Validator.TryValidateObject(spawn, new ValidationContext(spawn), errors, true), Is.True,
                $"Monster {spawn.MonsterId}: {string.Join("; ", errors.Select(error => error.ErrorMessage))}");
        }
    }

    /// <summary>
    /// Repairs only empty helper lists, including persisted reference metadata, and preserves unrelated owner fields.
    /// </summary>
    /// <param name="preserveReferences">Whether the stored JSON contains collection reference metadata.</param>
    /// <param name="hasCustomArcherMaps">Whether one helper already has owner-defined maps.</param>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task VersionOneSettingsAreRepairedIdempotentlyAsync(bool preserveReferences, bool hasCustomArcherMaps)
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var gameConfiguration = context.CreateNew<GameConfiguration>();
        var wizard = context.CreateNew<MonsterDefinition>();
        wizard.Number = 135;
        gameConfiguration.Monsters.Add(wizard);
        var configuration = context.CreateNew<PlugInConfiguration>();
        configuration.TypeId = typeof(WhiteWizardInvasionPlugIn).GUID;
        gameConfiguration.PlugInConfigurations.Add(configuration);
        var settings = new PeriodicInvasionConfiguration
        {
            ForceSingleMap = false,
            StartMessage = "Owner message",
            TaskDuration = TimeSpan.FromMinutes(7),
            Timetable = [new TimeOnly(11, 25)],
            Mobs =
            [
                new(135, 2, [2], SpawnMapStrategy.RandomMap),
                new(136, 8, [], SpawnMapStrategy.RandomMap),
                new(137, 9, hasCustomArcherMaps ? [3] : [], SpawnMapStrategy.RandomMap),
            ],
        };
        var referenceHandler = preserveReferences ? ReferenceHandler.Preserve : null;
        configuration.SetConfiguration(settings, referenceHandler);
        var json = JsonNode.Parse(configuration.CustomConfiguration!)!;
        json["OwnerCustomSetting"] = "preserve me";
        configuration.CustomConfiguration = json.ToJsonString();
        Assert.That(configuration.GetConfiguration<PeriodicInvasionConfiguration>(referenceHandler)!.StartMessage, Is.EqualTo(settings.StartMessage),
            "The fixture must round-trip through the host's serializers before applying the update.");
        var update = new AddWhiteWizardInvasionMobsUpdatePlugIn();
        var installed = context.CreateNew<ConfigurationUpdate>();
        installed.Key = update.Key;
        installed.Version = 1;
        await context.SaveChangesAsync().ConfigureAwait(false);

        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);
        var repairedJson = configuration.CustomConfiguration;
        var repaired = configuration.GetConfiguration<PeriodicInvasionConfiguration>(referenceHandler)!;
        Assert.Multiple(() =>
        {
            Assert.That(repaired.Mobs.Single(mob => mob.MonsterId == 136).MapIds, Is.EqualTo(new ushort[] { 2 }));
            Assert.That(repaired.Mobs.Single(mob => mob.MonsterId == 137).MapIds, Is.EqualTo(new ushort[] { hasCustomArcherMaps ? (ushort)3 : (ushort)2 }));
            Assert.That(repaired.Mobs.Select(mob => mob.Count), Is.EqualTo(new ushort[] { 2, 8, 9 }));
            Assert.That(repaired.ForceSingleMap, Is.False);
            Assert.That(repaired.StartMessage, Is.EqualTo(settings.StartMessage));
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(repairedJson!)![nameof(settings.StartMessage)], json[nameof(settings.StartMessage)]), Is.True);
            Assert.That(repaired.TaskDuration, Is.EqualTo(settings.TaskDuration));
            Assert.That(repaired.Timetable, Is.EqualTo(settings.Timetable));
            Assert.That(JsonNode.Parse(repairedJson!)!["OwnerCustomSetting"]!.GetValue<string>(), Is.EqualTo("preserve me"));
            Assert.That(installed.Version, Is.EqualTo(2));
            Assert.That(update.IsMandatory, Is.False);
        });

        await update.ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);
        Assert.That(configuration.CustomConfiguration, Is.EqualTo(repairedJson));
        Assert.That((await context.GetAsync<ConfigurationUpdate>().ConfigureAwait(false)).Count(entry => entry.Key == update.Key), Is.EqualTo(1));
        Assert.That(gameConfiguration.Monsters, Has.Count.EqualTo(1), "Existing monster definitions must not be recreated.");
    }

    /// <summary>
    /// An empty wizard source must not cause arbitrary fallback maps to overwrite owner configuration.
    /// </summary>
    [Test]
    public async Task MissingSourceMapsLeaveConfigurationUnchangedAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var gameConfiguration = context.CreateNew<GameConfiguration>();
        var wizard = context.CreateNew<MonsterDefinition>();
        wizard.Number = 135;
        gameConfiguration.Monsters.Add(wizard);
        var configuration = context.CreateNew<PlugInConfiguration>();
        configuration.TypeId = typeof(WhiteWizardInvasionPlugIn).GUID;
        configuration.SetConfiguration(new PeriodicInvasionConfiguration
        {
            Mobs = [new(135, 1, [], SpawnMapStrategy.RandomMap), new(136, 15, [], SpawnMapStrategy.RandomMap)],
        }, null);
        gameConfiguration.PlugInConfigurations.Add(configuration);
        var originalJson = configuration.CustomConfiguration;

        await new AddWhiteWizardInvasionMobsUpdatePlugIn().ApplyUpdateAsync(context, gameConfiguration).ConfigureAwait(false);

        Assert.That(configuration.CustomConfiguration, Is.EqualTo(originalJson));
    }

    private static JsonArray GetArray(JsonNode? node) => node as JsonArray ?? (JsonArray)node!["$values"]!;
}
