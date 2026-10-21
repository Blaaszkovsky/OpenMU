// <copyright file="FereaEncounterConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Ferea;

using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Editable encounter rules. Balancing values are OpenMU defaults, not asserted retail server formulas.
/// Monster identities, positions and quantities are read from the configured spawn waves.
/// </summary>
public sealed class FereaEncounterConfiguration
{
    /// <summary>Gets or sets the west edge of the entry trigger.</summary>
    public byte EntranceX1 { get; set; } = 110;

    /// <summary>Gets or sets the east edge of the entry trigger.</summary>
    public byte EntranceX2 { get; set; } = 114;

    /// <summary>Gets or sets the south edge of the entry trigger.</summary>
    public byte EntranceY1 { get; set; } = 58;

    /// <summary>Gets or sets the north edge of the entry trigger.</summary>
    public byte EntranceY2 { get; set; } = 62;

    /// <summary>Gets or sets the arrival x-coordinate inside the arena.</summary>
    public byte ArrivalX { get; set; } = 46;

    /// <summary>Gets or sets the arrival y-coordinate inside the arena.</summary>
    public byte ArrivalY { get; set; } = 122;

    /// <summary>
    /// Gets or sets the map hosting the arena.
    /// </summary>
    public short MapNumber { get; set; } = 112;

    /// <summary>
    /// Gets or sets the west edge of the arena.
    /// </summary>
    public byte ArenaX1 { get; set; } = 20;

    /// <summary>
    /// Gets or sets the east edge of the arena.
    /// </summary>
    public byte ArenaX2 { get; set; } = 51;

    /// <summary>
    /// Gets or sets the south edge of the arena.
    /// </summary>
    public byte ArenaY1 { get; set; } = 107;

    /// <summary>
    /// Gets or sets the north edge of the arena.
    /// </summary>
    public byte ArenaY2 { get; set; } = 140;

    /// <summary>
    /// Gets or sets the walkable position outside the arena.
    /// </summary>
    public byte EjectionX { get; set; } = 54;

    /// <summary>
    /// Gets or sets the walkable position outside the arena.
    /// </summary>
    public byte EjectionY { get; set; } = 122;

    /// <summary>
    /// Gets or sets the wave containing exactly one Crystal Orb.
    /// </summary>
    public byte OrbWave { get; set; } = 20;

    /// <summary>
    /// Gets or sets the wave containing the Orb guardians.
    /// </summary>
    public byte GuardianWave { get; set; } = 21;

    /// <summary>
    /// Gets or sets the wave containing exactly one Lord.
    /// </summary>
    public byte LordWave { get; set; } = 22;

    /// <summary>
    /// Gets or sets the wave containing exactly one General.
    /// </summary>
    public byte GeneralWave { get; set; } = 23;

    /// <summary>
    /// Gets or sets the wave summoned at each health phase.
    /// </summary>
    public byte ReinforcementWave { get; set; } = 24;

    /// <summary>
    /// Gets or sets the descending one-time health gates; configurable OpenMU encounter values.
    /// </summary>
    public IList<int> PhaseHealthPercentages { get; set; } = new List<int> { 75, 50, 25 };

