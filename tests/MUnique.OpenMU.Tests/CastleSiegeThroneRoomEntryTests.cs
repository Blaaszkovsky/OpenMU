// <copyright file="CastleSiegeThroneRoomEntryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.CastleSiege.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlayerActions;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Verifies the configured Castle Siege throne-room gate requirements through the ordinary warp action.
/// </summary>
[TestFixture]
public sealed class CastleSiegeThroneRoomEntryTests
{
    /// <summary>
    /// The configured entry gate which leads through the Castle Siege throne-room entrance.
    /// </summary>
    private const short ThroneRoomEnterGateNumber = 96;

    /// <summary>
    /// Verifies an attacking player cannot enter the throne room while a Guardian Statue is alive.
    /// </summary>
    [Test]
    public async Task AttackingPlayerCannotEnterWhileAStatueIsAliveAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        fixture.AddStatue(isAlive: true);
        var originalPosition = (fixture.Player.SelectedCharacter!.PositionX, fixture.Player.SelectedCharacter.PositionY);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.ThroneRoomEnterGate).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Player.SelectedCharacter.PositionX, Is.EqualTo(originalPosition.PositionX));
            Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.EqualTo(originalPosition.PositionY));
            Assert.That(fixture.Player.CurrentMap, Is.Null);
        });
    }

    /// <summary>
    /// Verifies an unassigned player is also denied while a Guardian Statue is alive.
    /// </summary>
    [Test]
    public async Task UnassignedPlayerCannotEnterWhileAStatueIsAliveAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.None).ConfigureAwait(false);
        fixture.AddStatue(isAlive: true);
        var originalPosition = (fixture.Player.SelectedCharacter!.PositionX, fixture.Player.SelectedCharacter.PositionY);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.ThroneRoomEnterGate).ConfigureAwait(false);

        Assert.That(
            (fixture.Player.SelectedCharacter.PositionX, fixture.Player.SelectedCharacter.PositionY),
            Is.EqualTo(originalPosition));
    }

    /// <summary>
    /// Verifies defenders can use the throne-room entrance while statues are alive.
    /// </summary>
    [Test]
    public async Task DefenderCanEnterWhileAStatueIsAliveAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Defense).ConfigureAwait(false);
        fixture.AddStatue(isAlive: true);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.ThroneRoomEnterGate).ConfigureAwait(false);

        Assert.That(fixture.Player.SelectedCharacter!.PositionX, Is.InRange(fixture.ThroneRoomExitGate.X1, fixture.ThroneRoomExitGate.X2));
        Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.InRange(fixture.ThroneRoomExitGate.Y1, fixture.ThroneRoomExitGate.Y2));
    }

    /// <summary>
    /// Verifies attackers can enter after every configured Guardian Statue is destroyed.
    /// </summary>
    [Test]
    public async Task AttackingPlayerCanEnterAfterAllStatuesAreDestroyedAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        fixture.AddStatue(isAlive: false);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.ThroneRoomEnterGate).ConfigureAwait(false);

        Assert.That(fixture.Player.SelectedCharacter!.PositionX, Is.InRange(fixture.ThroneRoomExitGate.X1, fixture.ThroneRoomExitGate.X2));
        Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.InRange(fixture.ThroneRoomExitGate.Y1, fixture.ThroneRoomExitGate.Y2));
    }

    /// <summary>
    /// Verifies the statue restriction is limited to the active battle state.
    /// </summary>
    [Test]
    public async Task ThroneRoomEntryIsUnchangedOutsideBattleStateAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Attack1).ConfigureAwait(false);
        fixture.CastleSiegeContext.CurrentState = CastleSiegeState.Ready;
        fixture.AddStatue(isAlive: true);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.ThroneRoomEnterGate).ConfigureAwait(false);

        Assert.That(fixture.Player.SelectedCharacter!.PositionX, Is.InRange(fixture.ThroneRoomExitGate.X1, fixture.ThroneRoomExitGate.X2));
        Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.InRange(fixture.ThroneRoomExitGate.Y1, fixture.ThroneRoomExitGate.Y2));
    }

    /// <summary>
    /// Verifies other gates on the siege map are unaffected by the throne-room requirement.
    /// </summary>
    [Test]
    public async Task OtherSiegeMapGateIsUnchangedDuringBattleAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Attack1, OtherSiegeMapGateNumber).ConfigureAwait(false);
        fixture.AddStatue(isAlive: true);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.OtherSiegeMapEnterGate).ConfigureAwait(false);

        Assert.That(fixture.Player.SelectedCharacter!.PositionX, Is.InRange(fixture.OtherSiegeMapExitGate.X1, fixture.OtherSiegeMapExitGate.X2));
        Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.InRange(fixture.OtherSiegeMapExitGate.Y1, fixture.OtherSiegeMapExitGate.Y2));
    }

    /// <summary>
    /// Verifies another map's gate into the siege map is unaffected by the throne-room requirement.
    /// </summary>
    [Test]
    public async Task GateFromAnotherMapIsUnchangedDuringBattleAsync()
    {
        var fixture = await CreateFixtureAsync(CastleSiegeJoinSide.Attack1, OtherMapEnterGateNumber).ConfigureAwait(false);
        fixture.AddStatue(isAlive: true);

        await new WarpGateAction().EnterGateAsync(fixture.Player, fixture.OtherMapEnterGate).ConfigureAwait(false);

        Assert.That(fixture.Player.SelectedCharacter!.PositionX, Is.InRange(fixture.OtherMapExitGate.X1, fixture.OtherMapExitGate.X2));
        Assert.That(fixture.Player.SelectedCharacter.PositionY, Is.InRange(fixture.OtherMapExitGate.Y1, fixture.OtherMapExitGate.Y2));
    }

    private const short OtherSiegeMapGateNumber = 98;
    private const short OtherMapEnterGateNumber = 1;

    private static async ValueTask<TestFixture> CreateFixtureAsync(CastleSiegeJoinSide side, short enterGateNumber = ThroneRoomEnterGateNumber)
    {
        var gameContext = (GameContext)GameContextTestHelper.CreateGameContext();
        var configuration = (BasicModel.GameConfiguration)gameContext.Configuration;
        var siegeMapDefinition = new BasicModel.GameMapDefinition
        {
            Number = 30,
            TerrainData = new byte[ushort.MaxValue + 3],
        };
        configuration.Maps.Add(siegeMapDefinition);

        var throneRoomExitGate = CreateExitGate(siegeMapDefinition, 164, 198, 187, 209);
        var throneRoomEnterGate = CreateEnterGate(ThroneRoomEnterGateNumber, 93, 242, 95, 243, throneRoomExitGate);
        siegeMapDefinition.EnterGates.Add(throneRoomEnterGate);
        siegeMapDefinition.ExitGates.Add(throneRoomExitGate);

        var otherSiegeMapExitGate = CreateExitGate(siegeMapDefinition, 90, 236, 99, 239);
        var otherSiegeMapEnterGate = CreateEnterGate(OtherSiegeMapGateNumber, 160, 203, 161, 205, otherSiegeMapExitGate);
        siegeMapDefinition.EnterGates.Add(otherSiegeMapEnterGate);
        siegeMapDefinition.ExitGates.Add(otherSiegeMapExitGate);

        var otherMapDefinition = configuration.Maps.Single(map => map.Number == 0);
        var otherMapExitGate = CreateExitGate(siegeMapDefinition, 40, 40, 42, 42);
        var otherMapEnterGate = CreateEnterGate(OtherMapEnterGateNumber, 10, 10, 12, 12, otherMapExitGate);
        otherMapDefinition.EnterGates.Add(otherMapEnterGate);

        var castleSiegeConfiguration = new BasicModel.CastleSiegeConfiguration
        {
            Enabled = true,
            CastleSiegeMapDefinition = siegeMapDefinition,
        };
        AddSchedule(castleSiegeConfiguration);
        configuration.CastleSiegeConfiguration = castleSiegeConfiguration;

        var plugIn = new CastleSiegePlugIn();
        gameContext.PlugInManager.RegisterPlugInAtPlugInPoint<IWarpGateEnteringPlugIn>(plugIn);
        plugIn.ForceStart();
        await plugIn.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        var castleSiegeContext = plugIn.GetContext(gameContext)!;

        var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
        var currentMapDefinition = enterGateNumber == OtherMapEnterGateNumber ? otherMapDefinition : siegeMapDefinition;
        var currentMap = await gameContext.GetMapAsync((ushort)currentMapDefinition.Number).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The test map wasn't initialized.");
        var enterGate = enterGateNumber switch
        {
            ThroneRoomEnterGateNumber => throneRoomEnterGate,
            OtherSiegeMapGateNumber => otherSiegeMapEnterGate,
            OtherMapEnterGateNumber => otherMapEnterGate,
            _ => throw new ArgumentOutOfRangeException(nameof(enterGateNumber)),
        };

        player.SelectedCharacter!.PositionX = enterGate.X1;
        player.SelectedCharacter.PositionY = enterGate.Y1;
        player.SelectedCharacter.CurrentMap = currentMapDefinition;
        await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
        player.CurrentMap = currentMap;
        await currentMap.AddAsync(player).ConfigureAwait(false);
        castleSiegeContext.PlayerJoinSides[player.SelectedCharacter.Id] = side;

        return new TestFixture(
            player,
            castleSiegeContext,
            throneRoomExitGate,
            throneRoomEnterGate,
            otherSiegeMapExitGate,
            otherSiegeMapEnterGate,
            otherMapExitGate,
            otherMapEnterGate);
    }

    private static void AddSchedule(BasicModel.CastleSiegeConfiguration configuration)
    {
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Idle1, DayOfWeek = DayOfWeek.Sunday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.RegisterGuild, DayOfWeek = DayOfWeek.Monday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Idle2, DayOfWeek = DayOfWeek.Tuesday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.RegisterMark, DayOfWeek = DayOfWeek.Wednesday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Idle3, DayOfWeek = DayOfWeek.Thursday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Notify, DayOfWeek = DayOfWeek.Friday });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Ready, DayOfWeek = DayOfWeek.Saturday, Hour = 18 });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.Start, DayOfWeek = DayOfWeek.Saturday, Hour = 20 });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.End, DayOfWeek = DayOfWeek.Saturday, Hour = 22 });
        configuration.StateSchedule.Add(new BasicModel.CastleSiegeStateScheduleEntry { State = CastleSiegeState.EndCycle, DayOfWeek = DayOfWeek.Saturday, Hour = 22, Minute = 5 });
    }

    private static BasicModel.ExitGate CreateExitGate(GameMapDefinition map, byte x1, byte y1, byte x2, byte y2)
    {
        return new BasicModel.ExitGate
        {
            Map = map,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Direction = Direction.South,
        };
    }

    private static BasicModel.EnterGate CreateEnterGate(short number, byte x1, byte y1, byte x2, byte y2, BasicModel.ExitGate target)
    {
        return new BasicModel.EnterGate
        {
            Number = number,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            TargetGate = target,
        };
    }

    private sealed class TestFixture
    {
        public TestFixture(
            Player player,
            CastleSiegeContext castleSiegeContext,
            BasicModel.ExitGate throneRoomExitGate,
            BasicModel.EnterGate throneRoomEnterGate,
            BasicModel.ExitGate otherSiegeMapExitGate,
            BasicModel.EnterGate otherSiegeMapEnterGate,
            BasicModel.ExitGate otherMapExitGate,
            BasicModel.EnterGate otherMapEnterGate)
        {
            this.Player = player;
            this.CastleSiegeContext = castleSiegeContext;
            this.ThroneRoomExitGate = throneRoomExitGate;
            this.ThroneRoomEnterGate = throneRoomEnterGate;
            this.OtherSiegeMapExitGate = otherSiegeMapExitGate;
            this.OtherSiegeMapEnterGate = otherSiegeMapEnterGate;
            this.OtherMapExitGate = otherMapExitGate;
            this.OtherMapEnterGate = otherMapEnterGate;
        }

        public Player Player { get; }

        public CastleSiegeContext CastleSiegeContext { get; }

        public BasicModel.ExitGate ThroneRoomExitGate { get; }

        public BasicModel.EnterGate ThroneRoomEnterGate { get; }

        public BasicModel.ExitGate OtherSiegeMapExitGate { get; }

        public BasicModel.EnterGate OtherSiegeMapEnterGate { get; }

        public BasicModel.ExitGate OtherMapExitGate { get; }

        public BasicModel.EnterGate OtherMapEnterGate { get; }

        public void AddStatue(bool isAlive)
        {
            this.CastleSiegeContext.ActiveNpcs.Add(new CastleSiegeNpcRuntime
            {
                Definition = new BasicModel.CastleSiegeNpcDefinition
                {
                    MonsterDefinition = new BasicModel.MonsterDefinition { Number = CastleSiegeStatue.MonsterNumber },
                    InstanceId = 1,
                    IsPersistedToDatabase = true,
                },
                IsAlive = isAlive,
            });
        }
    }
}
