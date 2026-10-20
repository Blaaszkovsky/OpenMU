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
    public override string Name => "Add experimental Fearless Wings";

    /// <inheritdoc />
    public override string Description => "Adds item 12/248 for a paired MuMain client with the Fearless wing asset.";

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override IEnumerable<UpdateDependency> DependsOn => [typeof(AddMovementSpeedAttributesPlugInSeason6).GUID];

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        var wing = new Wings(context, gameConfiguration).AddExperimentalFearlessWing();
        if (wing is not null)
        {
            ItemRules.Apply([wing]);
        }

        return ValueTask.CompletedTask;
    }
}
