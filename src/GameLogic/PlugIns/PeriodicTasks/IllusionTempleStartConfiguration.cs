// <copyright file="IllusionTempleStartConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using MUnique.OpenMU.Interfaces;

/// <summary>Schedule and match rules for the Season Six Illusion Temple.</summary>
public class IllusionTempleStartConfiguration : MiniGameStartConfiguration
{
    /// <summary>Gets the default rules and six daily registration windows of this fork.</summary>
    public static IllusionTempleStartConfiguration Default => new()
    {
        PreStartMessageDelay = TimeSpan.Zero,
        TaskDuration = TimeSpan.FromMinutes(21),
        Timetable = GenerateTimeSequence(TimeSpan.FromHours(4), new TimeOnly(2, 0)).ToList(),
        EntranceOpenedMessage = LocalizedString.FromResource(() => IllusionTempleResources.EntranceOpened),
        EntranceClosedMessage = LocalizedString.FromResource(() => IllusionTempleResources.EntranceClosed),
    };

    /// <summary>Gets or sets the minimum number of participants.</summary>
    [Range(2, 10)]
    public int MinimumPlayers { get; set; } = 4;

    /// <summary>Gets or sets the team score which ends the match.</summary>
    [Range(2, 255)]
    public byte WinningScore { get; set; } = 7;

    /// <summary>Gets or sets the points earned for killing an opponent.</summary>
    [Range(0, 255)]
    public byte SkillPointsPerKill { get; set; } = 5;

    /// <summary>Gets or sets the stationary capture and deposit time.</summary>
    public TimeSpan CaptureDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the protection and restraint duration.</summary>
    public TimeSpan SkillDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the carrier's movement speed factor.</summary>
    [Range(0.05, 1)]
    public float CarrierMovementFactor { get; set; } = 0.5f;

    /// <summary>Gets or sets the protection skill cost.</summary>
    public byte ProtectionCost { get; set; } = 5;

    /// <summary>Gets or sets the restraint skill cost.</summary>
    public byte RestraintCost { get; set; } = 5;

    /// <summary>Gets or sets the pursuit skill cost.</summary>
    public byte PursuitCost { get; set; } = 10;

    /// <summary>Gets or sets the shield burn skill cost.</summary>
    public byte ShieldBurnCost { get; set; } = 5;

    /// <summary>Gets or sets the shield fraction removed by shield burn.</summary>
    [Range(0, 1)]
    public float ShieldBurnFactor { get; set; } = 0.5f;
}
