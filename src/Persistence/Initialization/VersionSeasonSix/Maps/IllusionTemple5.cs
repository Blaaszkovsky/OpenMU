// <copyright file="IllusionTemple5.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;

/// <summary>
/// Initialization for the Illusion Temple 5.
/// </summary>
internal class IllusionTemple5 : IllusionTempleBase
{
    /// <summary>
    /// The Number of the Map.
    /// </summary>
    internal const byte Number = 49;

    /// <summary>
    /// Initializes a new instance of the <see cref="IllusionTemple5"/> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public IllusionTemple5(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <summary>
    /// Gets the name of the map.
    /// </summary>
    internal static LocalizedString Name => LocalizedString.FromResource(() => MapNames.IllusionTemple5);

    /// <inheritdoc/>
    protected override byte MapNumber => Number;

    /// <inheritdoc/>
    protected override LocalizedString MapName => Name;
}
