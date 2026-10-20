// <copyright file="Ferea.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;

/// <summary>
/// Initializes the Ferea map for the extended client.
/// </summary>
internal class Ferea : BaseMapInitializer
{
    /// <summary>
    /// The map number sent to the client. Its terrain files use the World113 suffix.
    /// </summary>
    internal const byte Number = 112;

    /// <summary>
    /// Initializes a new instance of the <see cref="Ferea"/> class.
    /// </summary>
    /// <param name="context">The persistence context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public Ferea(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc/>
    protected override byte MapNumber => Number;

    /// <inheritdoc/>
    protected override LocalizedString MapName => MapNames.ResourceManager.GetLocalizedString(nameof(Ferea));
}
