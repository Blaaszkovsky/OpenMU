// <copyright file="FereaResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

using System.Globalization;
using System.Resources;

/// <summary>
/// Localized names for the encounter and its optional configuration update.
/// </summary>
public static class FereaResources
{
    private static readonly ResourceManager Resources = new(typeof(FereaResources));

    /// <summary>Gets the encounter name.</summary>
    public static string Name => Resources.GetString(nameof(Name), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the encounter description.</summary>
    public static string Description => Resources.GetString(nameof(Description), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the update name.</summary>
    public static string UpdateName => Resources.GetString(nameof(UpdateName), CultureInfo.CurrentUICulture)!;

    /// <summary>Gets the update description.</summary>
    public static string UpdateDescription => Resources.GetString(nameof(UpdateDescription), CultureInfo.CurrentUICulture)!;
}
