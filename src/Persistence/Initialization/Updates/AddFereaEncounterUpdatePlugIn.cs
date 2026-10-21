// <copyright file="AddFereaEncounterUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Ferea;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Monsters;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the optional Ferea encounter without overwriting operator settings.
/// </summary>
[PlugIn]
[Display(Name = nameof(FereaResources.UpdateName), Description = nameof(FereaResources.UpdateDescription), ResourceType = typeof(FereaResources))]
[Guid("A8C7138D-9BC1-4936-B8A0-2538A67F6915")]
public sealed class AddFereaEncounterUpdatePlugIn : UpdatePlugInBase
{
    /// <summary>The persisted default General health in encounter versions one and two.</summary>
    private const int LegacyGeneralMaximumHealth = 250000;

    /// <summary>The persisted default Lord health in encounter versions one and two.</summary>
    private const int LegacyLordMaximumHealth = 1000000;

    /// <summary>The persisted default regeneration percentage in encounter versions one and two.</summary>
    private const float LegacyRegenerationPercentPerPlayer = 0.25f;

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => FereaResources.UpdateName;

    /// <inheritdoc />
    public override string Description => FereaResources.UpdateDescription;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 3;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [new UpdateDependency(typeof(AddBaskiPrototypeUpdatePlugIn).GUID, minVersion: 4)];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration, int? installedVersion)
        => this.EnsureConfigurationAsync(context, gameConfiguration, installedVersion is null or < 3);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
        => this.EnsureConfigurationAsync(context, gameConfiguration, true);

    private static void EnsureHealth(IContext context, GameConfiguration gameConfiguration, short number, int health, int legacyHealth, bool migrateLegacyBalance)
    {
        var monster = gameConfiguration.Monsters.Single(m => m.Number == number);
        var attribute = monster.Attributes.SingleOrDefault(a => a.AttributeDefinition == Stats.MaximumHealth);
        if (attribute is null)
        {
            monster.AddAttributes(new Dictionary<AttributeDefinition, float> { { Stats.MaximumHealth, health } }, context, gameConfiguration);
        }
        else if (migrateLegacyBalance && attribute.Value == legacyHealth)
        {
            attribute.Value = health;
        }
    }

    private ValueTask EnsureConfigurationAsync(IContext context, GameConfiguration gameConfiguration, bool migrateLegacyBalance)
    {
        // Repair missing health on every run. Migrate old default values only on upgrade, so later operator edits survive.
        EnsureHealth(context, gameConfiguration, 733, FereaMonstersInitializer.GeneralMaximumHealth, LegacyGeneralMaximumHealth, migrateLegacyBalance);
        EnsureHealth(context, gameConfiguration, 734, FereaMonstersInitializer.LordMaximumHealth, LegacyLordMaximumHealth, migrateLegacyBalance);
        new FereaEncounterInitializer(context, gameConfiguration).Initialize();
        if (gameConfiguration.PlugInConfigurations.FirstOrDefault(p => p.TypeId == typeof(FereaEncounterPlugIn).GUID) is not { } configuration)
        {
            configuration = context.CreateNew<PlugInConfiguration>();
            configuration.SetGuid(typeof(FereaEncounterPlugIn).GUID);
            configuration.TypeId = typeof(FereaEncounterPlugIn).GUID;
            configuration.IsActive = true;
            configuration.SetConfiguration(new FereaEncounterConfiguration(), null);
            gameConfiguration.PlugInConfigurations.Add(configuration);
        }
        else if (string.IsNullOrWhiteSpace(configuration.CustomConfiguration))
        {
            configuration.SetConfiguration(new FereaEncounterConfiguration(), null);
        }
        else if (migrateLegacyBalance)
        {
            // Edit only the primitive setting. Full deserialization would require the original reference handler
            // for persisted collection metadata, and rewriting the whole object could lose unknown owner settings.
            var settings = JsonNode.Parse(configuration.CustomConfiguration)?.AsObject();
            var regeneration = settings?[nameof(FereaEncounterConfiguration.RegenerationPercentPerPlayer)];
            if (settings is not null && regeneration is JsonValue value && value.TryGetValue<float>(out var percent) && percent == LegacyRegenerationPercentPerPlayer)
            {
                settings[nameof(FereaEncounterConfiguration.RegenerationPercentPerPlayer)] = new FereaEncounterConfiguration().RegenerationPercentPerPlayer;
                configuration.CustomConfiguration = settings.ToJsonString();
            }
        }

        return ValueTask.CompletedTask;
    }
}
