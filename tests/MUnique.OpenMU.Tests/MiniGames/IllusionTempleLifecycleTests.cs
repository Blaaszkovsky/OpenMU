// <copyright file="IllusionTempleLifecycleTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests.MiniGames;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.MiniGames.IllusionTemple;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using Moq;
using ExitGate = MUnique.OpenMU.Persistence.BasicModel.ExitGate;
using MiniGameDefinition = MUnique.OpenMU.Persistence.BasicModel.MiniGameDefinition;

/// <summary>Exercises the configured Illusion Temple match through its real manager, map, and player actions.</summary>
[TestFixture]
public class IllusionTempleLifecycleTests
{
    /// <summary>A detached NPC channel must reacquire the player's persistence lock before changing its inventory.</summary>
    /// <param name="deposit">Whether to deposit an already captured relic instead of capturing a new one.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Timeout(90_000)]
    public async Task NpcChannelWaitsForIndependentPersistenceOperationAsync(bool deposit)
    {
        await using var world = await TestWorld.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var player = world.Players[0];
        var npc = GetNpcs(world.Game).First(candidate => candidate.Definition.Number == 380);
        player.Position = npc.Position;
        if (deposit)
        {
            await InteractUntilAsync(world.Game, player, npc, () => HasRelic(player), "The fixture must capture a relic before testing a deposit.").ConfigureAwait(false);
            npc = GetNpcs(world.Game).Single(candidate => candidate.Definition.Number == 383 + world.Game.GetTeam(player));
            player.Position = npc.Position;
        }

        var appear = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!);
        var removed = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemRemovedPlugIn>()!);
        appear.Invocations.Clear();
        removed.Invocations.Clear();

        // The packet funnel holds this lock when it starts the detached channel.
        await player.RunPersistenceExclusiveAsync(() => world.Game.TalkToEventNpcAsync(player, npc)).ConfigureAwait(false);
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = Task.Run(() => player.RunPersistenceExclusiveAsync(async () =>
        {
            lockHeld.SetResult();
            await release.Task.ConfigureAwait(false);
        }).AsTask());

        try
        {
            await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(HasRelic(player), Is.EqualTo(deposit), "The channel must not change inventory while an independent persistence operation holds the lock.");
                appear.Verify(view => view.ItemAppearAsync(It.IsAny<Item>()), Times.Never);
                removed.Verify(view => view.RemoveItemAsync(It.IsAny<byte>()), Times.Never);
            });
        }
        finally
        {
            release.TrySetResult();
            await saving.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        await WaitUntilAsync(() => HasRelic(player) != deposit, TimeSpan.FromSeconds(5), "The valid channel must finish after the persistence lock is released.").ConfigureAwait(false);
    }

    /// <summary>Event completion and disposal must serialize relic deletion and rewards against player saves.</summary>
    /// <param name="dispose">Whether to dispose the running match instead of ending it normally.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Timeout(90_000)]
    public async Task MatchCleanupWaitsForIndependentPersistenceOperationAsync(bool dispose)
    {
        await using var world = await TestWorld.CreateAsync(2, winningScore: 3).ConfigureAwait(false);
        await world.StartAsync(fromPlayerPacket: true).ConfigureAwait(false);
        var player = world.Players[0];
        if (!dispose)
        {
            // Only rank one earns the configured reward; two deposits establish a winner before clock expiry.
            for (var score = 0; score < 2; score++)
            {
                var scoringStatue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
                player.Position = scoringStatue.Position;
                await InteractUntilAsync(world.Game, player, scoringStatue, () => HasRelic(player), "The winner must capture each scoring relic.").ConfigureAwait(false);
                var pedestal = GetNpcs(world.Game).Single(npc => npc.Definition.Number == 383 + world.Game.GetTeam(player));
                player.Position = pedestal.Position;
                await InteractUntilAsync(world.Game, player, pedestal, () => !HasRelic(player), "The winner must complete each normal deposit.").ConfigureAwait(false);
                await WaitUntilAsync(() => GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380) == 2, TimeSpan.FromSeconds(5), "A completed score must restore both statues.").ConfigureAwait(false);
            }
        }

        var statue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        player.Position = statue.Position;
        await InteractUntilAsync(world.Game, player, statue, () => HasRelic(player), "The fixture must capture a relic before match cleanup.").ConfigureAwait(false);
        var experienceBefore = player.SelectedCharacter!.Experience;
        var removed = Mock.Get(player.ViewPlugIns.GetPlugIn<IItemRemovedPlugIn>()!);
        removed.Invocations.Clear();
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = Task.Run(() => player.RunPersistenceExclusiveAsync(async () =>
        {
            lockHeld.SetResult();
            await release.Task.ConfigureAwait(false);
        }).AsTask());
        Task? disposing = null;
        try
        {
            await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (dispose)
            {
                disposing = Task.Run(() => world.Game.DisposeAsync().AsTask());
            }
            else
            {
                typeof(MiniGameContext).GetMethod("FinishEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(world.Game, null);
            }

            await WaitUntilAsync(() => dispose ? world.Game.IsDisposing : world.Game.State == MiniGameState.Ended, TimeSpan.FromSeconds(5), "The match must enter its cleanup path.").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(HasRelic(player), Is.True, "Cleanup must not delete a relic during an independent player save.");
                Assert.That(player.SelectedCharacter.Experience, Is.EqualTo(experienceBefore), "Finishing rewards must wait for the persistence lock.");
                removed.Verify(view => view.RemoveItemAsync(It.IsAny<byte>()), Times.Never);
            });
        }
        finally
        {
            release.TrySetResult();
            await saving.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (disposing is not null)
            {
                await disposing.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }

        await WaitUntilAsync(() => !HasRelic(player), TimeSpan.FromSeconds(5), "Cleanup must delete the relic after the persistence lock is released.").ConfigureAwait(false);
        if (!dispose)
        {
            await WaitUntilAsync(() => player.SelectedCharacter.Experience > experienceBefore, TimeSpan.FromSeconds(5), "The configured finishing reward must follow serialized cleanup.").ConfigureAwait(false);
        }
    }

    /// <summary>An attack may not wait for a victim's persistence lock or delete its relic while that lock is held.</summary>
    [Test]
    [Timeout(90_000)]
    public async Task CarrierDeathDoesNotNestAttackerAndVictimPersistenceLocksAsync()
    {
        await using var world = await TestWorld.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);
        var carrier = world.Players[0];
        var attacker = world.Players[1];
        var statue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        carrier.Position = statue.Position;
        await InteractUntilAsync(world.Game, carrier, statue, () => HasRelic(carrier), "The fixture must capture a relic before carrier death.").ConfigureAwait(false);
        carrier.Attributes![Stats.CurrentShield] = 0;
        carrier.Attributes[Stats.CurrentHealth] = 1;
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = Task.Run(() => carrier.RunPersistenceExclusiveAsync(async () =>
        {
            lockHeld.SetResult();
            await release.Task.ConfigureAwait(false);
        }).AsTask());
        try
        {
            await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await attacker.RunPersistenceExclusiveAsync(async () =>
            {
                await carrier.AttackByAsync(attacker, null, false).ConfigureAwait(false);
            }).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(carrier.IsAlive, Is.False);
                Assert.That(HasRelic(carrier), Is.True, "The death callback must defer persisted relic deletion while the victim's lock is held.");
                Assert.That(GetDrops(world.Game).Any(drop => IsRelic(drop.Item)), Is.False);
            });
        }
        finally
        {
            release.TrySetResult();
            await saving.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        await WaitUntilAsync(() => !HasRelic(carrier) && GetDrops(world.Game).Any(drop => IsRelic(drop.Item)), TimeSpan.FromSeconds(2), "The relic must drop after the victim's persistence lock is released.").ConfigureAwait(false);
    }

    /// <summary>Ten admitted players are split evenly, and the ordinary attack route rejects friendly fire.</summary>
    [Test]
    [Timeout(90_000)]
    public async Task TenEntrantsSplitEvenlyAndRejectFriendlyFireAsync()
    {
        await using var world = await TestWorld.CreateAsync(10).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);

        var teams = world.Players.Select(world.Game.GetTeam).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(world.Game.PlayerCount, Is.EqualTo(10));
            Assert.That(teams.Count(team => team == 0), Is.EqualTo(5));
            Assert.That(teams.Count(team => team == 1), Is.EqualTo(5));
            Assert.That(teams.All(team => team < 2), Is.True);
        });

        var attacker = world.Players[0];
        var friendly = world.Players.First(player => player != attacker && world.Game.GetTeam(player) == world.Game.GetTeam(attacker));
        attacker.Position = new Point(120, 120);
        friendly.Position = attacker.Position;
        friendly.Attributes![Stats.CurrentHealth] = 10_000;
        var healthBefore = friendly.Attributes[Stats.CurrentHealth];

        await friendly.AttackByAsync(attacker, null, false).ConfigureAwait(false);

        Assert.That(friendly.Attributes[Stats.CurrentHealth], Is.EqualTo(healthBefore), "The real Player.AttackByAsync path must preserve an ally's health.");
    }

    /// <summary>Opponent kills fund event skills; rejected skills keep their points and the relic can be recovered and scored.</summary>
    [Test]
    [Timeout(150_000)]
    public async Task PvPKillSkillsRelicRecoveryAndSingleRewardUseRealActionsAsync()
    {
        await using var world = await TestWorld.CreateAsync(6).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);

        var caster = world.Players[0];
        var teammate = world.Players.First(player => player != caster && world.Game.GetTeam(player) == world.Game.GetTeam(caster));
        var otherTeammate = world.Players.First(player => player != caster && player != teammate && world.Game.GetTeam(player) == world.Game.GetTeam(caster));
        var opponents = world.Players.Where(player => world.Game.GetTeam(player) != world.Game.GetTeam(caster)).ToArray();
        var firstVictim = opponents[0];
        var secondVictim = opponents[1];

        await world.Game.UseSkillAsync(caster, 210, ViewExtensions.ConstantPlayerId).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(LastSkillPoints(caster), Is.Zero, "An unaffordable skill must not change the event-point balance.");
            Assert.That(LastSkillSucceeded(caster), Is.False);
        });

        var foreignPlayer = await world.AddForeignPlayerAsync().ConfigureAwait(false);
        await world.Game.UseSkillAsync(caster, 211, foreignPlayer.Id).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(LastSkillPoints(caster), Is.Zero, "A non-member on the event map must not consume points.");
            Assert.That(LastSkillSucceeded(caster), Is.False, "A non-member target must be rejected by the event skill path.");
        });

        caster.Position = new Point(100, 100);
        firstVictim.Position = caster.Position;
        firstVictim.Attributes![Stats.CurrentShield] = 0;
        firstVictim.Attributes[Stats.CurrentHealth] = 1;
        await firstVictim.AttackByAsync(caster, null, false).ConfigureAwait(false);
        Assert.That(firstVictim.IsAlive, Is.False, "The opposing player must die through the real PvP attack pipeline.");
        Assert.That(LastSkillPoints(caster), Is.EqualTo(4), "An opposing PvP kill must award the configured event points.");

        var statue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        teammate.Position = statue.Position;
        otherTeammate.Position = statue.Position;
        caster.Position = statue.Position;
        await Task.WhenAll(
            world.Game.TalkToEventNpcAsync(teammate, statue).AsTask(),
            world.Game.TalkToEventNpcAsync(otherTeammate, statue).AsTask()).ConfigureAwait(false);
        await WaitUntilAsync(
            () => world.Players.Count(HasRelic) == 1,
            TimeSpan.FromSeconds(5),
            "Concurrent statue channels should produce one relic holder.").ConfigureAwait(false);
        var holder = world.Players.Single(HasRelic);
        Assert.That(GetNpcs(world.Game), Does.Not.Contain(statue), "Capturing the relic must remove the selected statue until a score is made.");

        var farPoint = holder.Position == new Point(20, 20) ? new Point(30, 30) : new Point(20, 20);
        caster.Position = farPoint;
        secondVictim.Position = new Point((byte)(farPoint.X + 20), farPoint.Y);
        await world.Game.UseSkillAsync(caster, 211, secondVictim.Id).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(LastSkillPoints(caster), Is.EqualTo(4), "An out-of-range skill target must not consume points.");
            Assert.That(LastSkillSucceeded(caster), Is.False);
        });

        await world.Game.UseSkillAsync(caster, 212, ViewExtensions.ConstantPlayerId).ConfigureAwait(false);
        await caster.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
        Assert.That(caster.Position, Is.EqualTo(holder.Position), "Pursuit must use the server-side relic holder position.");
        Assert.That(LastSkillSucceeded(caster), Is.True);
        Assert.That(LastSkillPoints(caster), Is.EqualTo(3));

        await world.Game.UseSkillAsync(caster, 210, ViewExtensions.ConstantPlayerId).ConfigureAwait(false);
        Assert.That(LastSkillSucceeded(caster), Is.True);
        Assert.That(LastSkillPoints(caster), Is.EqualTo(2));

        secondVictim.Position = new Point((byte)(caster.Position.X + 1), caster.Position.Y);
        await world.Game.UseSkillAsync(caster, 211, secondVictim.Id).ConfigureAwait(false);
        Assert.That(LastSkillSucceeded(caster), Is.True);
        Assert.That(LastSkillPoints(caster), Is.EqualTo(1));
        Assert.That(secondVictim.MagicEffectList.ContainsEffect(203), Is.True, "The valid restraint must apply its timed effect.");

        secondVictim.Attributes![Stats.CurrentShield] = 100;
        await world.Game.UseSkillAsync(caster, 213, secondVictim.Id).ConfigureAwait(false);
        Assert.That(LastSkillSucceeded(caster), Is.True);
        Assert.That(LastSkillPoints(caster), Is.Zero);
        Assert.That(secondVictim.Attributes[Stats.CurrentShield], Is.EqualTo(50), "Shield burn must apply the configured shield fraction.");

        await WaitUntilAsync(
            () => !caster.MagicEffectList.ContainsEffect(202) && !secondVictim.MagicEffectList.ContainsEffect(203),
            TimeSpan.FromSeconds(5),
            "Protection and restraint should expire at the configured fixture duration.").ConfigureAwait(false);

        holder.Position = caster.Position;
        holder.Attributes![Stats.CurrentShield] = 0;
        holder.Attributes[Stats.CurrentHealth] = 1;
        await holder.AttackByAsync(secondVictim, null, false).ConfigureAwait(false);
        Assert.That(holder.IsAlive, Is.False, "The relic carrier must die through a normal opponent attack.");
        var spawnGate = new SpawnGateSelectionArgs();
        await world.EventPlugIn.SelectSpawnGateAsync(holder, spawnGate).ConfigureAwait(false);
        Assert.That(spawnGate.Gate, Is.SameAs(world.Game.GetTeamSpawnGate(holder)), "The normal spawn-gate selection plug-in must choose the dead player's team base.");
        await WaitUntilAsync(
            () => GetDrops(world.Game).Any(drop => IsRelic(drop.Item)),
            TimeSpan.FromSeconds(5),
            "Death must create a recoverable ground relic.").ConfigureAwait(false);

        var relicDrop = GetDrops(world.Game).Single(drop => IsRelic(drop.Item));
        caster.Position = relicDrop.Position;
        await new PickupItemAction().PickupItemAsync(caster, relicDrop.Id).ConfigureAwait(false);
        await WaitUntilAsync(() => HasRelic(caster), TimeSpan.FromSeconds(3), "The normal pickup action must transfer the ground relic to the new carrier.").ConfigureAwait(false);
        var pickedUpRelic = caster.Inventory!.Items.Single(IsRelic);
        Mock.Get(caster.ViewPlugIns.GetPlugIn<IItemAppearPlugIn>()!).Verify(view => view.ItemAppearAsync(pickedUpRelic), Times.Once);

        var experienceBeforeReward = caster.SelectedCharacter!.Experience;
        var pedestal = GetNpcs(world.Game).Single(npc => npc.Definition.Number == 383 + world.Game.GetTeam(caster));
        caster.Position = pedestal.Position;
        await InteractUntilAsync(world.Game, caster, pedestal, () => !HasRelic(caster), "The recovered relic must deposit normally.").ConfigureAwait(false);
        await WaitUntilAsync(() => GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380) == 2, TimeSpan.FromSeconds(5), "The first score must restore both statues.").ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(world.Game.State, Is.EqualTo(MiniGameState.Playing), "One score must not end a two-point fixture match.");
            Assert.That(caster.SelectedCharacter!.Experience, Is.EqualTo(experienceBeforeReward), "A first deposit must not award the finishing reward.");
        });

        var nextStatue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        caster.Position = nextStatue.Position;
        await InteractUntilAsync(world.Game, caster, nextStatue, () => HasRelic(caster), "A restored statue must grant the next relic.").ConfigureAwait(false);
        caster.Position = pedestal.Position;
        await InteractUntilAsync(world.Game, caster, pedestal, () => world.Game.State == MiniGameState.Ended, "The second deposit must end the fixture match.").ConfigureAwait(false);
        Assert.That(HasRelic(caster), Is.False, "Depositing the relic must consume it.");
        await WaitUntilAsync(
            () => caster.SelectedCharacter!.Experience > experienceBeforeReward,
            TimeSpan.FromSeconds(5),
            "The event-end reward callback should award the winner's configured experience.").ConfigureAwait(false);

        var character = caster.SelectedCharacter!;
        var experienceAfterReward = character.Experience;
        Assert.That(experienceAfterReward, Is.GreaterThan(experienceBeforeReward), "An alive winner must receive the configured rank-one experience reward.");
        await world.Game.ClaimRewardAsync(caster).ConfigureAwait(false);
        Assert.That(character.Experience, Is.EqualTo(experienceAfterReward), "Replaying the reward claim must not pay twice.");

        await world.Game.DisposeAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(world.Game.IsDisposed, Is.True);
            Assert.That(world.Game.Map.IsTornDown, Is.True);
            Assert.That(world.Game.PlayerCount, Is.Zero);
            Assert.That(caster.CurrentMiniGame, Is.Null);
            Assert.That(caster.CurrentMap, Is.Not.SameAs(world.Game.Map));
            Assert.That(world.Players.All(player => !player.Inventory!.Items.Any(item => item.Definition is { Group: 14, Number: 64 })), Is.True);
        });
    }

    /// <summary>A disposed ground relic stays reserved while pickup persistence is pending, then deposits normally.</summary>
    [Test]
    [Timeout(90_000)]
    public async Task PendingRelicPickupDoesNotRestoreStatuesBeforePickupCallbackAsync()
    {
        await using var world = await TestWorld.CreateAsync(2).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);

        var carrier = world.Players[0];
        var picker = world.Players[1];
        var statueCount = GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380);
        Assert.That(statueCount, Is.EqualTo(2));
        var statue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        carrier.Position = statue.Position;
        await world.Game.TalkToEventNpcAsync(carrier, statue).ConfigureAwait(false);
        await WaitUntilAsync(() => HasRelic(carrier), TimeSpan.FromSeconds(5), "The normal statue channel should give the carrier the relic.").ConfigureAwait(false);

        carrier.Attributes![Stats.CurrentShield] = 0;
        carrier.Attributes[Stats.CurrentHealth] = 1;
        picker.Attributes!.AddElement(new ConstantElement(1_000_000), Stats.MinimumPhysBaseDmg);
        picker.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumPhysBaseDmg);
        await carrier.AttackByAsync(picker, null, false).ConfigureAwait(false);
        Assert.That(carrier.IsAlive, Is.False, "The carrier must die through the real PvP attack pipeline.");
        await WaitUntilAsync(
            () => GetDrops(world.Game).Any(drop => IsRelic(drop.Item)),
            TimeSpan.FromSeconds(5),
            "Carrier death should create the relic drop.").ConfigureAwait(false);

        var relicDrop = GetDrops(world.Game).Single(drop => IsRelic(drop.Item));
        var pickup = await relicDrop.TryPickUpByAsync(picker).ConfigureAwait(false);
        Assert.That(pickup.Success, Is.True, "The drop's ordinary inventory pickup must succeed before its player callback.");
        await Task.Delay(TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(GetDrops(world.Game).Any(drop => IsRelic(drop.Item)), Is.False, "The disposed drop must no longer be visible on the ground.");
            Assert.That(picker.Inventory!.Items.Count(IsRelic), Is.EqualTo(1), "Exactly one relic must be in the picker inventory while persistence/callback is pending.");
            Assert.That(GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380), Is.Zero, "The one-second info loop must not restore either statue while the picked-up relic is in inventory.");
        });

        await picker.OnPickedUpItemAsync(relicDrop).ConfigureAwait(false);
        Assert.That(HasRelic(picker), Is.True, "The normal player pickup callback should transfer relic ownership to the picker.");
        var pedestal = GetNpcs(world.Game).Single(npc => npc.Definition.Number == 383 + world.Game.GetTeam(picker));
        picker.Position = pedestal.Position;
        await InteractUntilAsync(world.Game, picker, pedestal, () => !HasRelic(picker), "The picked-up relic should remain valid for a normal score deposit.").ConfigureAwait(false);
        await WaitUntilAsync(() => GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380) == 2, TimeSpan.FromSeconds(5), "The deposit should restore both statues for the next capture.").ConfigureAwait(false);
        Assert.That(HasRelic(picker), Is.False, "The normal pedestal deposit should consume the relic.");
        Assert.That(world.Game.State, Is.EqualTo(MiniGameState.Playing));
    }

    /// <summary>An expired death relic becomes unavailable and restores the statue for another capture.</summary>
    [Test]
    [Timeout(90_000)]
    public async Task ExpiredDeathRelicRestoresStatueForNextCaptureAsync()
    {
        await using var world = await TestWorld.CreateAsync(2, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
        await world.StartAsync().ConfigureAwait(false);

        var holder = world.Players[0];
        var opponent = world.Players[1];
        var expectedStatueCount = GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380);
        Assert.That(expectedStatueCount, Is.EqualTo(2), "The initialized IT map must begin with both configured statues.");
        var statue = GetNpcs(world.Game).First(npc => npc.Definition.Number == 380);
        holder.Position = statue.Position;
        await world.Game.TalkToEventNpcAsync(holder, statue).ConfigureAwait(false);
        await WaitUntilAsync(() => HasRelic(holder), TimeSpan.FromSeconds(5), "The configured statue channel should grant the relic.").ConfigureAwait(false);

        holder.Attributes![Stats.CurrentShield] = 0;
        holder.Attributes[Stats.CurrentHealth] = 1;
        opponent.Attributes!.AddElement(new ConstantElement(1_000_000), Stats.MinimumPhysBaseDmg);
        opponent.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumPhysBaseDmg);
        await holder.AttackByAsync(opponent, null, false).ConfigureAwait(false);
        Assert.That(holder.IsAlive, Is.False, "The carrier must die through a real opposing attack.");
        await WaitUntilAsync(
            () => GetDrops(world.Game).Any(drop => IsRelic(drop.Item)),
            TimeSpan.FromSeconds(1),
            "The carrier's relic should appear as a ground item before its expiration.").ConfigureAwait(false);

        await WaitUntilAsync(
            () => !GetDrops(world.Game).Any(drop => IsRelic(drop.Item)),
            TimeSpan.FromSeconds(5),
            "The ground relic should be removed when its configured item-drop timer expires.").ConfigureAwait(false);
        await WaitUntilAsync(
            () => GetNpcs(world.Game).Count(npc => npc.Definition.Number == 380) == expectedStatueCount,
            TimeSpan.FromSeconds(5),
            "After relic expiry, the event should restore both configured statues.").ConfigureAwait(false);
        Assert.That(HasRelic(holder), Is.False);
    }

    private static byte LastSkillPoints(Player player)
    {
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IIllusionTempleViewPlugIn>()!);
        return (byte)view.Invocations.Last(invocation => invocation.Method.Name == nameof(IIllusionTempleViewPlugIn.ShowSkillPointsAsync)).Arguments[0]!;
    }

    private static bool LastSkillSucceeded(Player player)
    {
        var view = Mock.Get(player.ViewPlugIns.GetPlugIn<IIllusionTempleViewPlugIn>()!);
        return (bool)view.Invocations.Last(invocation => invocation.Method.Name == nameof(IIllusionTempleViewPlugIn.ShowSkillResultAsync)).Arguments[3]!;
    }

    private static bool HasRelic(Player player) => player.Inventory?.Items.Any(item => IsRelic(item)) is true;

    private static bool IsRelic(MUnique.OpenMU.DataModel.Entities.Item item) => item.Definition is { Group: 14, Number: 64 };

    private static IList<NonPlayerCharacter> GetNpcs(IllusionTempleContext game) => game.Map.GetNpcsInRange(new Point(128, 128), 255);

    private static IList<DroppedItem> GetDrops(IllusionTempleContext game) => game.Map.GetDropsInRange(new Point(128, 128), 255).OfType<DroppedItem>().ToList();

    private static async Task InteractUntilAsync(IllusionTempleContext game, Player player, NonPlayerCharacter npc, Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await game.TalkToEventNpcAsync(player, npc).ConfigureAwait(false);
            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.That(condition(), Is.True, failureMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reached = condition();
        while (!reached && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
            reached = condition();
        }

        Assert.That(reached, Is.True, failureMessage);
    }

    private sealed class TestWorld : IAsyncDisposable
    {
        private TestWorld(GameContext gameContext, MiniGameDefinition definition, IReadOnlyList<Player> players, IllusionTemplePlugIn eventPlugIn)
        {
            this.Context = gameContext;
            this.Definition = definition;
            this.Players = players;
            this.EventPlugIn = eventPlugIn;
        }

        public GameContext Context { get; }

        public MiniGameDefinition Definition { get; }

        public IReadOnlyList<Player> Players { get; }

        public IllusionTemplePlugIn EventPlugIn { get; }

        public IllusionTempleContext Game { get; private set; } = null!;

        public static async ValueTask<TestWorld> CreateAsync(int playerCount, TimeSpan? itemDropDuration = null, byte winningScore = 2)
        {
            var provider = new InMemoryPersistenceContextProvider();
            await new Persistence.Initialization.VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance)
                .CreateInitialDataAsync(1, true).ConfigureAwait(false);
            using var persistenceContext = provider.CreateNewContext();
            var configuration = (await persistenceContext.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
            if (itemDropDuration is { } dropDuration)
            {
                configuration.ItemDropDuration = dropDuration;
            }
            var definition = configuration.MiniGameDefinitions.Single(candidate => candidate.Type == MiniGameType.IllusionTemple && candidate.GameLevel == 1) as MiniGameDefinition
                ?? throw new InvalidOperationException("The initialized Illusion Temple definition must use the persistence model.");
            definition.MapCreationPolicy = MiniGameMapCreationPolicy.Shared;
            definition.MaximumPlayerCount = 10;
            definition.EnterDuration = TimeSpan.FromSeconds(30);
            definition.GameDuration = TimeSpan.FromMinutes(3);
            definition.ExitDuration = TimeSpan.Zero;

            // Fixture-only compression: keep initialized maps, tickets, NPC definitions, player actions, and rewards,
            // while shortening channel/effect/drop timers and choosing the score threshold. StartAsync skips lobby/countdown waits.
            var startPlugIn = new IllusionTempleStartPlugIn
            {
                Configuration = new IllusionTempleStartConfiguration
                {
                    Timetable = [],
                    TaskDuration = TimeSpan.FromMinutes(3),
                    PreStartMessageDelay = TimeSpan.Zero,
                    MinimumPlayers = 2,
                    WinningScore = winningScore,
                    SkillPointsPerKill = 4,
                    CaptureDuration = TimeSpan.FromMilliseconds(150),
                    SkillDuration = TimeSpan.FromSeconds(3),
                    ProtectionCost = 1,
                    RestraintCost = 1,
                    PursuitCost = 1,
                    ShieldBurnCost = 1,
                    ShieldBurnFactor = 0.5f,
                },
            };
            var plugIns = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
            var eventPlugIn = new IllusionTemplePlugIn();
            plugIns.RegisterPlugInAtPlugInPoint<IPeriodicMiniGameStartPlugIn>(startPlugIn);
            plugIns.RegisterPlugInAtPlugInPoint<IAttackableGotKilledPlugIn>(eventPlugIn);
            plugIns.RegisterPlugInAtPlugInPoint<IPlayerSpawnGateSelectionPlugIn>(eventPlugIn);
            var drops = NullDropGenerator.Instance;
            var mediator = new ConfigurationChangeMediator();
            var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, drops, mediator);
            var gameContext = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance, plugIns, drops, mediator);
            initializer.PlugInManager = plugIns;
            initializer.PathFinderPool = gameContext.PathFinderPool;
            gameContext.StopPeriodicTasks();

            var players = new List<Player>(playerCount);
            var homeMap = (await gameContext.GetMapAsync(0).ConfigureAwait(false))!;
            var homePoint = homeMap.Terrain.GetWalkableCoordinate(homeMap.SafeZoneSpawnGate);
            Assert.That(homePoint, Is.Not.Null, "A real initialized home-map spawn point is required before entering the match.");
            for (var index = 0; index < playerCount; index++)
            {
                var player = await PlayerTestHelper.CreatePlayerAsync(gameContext).ConfigureAwait(false);
                player.SelectedCharacter!.Id = Guid.NewGuid();
                player.SelectedCharacter.Name = $"ITLifecycle{index:D2}";
                player.Attributes![Stats.Level] = 220;
                player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.AttackRatePvp);
                player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MinimumPhysBaseDmg);
                player.Attributes.AddElement(new ConstantElement(1_000_000), Stats.MaximumPhysBaseDmg);
                player.Attributes.AddElement(new ConstantElement(100_000), Stats.MaximumHealth);
                player.Attributes[Stats.CurrentHealth] = 100_000;
                player.Attributes[Stats.CurrentShield] = 0;
                await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
                player.IsAlive = true;
                var homeGate = new ExitGate
                {
                    Map = homeMap.Definition,
                    X1 = homePoint!.Value.X,
                    X2 = homePoint.Value.X,
                    Y1 = homePoint.Value.Y,
                    Y2 = homePoint.Value.Y,
                };
                await player.WarpToAsync(homeGate).ConfigureAwait(false);
                await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                Assert.That(player.CurrentMap, Is.SameAs(homeMap), "Each actor must complete its initial world warp before admission.");
                players.Add(player);
            }

            return new TestWorld(gameContext, definition, players, eventPlugIn);
        }

        public async ValueTask StartAsync(bool fromPlayerPacket = false)
        {
            async ValueTask OpenRegistrationAsync()
            {
                var startPlugIn = this.Context.PlugInManager.GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(MiniGameType.IllusionTemple)!;
                startPlugIn.ForceStart();
                await startPlugIn.ExecuteTaskAsync(this.Context).ConfigureAwait(false);
                await startPlugIn.ExecuteTaskAsync(this.Context).ConfigureAwait(false);
                this.Game = (IllusionTempleContext)await this.Context.MiniGames.GetOrCreateAsync(this.Definition, this.Players[0]).ConfigureAwait(false);
            }

            if (fromPlayerPacket)
            {
                await this.Players[0].RunPersistenceExclusiveAsync(OpenRegistrationAsync).ConfigureAwait(false);
            }
            else
            {
                await OpenRegistrationAsync().ConfigureAwait(false);
            }

            await WaitUntilAsync(() => this.Game.State == MiniGameState.Open, TimeSpan.FromSeconds(5), "The real manager should open IT registration after ForceStart.").ConfigureAwait(false);
            foreach (var player in this.Players)
            {
                var ticketDefinition = this.Context.Configuration.Items.Single(item => item.Group == 13 && item.Number == 51);
                var ticket = player.PersistenceContext.CreateNew<Item>();
                ticket.Definition = ticketDefinition;
                ticket.Level = checked((byte)this.Definition.TicketItemLevel);
                ticket.Durability = 1;
                Assert.That(await player.Inventory!.AddItemAsync(20, ticket).ConfigureAwait(false), Is.True);
                await new EnterMiniGameAction().TryEnterMiniGameAsync(player, MiniGameType.IllusionTemple, this.Definition.GameLevel, 20).ConfigureAwait(false);
                Assert.That(player.CurrentMiniGame, Is.SameAs(this.Game), "The normal admission action must attach the player to the shared match.");
                Assert.That(player.Inventory.Items, Does.Not.Contain(ticket), "The normal admission action must consume the configured ticket.");
                await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            }

            await WaitUntilAsync(
                () =>
                {
                    if (this.Game.State == MiniGameState.Playing)
                    {
                        return true;
                    }

                    this.Game.SkipCurrentWait();
                    return false;
                },
                TimeSpan.FromSeconds(15),
                "The real event loop should close admission, skip only its active lobby/countdown waits, and start play.").ConfigureAwait(false);

            await WaitUntilAsync(
                () => this.Players.All(player => player.PlayerState.CurrentState == PlayerState.ChangingMap),
                TimeSpan.FromSeconds(5),
                "Starting play should send every participant through its configured team spawn gate.").ConfigureAwait(false);
            foreach (var player in this.Players)
            {
                await player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                Assert.Multiple(() =>
                {
                    Assert.That(player.CurrentMap, Is.SameAs(this.Game.Map));
                    Assert.That(player.IsAlive, Is.True);
                    Assert.That(this.Game.GetTeam(player), Is.LessThan(2));
                });
            }
        }

        public async ValueTask<Player> AddForeignPlayerAsync()
        {
            var player = await PlayerTestHelper.CreatePlayerAsync(this.Context).ConfigureAwait(false);
            player.SelectedCharacter!.Name = "ITLifecycleForeign";
            await player.PlayerState.TryAdvanceToAsync(PlayerState.EnteredWorld).ConfigureAwait(false);
            player.IsAlive = true;
            player.CurrentMap = this.Game.Map;
            await this.Game.Map.AddAsync(player).ConfigureAwait(false);
            Assert.That(player.CurrentMap, Is.SameAs(this.Game.Map));
            Assert.That(player.CurrentMiniGame, Is.Null, "The target must be present on the map without admission to this match.");
            return player;
        }

        public async ValueTask DisposeAsync()
        {
            if (this.Game is { IsDisposed: false })
            {
                await this.Game.DisposeAsync().ConfigureAwait(false);
            }

            await this.Context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
