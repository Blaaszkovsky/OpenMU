// <copyright file="IllusionTempleInitializer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Events;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.Initialization.Properties;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Maps;

/// <summary>
/// Ensures the Season 6 Illusion Temple definitions without replacing an owner's event settings.
/// </summary>
internal class IllusionTempleInitializer : InitializerBase
{
    // These are fork defaults, based on the success EXP of the closest Blood Castle bracket.
    // They are not a claim about the original server's reward table.
    private static readonly (short Minimum, short Maximum, int Experience)[] Brackets =
    [
        (220, 270, 25000),
        (271, 320, 30000),
        (321, 350, 35000),
        (351, 380, 35000),
        (381, 400, 35000),
        (400, 400, 40000),
    ];

    private static readonly (short Number, byte X, byte Y)[] LegacyRenewalSpawns =
    [
        (658, 169, 85), (659, 136, 101), (660, 151, 119), (661, 150, 88),
        (662, 165, 102), (663, 173, 67), (664, 187, 81), (665, 187, 51),
        (666, 203, 67), (667, 133, 121), (668, 206, 48),
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="IllusionTempleInitializer"/> class.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="gameConfiguration">The game configuration.</param>
    public IllusionTempleInitializer(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration)
    {
    }

    /// <inheritdoc />
    public override void Initialize()
    {
        this.EnsureNpcDefinitions();
        this.EnsureTicket();
        this.EnsureRelic();
        for (byte level = 1; level <= Brackets.Length; level++)
        {
            var map = this.GameConfiguration.Maps.FirstOrDefault(candidate => candidate.Number == 44 + level && candidate.Discriminator == 0);
            if (map is null)
            {
                // Maps deliberately removed by an owner are not recreated by a content update.
                continue;
            }

            this.EnsureNpcSpawns(map);
            if (!this.GameConfiguration.MiniGameDefinitions.Any(definition => definition.Type == MiniGameType.IllusionTemple && definition.GameLevel == level))
            {
                this.CreateDefinition(level, map);
            }
        }
    }

    /// <summary>
    /// Removes only the exact Renewal spawns previously seeded on these Season 6 maps.
    /// Owner-moved spawns, health overrides and unrelated NPCs are preserved.
    /// </summary>
    /// <returns>The asynchronous operation.</returns>
    internal async ValueTask RemoveLegacySpawnsAsync()
    {
        foreach (var map in this.GameConfiguration.Maps.Where(map => map.Number is >= 45 and <= 50 && map.Discriminator == 0))
        {
            var obsolete = map.MonsterSpawns.Where(spawn => spawn.Quantity == 1
                && spawn.Direction == Direction.Undefined
                && spawn.SpawnTrigger == SpawnTrigger.AutomaticDuringEvent
                && spawn.WaveNumber == 0
                && spawn.MaximumHealthOverride is null
                && LegacyRenewalSpawns.Any(legacy => spawn.MonsterDefinition?.Number == legacy.Number
                    && spawn.X1 == legacy.X && spawn.X2 == legacy.X && spawn.Y1 == legacy.Y && spawn.Y2 == legacy.Y)).ToList();
            foreach (var spawn in obsolete)
            {
                map.MonsterSpawns.Remove(spawn);
                await this.Context.DeleteAsync(spawn).ConfigureAwait(false);
            }
        }
    }

    private void EnsureNpcDefinitions()
    {
        (short Number, LocalizedString Name, NpcObjectKind Kind)[] defaults =
        [
            (380, LocalizedString.FromResource(() => MonsterNames.StoneStatue), NpcObjectKind.Statue),
            (381, LocalizedString.FromResource(() => MonsterNames.MUAlliesGeneral), NpcObjectKind.PassiveNpc),
            (382, LocalizedString.FromResource(() => MonsterNames.IllusionElder), NpcObjectKind.PassiveNpc),
            (383, LocalizedString.FromResource(() => MonsterNames.AllianceItemStorage), NpcObjectKind.PassiveNpc),
            (384, LocalizedString.FromResource(() => MonsterNames.IllusionItemStorage), NpcObjectKind.PassiveNpc),
        ];
        foreach (var expected in defaults)
        {
            if (this.GameConfiguration.Monsters.Any(monster => monster.Number == expected.Number))
            {
                continue;
            }

            var monster = this.Context.CreateNew<MonsterDefinition>();
            monster.SetGuid(expected.Number);
            monster.Number = expected.Number;
            monster.Designation = expected.Name;
            monster.ObjectKind = expected.Kind;
            this.GameConfiguration.Monsters.Add(monster);
        }
    }

    private void EnsureTicket()
    {
        if (this.GameConfiguration.Items.Any(item => item.Group == 13 && item.Number == 51))
        {
            return;
        }

        var ticket = this.Context.CreateNew<ItemDefinition>();
        ticket.SetGuid(13, 51);
        ticket.Group = 13;
        ticket.Number = 51;
        ticket.Name = LocalizedString.FromResource(() => ItemNames.ScrollOfBlood);
        ticket.Width = ticket.Height = 2;
        ticket.Durability = 1;
        ticket.MaximumItemLevel = 6;
        ticket.IsRepairable = false;
        this.GameConfiguration.Items.Add(ticket);
    }

    private void EnsureRelic()
    {
        if (this.GameConfiguration.Items.Any(item => item.Group == 14 && item.Number == 64))
        {
            return;
        }

        var relic = this.Context.CreateNew<ItemDefinition>();
        relic.SetGuid(14, 64);
        relic.Group = 14;
        relic.Number = 64;
        relic.Name = ItemNames.ResourceManager.GetLocalizedString("IllusionTempleRelic");
        relic.Width = 1;
        relic.Height = 1;
        relic.Durability = 1;
        relic.MaximumItemLevel = 0;

        // This belongs to the match, not an ActiveQuest. A quest flag would block normal ground recovery.
        relic.IsQuestItem = false;
        relic.StorageLimitPerCharacter = 1;
        relic.IsBoundToCharacter = false;
        relic.IsDroppable = false;
        relic.IsTradable = false;
        relic.IsStorable = false;
        relic.IsSellableToNpc = false;
        relic.IsPersonalStoreSellable = false;
        relic.IsRepairable = false;
        relic.DropsFromMonsters = false;
        this.GameConfiguration.Items.Add(relic);
    }

    private void EnsureNpcSpawns(GameMapDefinition map)
    {
        var existing = map.MonsterSpawns.Where(spawn => spawn.SpawnTrigger == SpawnTrigger.AutomaticDuringEvent && spawn.MonsterDefinition is not null)
            .GroupBy(spawn => spawn.MonsterDefinition!.Number)
            .ToDictionary(group => group.Key, group => group.Sum(spawn => Math.Max(1, (int)spawn.Quantity)));
        foreach (var expected in IllusionTempleBase.EventNpcSpawns)
        {
            if (existing.TryGetValue(expected.MonsterNumber, out var count) && count > 0)
            {
                // In particular, do not move an owner's general, storage or statue back to the defaults.
                existing[expected.MonsterNumber] = count - 1;
                continue;
            }

            var spawn = this.Context.CreateNew<MonsterSpawnArea>();
            spawn.GameMap = map;
            spawn.MonsterDefinition = this.GameConfiguration.Monsters.Single(monster => monster.Number == expected.MonsterNumber);
            spawn.X1 = spawn.X2 = expected.X;
            spawn.Y1 = spawn.Y2 = expected.Y;
            spawn.Quantity = 1;
            spawn.Direction = Direction.Undefined;
            spawn.SpawnTrigger = SpawnTrigger.AutomaticDuringEvent;
            map.MonsterSpawns.Add(spawn);
        }
    }

    private void CreateDefinition(byte level, GameMapDefinition map)
    {
        var bracket = Brackets[level - 1];
        var definition = this.Context.CreateNew<MiniGameDefinition>();
        definition.SetGuid((short)MiniGameType.IllusionTemple, level);
        definition.Name = map.Name;
        definition.Description = MiniGameDescriptions.ResourceManager.GetLocalizedString("IllusionTempleEvent");
        definition.Type = MiniGameType.IllusionTemple;
        definition.GameLevel = level;
        definition.IntroducedIn = IntroducedGameVersions.MiniGames[(MiniGameType.IllusionTemple, level)];
        definition.MinimumCharacterLevel = definition.MinimumSpecialCharacterLevel = bracket.Minimum;
        definition.MaximumCharacterLevel = definition.MaximumSpecialCharacterLevel = bracket.Maximum;
        definition.RequiresMasterClass = level == 6;
        definition.EnterDuration = TimeSpan.FromMinutes(5);
        definition.GameDuration = TimeSpan.FromMinutes(15);
        definition.ExitDuration = TimeSpan.FromMinutes(1);
        definition.MaximumPlayerCount = 10;
        definition.AllowParty = false;
        definition.SaveRankingStatistics = true;
        definition.MapCreationPolicy = MiniGameMapCreationPolicy.Shared;
        definition.TicketItem = this.GameConfiguration.Items.Single(item => item.Group == 13 && item.Number == 51);
        definition.TicketItemLevel = level;
        definition.Entrance = map.ExitGates.Single(gate => gate.IsSpawnGate && gate.X1 == 98 && gate.Y1 == 128 && gate.X2 == 108 && gate.Y2 == 137);

        var experience = this.Context.CreateNew<MiniGameReward>();
        experience.Rank = 1;
        experience.RequiredSuccess = MiniGameSuccessFlags.Alive;
        experience.RewardType = MiniGameRewardType.Experience;
        experience.RewardAmount = bracket.Experience;
        definition.Rewards.Add(experience);

        var itemGroup = this.Context.CreateNew<DropItemGroup>();
        itemGroup.Description = MiniGameDescriptions.ResourceManager.GetLocalizedString("IllusionTempleWinningTeamChaosReward");
        itemGroup.ItemLevel = 0;
        itemGroup.PossibleItems.Add(this.GameConfiguration.Items.Single(item => item.Group == 12 && item.Number == 15));
        this.GameConfiguration.DropItemGroups.Add(itemGroup);
        var itemReward = this.Context.CreateNew<MiniGameReward>();
        itemReward.Rank = 1;
        itemReward.RequiredSuccess = MiniGameSuccessFlags.Alive;
        itemReward.RewardType = MiniGameRewardType.Item;
        itemReward.RewardAmount = 1;
        itemReward.ItemReward = itemGroup;
        definition.Rewards.Add(itemReward);
        this.GameConfiguration.MiniGameDefinitions.Add(definition);
    }
}
