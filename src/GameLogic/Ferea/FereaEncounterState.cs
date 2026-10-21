// <copyright file="FereaEncounterState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

/// <summary>
/// The server-side stages of a Ferea encounter.
/// </summary>
public enum FereaEncounterState
{
    /// <summary>The encounter is waiting for initialization.</summary>
    Waiting,

    /// <summary>The Orb and its guardians can be challenged.</summary>
    Orb,

    /// <summary>The Lord is fighting.</summary>
    Battle,

    /// <summary>The Lord is protected by a General.</summary>
    General,

    /// <summary>The arena is empty until its next opening.</summary>
    Cooldown,

    /// <summary>The Orb has fallen and entry is closed while the Lord is being spawned.</summary>
    Summoning,
}
