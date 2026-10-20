// <copyright file="AddExperimentalFearlessWingUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the experimental Fearless Wings from the locally supplied wing pack to an existing Season 6 configuration.
/// </summary>
[PlugIn]
[Guid("A0B4A89B-8772-4F35-9122-E978EAA2E8E0")]
public class AddExperimentalFearlessWingUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => "Add experimental wings";

    /// <inheritdoc />
    public override string Description => "Adds local items 12/245, 12/248 and 12/249, including third-wing options, for a paired MuMain client.";

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override DateTime UpdatedAt => new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [typeof(AddMovementSpeedAttributesPlugInSeason6).GUID];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var wings = new Wings(context, gameConfiguration);
        var wing = wings.AddExperimentalFearlessWing();
        if (wing is not null)
        {
            ItemRules.Apply([wing]);
        }

        var additionalWings = wings.AddExperimentalAdditionalWings().ToList();
        ItemRules.Apply(additionalWings);

        return ValueTask.CompletedTask;
    }
}