    /// <summary>
    /// Gets or sets the continuous time without living participants before a reset.
    /// </summary>
    public TimeSpan AbandonmentTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum battle duration.
    /// </summary>
    public TimeSpan BattleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the minimum wait after victory.
    /// </summary>
    public TimeSpan VictoryCooldownMinimum { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Gets or sets the maximum wait after victory.
    /// </summary>
    public TimeSpan VictoryCooldownMaximum { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the wait after abandonment, timeout or a failed spawn.
    /// </summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the interval of healing while the General lives.
    /// </summary>
    public TimeSpan RegenerationInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the percent of maximum health recovered per Roar victim each interval.
    /// </summary>
    public float RegenerationPercentPerPlayer { get; set; } = 0.05f;

    /// <summary>
    /// Gets or sets the radius of the phase transition which drains Mana and AG.
    /// </summary>
    public int RoarRadius { get; set; } = 8;

    /// <summary>
    /// Gets or sets the interval between area attacks.
    /// </summary>
    public TimeSpan BrandishInterval { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Gets or sets the area attack radius around the Lord.
    /// </summary>
    public int BrandishRadius { get; set; } = 3;

    /// <summary>
    /// Gets or sets the interval between mine placements.
    /// </summary>
    public TimeSpan MineInterval { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Gets or sets the warning time before mines explode.
    /// </summary>
    public TimeSpan MineDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the radius of each mine explosion.
    /// </summary>
    public int MineRadius { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum mines in one placement.
    /// </summary>
    public int MaximumMines { get; set; } = 5;

    /// <summary>
    /// Gets or sets the number of victory rolls against the Lord's configured drop groups. Zero disables item rewards.
    /// </summary>
    public int VictoryItemDropCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the independent chance, from zero to one, for each surviving participant to receive WCoinC.
    /// </summary>
    public double VictoryWCoinCChance { get; set; }

    /// <summary>Gets or sets the inclusive minimum WCoinC reward.</summary>
    public int VictoryWCoinCMinimum { get; set; }

    /// <summary>Gets or sets the inclusive maximum WCoinC reward.</summary>
    public int VictoryWCoinCMaximum { get; set; }

    /// <summary>
    /// Checks whether a point belongs to the encounter arena.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>Whether it is inside the inclusive bounds.</returns>
    public bool Contains(Point point) => point.X >= this.ArenaX1 && point.X <= this.ArenaX2
        && point.Y >= this.ArenaY1 && point.Y <= this.ArenaY2;

    /// <summary>
    /// Validates configuration before creating any encounter objects.
    /// </summary>
    public void Validate()
    {
        if (this.MapNumber < 0 || this.ArenaX1 >= this.ArenaX2 || this.ArenaY1 >= this.ArenaY2
            || this.EntranceX1 > this.EntranceX2 || this.EntranceY1 > this.EntranceY2
            || !this.Contains(new Point(this.ArrivalX, this.ArrivalY))
            || this.Contains(new Point(this.EjectionX, this.EjectionY))
            || new[] { this.OrbWave, this.GuardianWave, this.LordWave, this.GeneralWave, this.ReinforcementWave }.Distinct().Count() != 5
            || this.PhaseHealthPercentages is null || this.PhaseHealthPercentages.Any(p => p <= 0 || p >= 100)
            || !this.PhaseHealthPercentages.SequenceEqual(this.PhaseHealthPercentages.Distinct().OrderDescending())
            || this.AbandonmentTimeout <= TimeSpan.Zero || this.BattleTimeout <= TimeSpan.Zero
            || this.VictoryCooldownMinimum < TimeSpan.Zero || this.VictoryCooldownMaximum < this.VictoryCooldownMinimum
            || this.FailureCooldown < TimeSpan.Zero || this.RegenerationInterval <= TimeSpan.Zero
            || !float.IsFinite(this.RegenerationPercentPerPlayer) || this.RegenerationPercentPerPlayer < 0
            || this.RoarRadius <= 0 || this.BrandishInterval <= TimeSpan.Zero || this.BrandishRadius <= 0
            || this.MineInterval <= TimeSpan.Zero || this.MineDelay < TimeSpan.Zero
            || this.MineRadius <= 0 || this.MaximumMines is < 1 or > 5
            || this.VictoryItemDropCount is < 0 or > 100
            || !double.IsFinite(this.VictoryWCoinCChance) || this.VictoryWCoinCChance is < 0 or > 1
            || this.VictoryWCoinCMinimum < 0 || this.VictoryWCoinCMaximum < this.VictoryWCoinCMinimum)
        {
            throw new InvalidOperationException("Invalid Ferea encounter configuration.");
        }
    }
}
