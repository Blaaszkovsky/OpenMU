// <copyright file="CastleSiegeStateMachineTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.CastleSiege;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Tests the Castle Siege weekly state machine.
/// </summary>
[TestFixture]
public class CastleSiegeStateMachineTests
{
    /// <summary>
    /// Verifies that the plug-in manager discovers and activates the Castle Siege periodic task.
    /// </summary>
    [Test]
    public void PlugInIsDiscoveredAndActivated()
    {
        var manager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);

        manager.DiscoverAndRegisterPlugIns(typeof(CastleSiegePlugIn).Assembly);
        manager.ActivatePlugIn<CastleSiegePlugIn>();

        Assert.That(manager.GetKnownPlugInsOf<IPeriodicTaskPlugIn>(), Does.Contain(typeof(CastleSiegePlugIn)));
        Assert.That(manager.GetActivePlugInsOf<IPeriodicTaskPlugIn>(), Has.Some.InstanceOf<CastleSiegePlugIn>());
    }

    /// <summary>
    /// Verifies that the current state is calculated from the configured UTC week.
    /// </summary>
    /// <param name="utcTime">The current UTC time.</param>
    /// <param name="expectedState">The expected state.</param>
    [TestCase("2026-08-02T12:00:00Z", CastleSiegeState.Idle1)]
    [TestCase("2026-08-03T12:00:00Z", CastleSiegeState.RegisterGuild)]
    [TestCase("2026-08-08T19:00:00Z", CastleSiegeState.Ready)]
    [TestCase("2026-08-08T21:00:00Z", CastleSiegeState.Start)]
    [TestCase("2026-08-08T22:03:00Z", CastleSiegeState.End)]
    [TestCase("2026-08-08T23:00:00Z", CastleSiegeState.EndCycle)]
    public void CurrentStateIsCalculatedFromWeeklySchedule(string utcTime, CastleSiegeState expectedState)
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.Utc);

        var period = schedule.GetCurrentPeriod(DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.That(period.State, Is.EqualTo(expectedState));
        Assert.That(period.StartUtc, Is.LessThanOrEqualTo(DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
        Assert.That(period.EndUtc, Is.GreaterThan(DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
    }

    /// <summary>
    /// Verifies that the early registration period survives a restart through the scheduled idle and registration states.
    /// </summary>
    /// <param name="utcTime">The restart time.</param>
    [TestCase("2026-08-09T12:00:00Z")]
    [TestCase("2026-08-10T12:00:00Z")]
    public void EarlyRegistrationPeriodIsRecoveredAcrossRestart(string utcTime)
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.Utc);
        var period = schedule.GetCurrentEventPeriod(
            DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal));

        Assert.Multiple(() =>
        {
            Assert.That(period.State, Is.EqualTo(CastleSiegeState.RegisterGuild));
            Assert.That(period.StartUtc, Is.EqualTo(new DateTime(2026, 8, 8, 22, 5, 0, DateTimeKind.Utc)));
            Assert.That(period.EndUtc, Is.EqualTo(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that the schedule wraps from the last state to Sunday.
    /// </summary>
    [Test]
    public void LastStateEndsAtStartOfNextWeek()
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.Utc);
        var utcNow = new DateTime(2026, 8, 8, 23, 0, 0, DateTimeKind.Utc);

        var period = schedule.GetCurrentPeriod(utcNow);

        Assert.That(period.State, Is.EqualTo(CastleSiegeState.EndCycle));
        Assert.That(period.EndUtc, Is.EqualTo(new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Verifies that the configured times are interpreted in the server time zone, independent of the DST offset.
    /// </summary>
    /// <param name="timeZoneId">The server time zone id.</param>
    /// <param name="utcTime">The current UTC time.</param>
    /// <param name="expectedState">The expected state.</param>
    /// <param name="expectedStartUtc">The expected UTC start of the state.</param>
    /// <param name="expectedEndUtc">The expected UTC end of the state.</param>
    [TestCase("Europe/Warsaw", "2026-08-08T15:59:00Z", CastleSiegeState.Notify, "2026-08-06T22:00:00Z", "2026-08-08T16:00:00Z")]
    [TestCase("Europe/Warsaw", "2026-08-08T17:59:00Z", CastleSiegeState.Ready, "2026-08-08T16:00:00Z", "2026-08-08T18:00:00Z")]
    [TestCase("Europe/Warsaw", "2026-08-08T18:00:00Z", CastleSiegeState.Start, "2026-08-08T18:00:00Z", "2026-08-08T20:00:00Z")]
    [TestCase("Europe/Warsaw", "2026-12-12T18:59:00Z", CastleSiegeState.Ready, "2026-12-12T17:00:00Z", "2026-12-12T19:00:00Z")]
    [TestCase("Europe/Warsaw", "2026-12-12T19:00:00Z", CastleSiegeState.Start, "2026-12-12T19:00:00Z", "2026-12-12T21:00:00Z")]
    [TestCase("America/New_York", "2026-08-08T23:59:00Z", CastleSiegeState.Ready, "2026-08-08T22:00:00Z", "2026-08-09T00:00:00Z")]
    [TestCase("America/New_York", "2026-08-09T00:00:00Z", CastleSiegeState.Start, "2026-08-09T00:00:00Z", "2026-08-09T02:00:00Z")]
    [TestCase("Asia/Kolkata", "2026-08-08T14:29:00Z", CastleSiegeState.Ready, "2026-08-08T12:30:00Z", "2026-08-08T14:30:00Z")]
    [TestCase("UTC", "2026-08-08T20:00:00Z", CastleSiegeState.Start, "2026-08-08T20:00:00Z", "2026-08-08T22:00:00Z")]
    public void CurrentPeriodIsCalculatedInServerTimeZone(string timeZoneId, string utcTime, CastleSiegeState expectedState, string expectedStartUtc, string expectedEndUtc)
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

        var period = schedule.GetCurrentPeriod(ParseUtc(utcTime));

        Assert.Multiple(() =>
        {
            Assert.That(period.State, Is.EqualTo(expectedState));
            Assert.That(period.StartUtc, Is.EqualTo(ParseUtc(expectedStartUtc)));
            Assert.That(period.EndUtc, Is.EqualTo(ParseUtc(expectedEndUtc)));
        });
    }

    /// <summary>
    /// Verifies that the week boundary follows the local Sunday midnight, which is not
    /// the UTC midnight, so the same UTC instant belongs to different states in different zones.
    /// </summary>
    [Test]
    public void WeekStartFollowsLocalMidnight()
    {
        var utcNow = new DateTime(2026, 8, 8, 23, 30, 0, DateTimeKind.Utc);

        var utcPeriod = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.Utc).GetCurrentPeriod(utcNow);
        var warsawPeriod = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")).GetCurrentPeriod(utcNow);

        Assert.Multiple(() =>
        {
            Assert.That(utcPeriod.State, Is.EqualTo(CastleSiegeState.EndCycle));
            Assert.That(warsawPeriod.State, Is.EqualTo(CastleSiegeState.Idle1));
            Assert.That(warsawPeriod.StartUtc, Is.EqualTo(new DateTime(2026, 8, 8, 22, 0, 0, DateTimeKind.Utc)));
            Assert.That(warsawPeriod.EndUtc, Is.EqualTo(new DateTime(2026, 8, 9, 22, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that a state which spans a DST change keeps its local start and end times,
    /// so its duration is 23 or 25 hours instead of 24.
    /// </summary>
    /// <param name="utcTime">The current UTC time.</param>
    /// <param name="expectedStartUtc">The expected UTC start of the state.</param>
    /// <param name="expectedEndUtc">The expected UTC end of the state.</param>
    [TestCase("2026-03-29T12:00:00Z", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [TestCase("2026-10-25T12:00:00Z", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    public void StateSpanningDstChangeKeepsLocalBoundaries(string utcTime, string expectedStartUtc, string expectedEndUtc)
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        var period = schedule.GetCurrentPeriod(ParseUtc(utcTime));

        Assert.Multiple(() =>
        {
            Assert.That(period.State, Is.EqualTo(CastleSiegeState.Idle1));
            Assert.That(period.StartUtc, Is.EqualTo(ParseUtc(expectedStartUtc)));
            Assert.That(period.EndUtc, Is.EqualTo(ParseUtc(expectedEndUtc)));
        });
    }

    /// <summary>
    /// Verifies that the Saturday start time stays at the same local time before and after the DST change.
    /// </summary>
    [Test]
    public void StartStaysAtLocalTimeAcrossDstChange()
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        var beforeChange = schedule.GetCurrentPeriod(new DateTime(2026, 3, 28, 19, 30, 0, DateTimeKind.Utc));
        var afterChange = schedule.GetCurrentPeriod(new DateTime(2026, 4, 4, 18, 30, 0, DateTimeKind.Utc));

        Assert.Multiple(() =>
        {
            Assert.That(beforeChange.State, Is.EqualTo(CastleSiegeState.Start));
            Assert.That(beforeChange.StartUtc, Is.EqualTo(new DateTime(2026, 3, 28, 19, 0, 0, DateTimeKind.Utc)));
            Assert.That(afterChange.State, Is.EqualTo(CastleSiegeState.Start));
            Assert.That(afterChange.StartUtc, Is.EqualTo(new DateTime(2026, 4, 4, 18, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that following states from the end of a state continues at the scheduled local time across a DST change.
    /// </summary>
    [Test]
    public void CreatedPeriodEndsAtNextLocalBoundaryAcrossDstChange()
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        var idleStart = new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc);

        var idle = schedule.CreatePeriod(CastleSiegeState.Idle1, idleStart);
        var registration = schedule.CreatePeriod(CastleSiegeState.RegisterGuild, idle.EndUtc);

        Assert.Multiple(() =>
        {
            Assert.That(idle.EndUtc, Is.EqualTo(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc)));
            Assert.That(registration.EndUtc, Is.EqualTo(new DateTime(2026, 3, 30, 22, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that the early registration period after the cycle ends at the local start of the scheduled successor state.
    /// </summary>
    [Test]
    public void EarlyRegistrationPeriodIsCalculatedInServerTimeZone()
    {
        var schedule = new CastleSiegeSchedule(CreateSchedule(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        var period = schedule.GetCurrentEventPeriod(new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc));

        Assert.Multiple(() =>
        {
            Assert.That(period.State, Is.EqualTo(CastleSiegeState.RegisterGuild));
            Assert.That(period.StartUtc, Is.EqualTo(new DateTime(2026, 8, 8, 20, 5, 0, DateTimeKind.Utc)));
            Assert.That(period.EndUtc, Is.EqualTo(new DateTime(2026, 8, 10, 22, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that a scheduled time which does not exist because the clock is set forward is moved by the length of the gap.
    /// </summary>
    [Test]
    public void ScheduledTimeInDstGapIsMovedForward()
    {
        var entries = new[]
        {
            CreateScheduleEntry(CastleSiegeState.Idle1, DayOfWeek.Sunday, 2, 30),
            CreateScheduleEntry(CastleSiegeState.RegisterGuild, DayOfWeek.Monday, 0, 0),
        };
        var schedule = new CastleSiegeSchedule(entries, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));

        var period = schedule.GetCurrentPeriod(new DateTime(2026, 3, 29, 12, 0, 0, DateTimeKind.Utc));

        Assert.Multiple(() =>
        {
            Assert.That(period.State, Is.EqualTo(CastleSiegeState.Idle1));
            Assert.That(period.StartUtc, Is.EqualTo(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that duplicate schedule states are rejected.
    /// </summary>
    [Test]
    public void DuplicateScheduleStateIsRejected()
    {
        var schedule = CreateSchedule().ToList();
        schedule.Add(new CastleSiegeStateScheduleEntry
        {
            State = CastleSiegeState.Start,
            DayOfWeek = DayOfWeek.Saturday,
            Hour = 23,
        });

        Assert.That(() => new CastleSiegeSchedule(schedule, TimeZoneInfo.Utc), Throws.InvalidOperationException);
    }

    /// <summary>
    /// Verifies that persistent state and registrations are loaded and state changes can be saved.
    /// </summary>
    [Test]
    public async ValueTask ContextLoadsAndSavesPersistentStateAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);

        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);

        Assert.That(context.SiegeData.TributeMoney, Is.EqualTo(42));
        Assert.That(context.RegisteredGuilds.ContainsKey(fixture.RegisteredGuildId), Is.True);

        context.SiegeData.TributeMoney = 1234;
        await context.SaveOwnerAsync().ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        Assert.That(savedData.TributeMoney, Is.EqualTo(1234));
    }

    /// <summary>
    /// Verifies that an idle server's end-of-cycle saves preserve another server's capture and destroyed gate.
    /// </summary>
    [Test]
    public async ValueTask UnchangedPeerPreservesCapturedOwnerAndDestroyedNpcAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(277, 0, 250_000, true, true).Definition);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var active = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var idle = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var owner = Guid.NewGuid();
        active.SiegeData.OwnerGuildId = owner;
        active.SiegeData.IsOccupied = true;
        active.ActiveNpcs.Single().PersistedState!.CurrentHp = 0;
        await active.SaveOwnerAsync().ConfigureAwait(false);
        await active.SaveNpcStatesAsync().ConfigureAwait(false);

        await idle.SaveOwnerAsync().ConfigureAwait(false);
        await idle.SaveNpcStatesAsync().ConfigureAwait(false);
        await idle.LoadAsync().ConfigureAwait(false);
        await idle.SaveNpcStatesAsync().ConfigureAwait(false);

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(saved.OwnerGuildId, Is.EqualTo(owner));
            Assert.That(saved.IsOccupied, Is.True);
            Assert.That(saved.NpcStates.Single().CurrentHp, Is.Zero);
            Assert.That(idle.SiegeData.OwnerGuildId, Is.EqualTo(owner));
            Assert.That(idle.ActiveNpcs.Single().PersistedState!.CurrentHp, Is.Zero);
        });
    }

    /// <summary>
    /// Verifies that active servers merge distinct NPC changes and tribute deltas exactly once.
    /// </summary>
    [Test]
    public async ValueTask ActivePeersMergeNpcChangesAndTributeAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(277, 0, 250_000, true, true).Definition);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        first.ActiveNpcs.Single().PersistedState!.CurrentHp = 0;
        second.ActiveNpcs.Single().PersistedState!.DefenseLevel = 1;
        first.SiegeData.TributeMoney += 10;
        second.SiegeData.TributeMoney += 20;
        await first.SaveNpcStatesAsync().ConfigureAwait(false);
        await second.SaveNpcStatesAsync().ConfigureAwait(false);
        await first.SaveOwnerAsync().ConfigureAwait(false);
        await second.SaveOwnerAsync().ConfigureAwait(false);
        await second.SaveOwnerAsync().ConfigureAwait(false);
        await second.SaveNpcStatesAsync().ConfigureAwait(false);

        var saved = await store.ReadAsync().ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(saved.TributeMoney, Is.EqualTo(72));
            Assert.That(saved.NpcStates.Single().CurrentHp, Is.Zero);
            Assert.That(saved.NpcStates.Single().DefenseLevel, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Verifies that a rejected save is retried with a fresh snapshot without losing the local change.
    /// </summary>
    [Test]
    public async ValueTask OwnerSaveRetriesConflictUsingFreshContextAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var context = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        context.SiegeData.TributeMoney += 10;
        store.RejectedSaves = 1;
        var initialReads = store.WriteReads;

        await context.SaveOwnerAsync().ConfigureAwait(false);

        Assert.That((await store.ReadAsync().ConfigureAwait(false)).TributeMoney, Is.EqualTo(52));
        Assert.That(store.RejectedSaves, Is.Zero);
        Assert.That(store.WriteReads - initialReads, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies that separate game contexts coordinate the whole persistent read-modify-save operation.
    /// </summary>
    [Test]
    public async ValueTask ActivePeersSerializePersistentReadModifySaveAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        first.SiegeData.TributeMoney += 10;
        second.SiegeData.TributeMoney += 20;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = () => new ValueTask(release.Task);
        var initialReads = store.WriteReads;
        var firstSave = first.SaveOwnerAsync().AsTask();
        var secondSave = second.SaveOwnerAsync().AsTask();
        try
        {
            Assert.That(store.WriteReads - initialReads, Is.EqualTo(1), "The peer must wait before loading its write snapshot.");
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That((await store.ReadAsync().ConfigureAwait(false)).TributeMoney, Is.EqualTo(72));
    }

    /// <summary>
    /// Verifies that another active server's delayed damage checkpoint cannot resurrect a destroyed structure.
    /// </summary>
    [Test]
    public async ValueTask StaleDamageCheckpointCannotResurrectNpcAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(277, 0, 250_000, true, true).Definition);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        first.ActiveNpcs.Single().PersistedState!.CurrentHp = 0;
        second.ActiveNpcs.Single().PersistedState!.CurrentHp = 200_000;

        await first.SaveNpcStatesAsync().ConfigureAwait(false);
        await second.SaveNpcStatesAsync().ConfigureAwait(false);
        await second.SaveNpcStatesAsync().ConfigureAwait(false);

        Assert.That((await store.ReadAsync().ConfigureAwait(false)).NpcStates.Single().CurrentHp, Is.Zero);
    }

    /// <summary>
    /// Verifies that a stale repair fails instead of undoing a concurrent kill, while a fresh repair remains valid.
    /// </summary>
    [Test]
    public async ValueTask StaleRepairCannotUndoKillButFreshRepairCanRestoreNpcAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 277, 0, 100_000).ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(277, 0, 100_000, true, true).Definition);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        first.ActiveNpcs.Single().PersistedState!.CurrentHp = 0;
        second.ActiveNpcs.Single().PersistedState!.CurrentHp = 250_000;
        await first.SaveNpcStatesAsync().ConfigureAwait(false);

        Assert.ThrowsAsync<PersistenceConflictException>(async () => await second.SaveNpcStatesAsync().ConfigureAwait(false));
        Assert.That((await store.ReadAsync().ConfigureAwait(false)).NpcStates.Single().CurrentHp, Is.Zero);
        var fresh = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        fresh.ActiveNpcs.Single().PersistedState!.CurrentHp = 250_000;
        await fresh.SaveNpcStatesAsync().ConfigureAwait(false);
        Assert.That((await store.ReadAsync().ConfigureAwait(false)).NpcStates.Single().CurrentHp, Is.EqualTo(250_000));
    }

    /// <summary>
    /// Verifies that another server's idempotent capture does not reset taxes collected by the new owner.
    /// </summary>
    [Test]
    public async ValueTask RepeatedCapturePreservesNewTenureTributeAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var owner = Guid.NewGuid();
        CastleSiegeCrownMechanics.ApplyOwner(first, owner);
        CastleSiegeCrownMechanics.ApplyOwner(second, owner);
        await first.SaveOwnerAsync().ConfigureAwait(false);
        first.SiegeData.TributeMoney = 10;
        await first.SaveOwnerAsync().ConfigureAwait(false);

        await second.SaveOwnerAsync().ConfigureAwait(false);

        Assert.That((await store.ReadAsync().ConfigureAwait(false)).TributeMoney, Is.EqualTo(10));
    }

    /// <summary>
    /// Verifies that an exhausted save does not acknowledge a delta which was never committed.
    /// </summary>
    [Test]
    public async ValueTask RejectedSavePreservesBaselineForLaterRetryAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var context = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        context.SiegeData.TributeMoney += 10;
        store.RejectedSaves = 3;

        Assert.ThrowsAsync<PersistenceConflictException>(async () => await context.SaveOwnerAsync().ConfigureAwait(false));
        Assert.That((await store.ReadAsync().ConfigureAwait(false)).TributeMoney, Is.EqualTo(42));
        await context.SaveOwnerAsync().ConfigureAwait(false);
        await context.SaveOwnerAsync().ConfigureAwait(false);
        Assert.That((await store.ReadAsync().ConfigureAwait(false)).TributeMoney, Is.EqualTo(52));
    }

    /// <summary>
    /// Verifies that a configured structure initialized by two servers has a stable local checkpoint before its first save.
    /// </summary>
    [Test]
    public async ValueTask InitialNpcCheckpointPreservesPeerDamageAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(277, 0, 250_000, true, true).Definition);
        fixture.CastleSiegeConfiguration.GateLifeUpgrades.Add(new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeUpgradeDefinition { Level = 0, Value = 250_000 });
        var store = new DetachedSiegeStore(fixture.PersistenceContextProvider);
        var first = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        var second = await CreateDetachedContextAsync(fixture, store).ConfigureAwait(false);
        first.ActiveNpcs.Single().PersistedState!.CurrentHp = 0;

        await first.SaveNpcStatesAsync().ConfigureAwait(false);
        await second.SaveNpcStatesAsync().ConfigureAwait(false);

        Assert.That((await store.ReadAsync().ConfigureAwait(false)).NpcStates.Single().CurrentHp, Is.Zero);
    }

    /// <summary>
    /// Verifies that loading does not silently create a missing persistent state.
    /// </summary>
    [Test]
    public async ValueTask ContextLoadDoesNotCreateMissingPersistentStateAsync()
    {
        var fixture = await CreateFixtureAsync(false).ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await context.LoadAsync().ConfigureAwait(false));

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        Assert.That(await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false), Is.Empty);
    }

    /// <summary>
    /// Verifies that initialization explicitly creates a missing persistent state.
    /// </summary>
    [Test]
    public async ValueTask ContextInitializationCreatesMissingPersistentStateAsync()
    {
        var fixture = await CreateFixtureAsync(false).ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);

        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        Assert.That(context.SiegeData.Id, Is.EqualTo(savedData.Id));
    }

    /// <summary>
    /// Verifies that all database-persisted NPC runtime states are stored, including destroyed and unspawned structures.
    /// </summary>
    [Test]
    public async ValueTask ContextSavesPersistentNpcStatesIncludingDestroyedAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var gateStateId = await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        var statueStateId = await AddNpcStateAsync(fixture, 283, 0, 300_000).ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        context.ActiveNpcs.Add(CreateNpcRuntime(277, 0, 125_000, true, true));
        context.ActiveNpcs.Add(CreateNpcRuntime(283, 0, 0, true, false));
        context.ActiveNpcs.Add(CreateNpcRuntime(216, 0, 1_000, false, true));
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(context.ActiveNpcs[0].Definition);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(context.ActiveNpcs[1].Definition);

        await context.SaveNpcStatesAsync().ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        Assert.That(savedData.NpcStates, Has.Count.EqualTo(2));
        Assert.That(savedData.NpcStates.Select(state => state.MonsterNumber), Is.EquivalentTo(new short[] { 277, 283 }));
        Assert.Multiple(() =>
        {
            var gateState = savedData.NpcStates.Single(state => state.MonsterNumber == 277);
            var statueState = savedData.NpcStates.Single(state => state.MonsterNumber == 283);
            Assert.That(gateState.Id, Is.EqualTo(gateStateId));
            Assert.That(gateState.CurrentHp, Is.EqualTo(125_000));
            Assert.That(statueState.Id, Is.EqualTo(statueStateId));
            Assert.That(statueState.CurrentHp, Is.Zero);
        });
    }

    /// <summary>
    /// Verifies that an incomplete runtime snapshot does not delete persistent NPC states.
    /// </summary>
    [Test]
    public async ValueTask ContextPreservesNpcStatesForIncompleteRuntimeSnapshotAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var gateStateId = await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        context.ActiveNpcs.Add(new CastleSiegeNpcRuntime
        {
            Definition = new CastleSiegeNpcDefinition
            {
                MonsterDefinition = new MonsterDefinition { Number = 277 },
                InstanceId = 0,
                IsPersistedToDatabase = true,
            },
            IsAlive = true,
        });

        await context.SaveNpcStatesAsync().ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedState = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false))
            .Single()
            .NpcStates
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(savedState.Id, Is.EqualTo(gateStateId));
            Assert.That(savedState.CurrentHp, Is.EqualTo(250_000));
        });
    }

    /// <summary>
    /// Verifies that missing configured runtime keys cannot overwrite or delete saved NPC states.
    /// </summary>
    /// <param name="replaceMissingKey">Whether an unrelated runtime key replaces the missing key.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async ValueTask ContextPreservesNpcStatesForMissingRuntimeKeyAsync(bool replaceMissingKey)
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var gateStateId = await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        var statueStateId = await AddNpcStateAsync(fixture, 283, 0, 300_000).ConfigureAwait(false);
        var gate = CreateNpcRuntime(277, 0, 125_000, true, true);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(gate.Definition);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(CreateNpcRuntime(283, 0, 0, true, false).Definition);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        context.ActiveNpcs.Clear();
        context.ActiveNpcs.Add(gate);
        if (replaceMissingKey)
        {
            context.ActiveNpcs.Add(CreateNpcRuntime(283, 1, 0, true, false));
        }

        await context.SaveNpcStatesAsync().ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedStates = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single().NpcStates;
        Assert.That(savedStates, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            var savedGate = savedStates.Single(state => state.MonsterNumber == 277);
            var savedStatue = savedStates.Single(state => state.MonsterNumber == 283);
            Assert.That(savedGate.Id, Is.EqualTo(gateStateId));
            Assert.That(savedGate.CurrentHp, Is.EqualTo(250_000));
            Assert.That(savedStatue.Id, Is.EqualTo(statueStateId));
            Assert.That(savedStatue.InstanceId, Is.Zero);
            Assert.That(savedStatue.CurrentHp, Is.EqualTo(300_000));
        });
    }

    /// <summary>
    /// Verifies that a complete current configuration can still remove an obsolete saved NPC state.
    /// </summary>
    [Test]
    public async ValueTask ContextRemovesNpcStateRemovedFromConfigurationAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var gateStateId = await AddNpcStateAsync(fixture, 277, 0, 250_000).ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 283, 0, 300_000).ConfigureAwait(false);
        var gate = CreateNpcRuntime(277, 0, 125_000, true, true);
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(gate.Definition);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        context.ActiveNpcs.Clear();
        context.ActiveNpcs.Add(gate);

        await context.SaveNpcStatesAsync().ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedState = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single().NpcStates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(savedState.Id, Is.EqualTo(gateStateId));
            Assert.That(savedState.MonsterNumber, Is.EqualTo(277));
            Assert.That(savedState.CurrentHp, Is.EqualTo(125_000));
        });
    }

    /// <summary>
    /// Verifies startup state calculation and an automatic state transition.
    /// </summary>
    [Test]
    public async ValueTask PlugInCalculatesAndAdvancesStateAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object);

        Assert.That(context, Is.Not.Null);
        Assert.That(context!.CurrentState, Is.EqualTo(CastleSiegeState.RegisterGuild));
        Assert.That(context.StateEndTimeUtc, Is.EqualTo(new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc)));

        timeProvider.Advance(TimeSpan.FromHours(12));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        Assert.That(context.CurrentState, Is.EqualTo(CastleSiegeState.Idle2));
    }

    /// <summary>
    /// Verifies that a state transition is reported to the <see cref="ICastleSiegeStateChangedPlugIn"/>.
    /// </summary>
    [Test]
    public async ValueTask StateChangeIsReportedToPlugInAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var plugInManager = new PlugInManager(null, NullLoggerFactory.Instance, null, null);
        var recorder = new StateChangeRecordingPlugIn();
        plugInManager.RegisterPlugInAtPlugInPoint<ICastleSiegeStateChangedPlugIn>(recorder);
        fixture.GameContext.SetupGet(game => game.PlugInManager).Returns(plugInManager);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        timeProvider.Advance(TimeSpan.FromHours(12));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        Assert.That(recorder.Changes, Is.EqualTo(new[] { (CastleSiegeState.RegisterGuild, CastleSiegeState.Idle2) }));
    }

    /// <summary>
    /// Verifies that the administration snapshot includes registrations and can remove one during the registration state.
    /// </summary>
    [Test]
    public async ValueTask AdministrationSnapshotsAndRemovesRegistrationAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var snapshot = await administration.GetSnapshotAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var result = await administration
            .RemoveRegistrationAsync(fixture.GameContext.Object, fixture.RegisteredGuildId)
            .ConfigureAwait(false);
        var updatedSnapshot = await administration.GetSnapshotAsync(fixture.GameContext.Object).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.State, Is.EqualTo(CastleSiegeState.RegisterGuild));
            Assert.That(snapshot.Registrations.Select(registration => registration.GuildId), Does.Contain(fixture.RegisteredGuildId));
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(updatedSnapshot!.Registrations, Is.Empty);
        });
    }

    /// <summary>
    /// Verifies that a registration remains visible and removable after its guild has been deleted.
    /// </summary>
    [Test]
    public async ValueTask AdministrationSnapshotKeepsDeletedGuildRegistrationAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var guildServer = new Mock<IGuildServer>();
        guildServer
            .Setup(server => server.GetPersistentGuildNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));
        fixture.GameContext.As<IGameServerContext>()
            .SetupGet(context => context.GuildServer)
            .Returns(guildServer.Object);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var snapshot = await administration.GetSnapshotAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var result = await administration
            .RemoveRegistrationAsync(fixture.GameContext.Object, fixture.RegisteredGuildId)
            .ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.Registrations, Has.Count.EqualTo(1));
            Assert.That(snapshot.Registrations[0].GuildName, Is.EqualTo("Test"));
            Assert.That(snapshot.Registrations[0].IsGuildDeleted, Is.True);
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(plugIn.GetContext(fixture.GameContext.Object)!.RegisteredGuilds, Is.Empty);
        });
    }

    /// <summary>
    /// Verifies that duplicate runtime NPC keys don't prevent the administration snapshot from being created.
    /// </summary>
    [Test]
    public async ValueTask AdministrationSnapshotToleratesDuplicateNpcRuntimeKeysAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object)!;
        context.ActiveNpcs.Add(CreateNpcRuntime(277, 0, 125_000, true, true));
        context.ActiveNpcs.Add(CreateNpcRuntime(277, 0, 125_000, true, true));

        var snapshot = await administration
            .GetSnapshotAsync(fixture.GameContext.Object)
            .ConfigureAwait(false);

        Assert.That(snapshot, Is.Not.Null);
    }

    /// <summary>
    /// Verifies that an administration state request only affects the selected game context.
    /// </summary>
    [Test]
    public async ValueTask AdministrationStateRequestIsScopedToOneGameContextAsync()
    {
        var firstFixture = await CreateFixtureAsync().ConfigureAwait(false);
        var secondFixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(firstFixture.GameContext.Object).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(secondFixture.GameContext.Object).ConfigureAwait(false);
        var result = administration.ForceState(firstFixture.GameContext.Object, CastleSiegeState.Idle1);
        await plugIn.ExecuteTaskAsync(firstFixture.GameContext.Object).ConfigureAwait(false);
        await plugIn.ExecuteTaskAsync(secondFixture.GameContext.Object).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(plugIn.GetContext(firstFixture.GameContext.Object)!.CurrentState, Is.EqualTo(CastleSiegeState.Idle1));
            Assert.That(plugIn.GetContext(secondFixture.GameContext.Object)!.CurrentState, Is.EqualTo(CastleSiegeState.RegisterGuild));
        });
    }

    /// <summary>
    /// Verifies that a context-specific state request wins over a simultaneous global request without applying both.
    /// </summary>
    [Test]
    public async ValueTask AdministrationStateRequestPreventsSecondTransitionInSameTickAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        plugIn.ForceState(CastleSiegeState.EndCycle);
        var result = administration.ForceState(fixture.GameContext.Object, CastleSiegeState.Idle1);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(plugIn.GetContext(fixture.GameContext.Object)!.CurrentState, Is.EqualTo(CastleSiegeState.Idle1));
            Assert.That(plugIn.GetContext(fixture.GameContext.Object)!.RegisteredGuilds, Is.Not.Empty);
        });
    }

    /// <summary>
    /// Verifies that an administrator can remove a registration while marks are being registered.
    /// </summary>
    [Test]
    public async ValueTask AdministrationRemovesRegistrationDuringMarkRegistrationAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        var administration = new CastleSiegeAdministration(plugIn);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        administration.ForceState(fixture.GameContext.Object, CastleSiegeState.RegisterMark);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var result = await administration
            .RemoveRegistrationAsync(fixture.GameContext.Object, fixture.RegisteredGuildId)
            .ConfigureAwait(false);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(plugIn.GetContext(fixture.GameContext.Object)!.RegisteredGuilds, Is.Empty);
        });
    }

    /// <summary>
    /// Verifies that plug-in initialization restores the early registration period after a restart.
    /// </summary>
    [Test]
    public async ValueTask PlugInRecoversEarlyRegistrationAfterRestartAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object);

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.CurrentState, Is.EqualTo(CastleSiegeState.RegisterGuild));
            Assert.That(context.StateStartTimeUtc, Is.EqualTo(new DateTime(2026, 8, 8, 22, 5, 0, DateTimeKind.Utc)));
            Assert.That(context.StateEndTimeUtc, Is.EqualTo(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc)));
        });
    }

    /// <summary>
    /// Verifies that forcing the event starts the battle for its configured duration.
    /// </summary>
    [Test]
    public async ValueTask ForceStartEntersStartStateAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var utcNow = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(utcNow);
        var plugIn = new CastleSiegePlugIn(timeProvider);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        plugIn.ForceStart();
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object)!;

        Assert.That(context.CurrentState, Is.EqualTo(CastleSiegeState.Start));
        Assert.That(context.IsEventRunning, Is.True);
        Assert.That(context.StateStartTimeUtc, Is.EqualTo(utcNow.UtcDateTime));
        Assert.That(context.StateEndTimeUtc, Is.EqualTo(utcNow.AddHours(2).UtcDateTime));
    }

    /// <summary>
    /// Verifies that the event-running flag is set exclusively for the battle state.
    /// </summary>
    [Test]
    public async ValueTask EventIsRunningOnlyDuringStartStateAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var context = new CastleSiegeContext(fixture.GameContext.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);

        foreach (var state in Enum.GetValues<CastleSiegeState>())
        {
            context.CurrentState = state;
            Assert.That(
                context.IsEventRunning,
                Is.EqualTo(state == CastleSiegeState.Start),
                $"Unexpected event-running value for state {state}.");
        }
    }

    /// <summary>
    /// Verifies that the periodic NPC snapshot is scheduled when the server starts outside the battle phase.
    /// </summary>
    [Test]
    public async ValueTask PeriodicNpcSaveIsScheduledOnStartupAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        await AddNpcStateAsync(fixture, 277, 1, 1_000).ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object)!;
        context.ActiveNpcs.Add(CreateNpcRuntime(277, 1, 500, true, true));
        fixture.CastleSiegeConfiguration.NpcDefinitions.Add(context.ActiveNpcs.Single().Definition);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var savedData = (await persistenceContext.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        Assert.That(savedData.NpcStates.Single(state => state.MonsterNumber == 277).CurrentHp, Is.EqualTo(500));
    }

    /// <summary>
    /// Verifies that EndCycle clears registrations and immediately opens the next registration phase.
    /// </summary>
    [Test]
    public async ValueTask EndCycleTransitionsDirectlyToRegistrationAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 8, 22, 4, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object)!;

        Assert.That(context.CurrentState, Is.EqualTo(CastleSiegeState.RegisterGuild));
        Assert.That(context.StateStartTimeUtc, Is.EqualTo(new DateTime(2026, 8, 8, 22, 5, 0, DateTimeKind.Utc)));
        Assert.That(context.StateEndTimeUtc, Is.EqualTo(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(context.RegisteredGuilds, Is.Empty);

        using var persistenceContext = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeGuildRegistration),
            false,
            fixture.GameConfiguration);
        Assert.That(await persistenceContext.GetAsync<CastleSiegeGuildRegistration>().ConfigureAwait(false), Is.Empty);
    }

    /// <summary>
    /// Verifies that regular notifications are not repeated on every timer tick.
    /// </summary>
    [Test]
    public async ValueTask RegistrationNotificationUsesConfiguredIntervalAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 0, 15, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);

        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        timeProvider.Advance(TimeSpan.FromMinutes(19));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        Assert.That(fixture.Notifications, Has.Count.EqualTo(2));
        Assert.That(fixture.Notifications, Has.All.Contains("registration"));
    }

    /// <summary>
    /// Verifies the Ready-state notification at 30 minutes and every minute during the final five minutes.
    /// </summary>
    [Test]
    public async ValueTask ReadyNotificationsUseCountdownIntervalsAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 8, 19, 29, 0, TimeSpan.Zero));
        var plugIn = new CastleSiegePlugIn(timeProvider);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        timeProvider.Advance(TimeSpan.FromMinutes(24));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        for (var minute = 0; minute < 5; minute++)
        {
            timeProvider.Advance(TimeSpan.FromMinutes(1));
            await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        }

        Assert.That(fixture.Notifications, Has.Count.EqualTo(6));
        Assert.That(fixture.Notifications, Has.All.Contains("starts in"));
    }

    /// <summary>
    /// Verifies that a disabled or missing configuration is handled without creating runtime state.
    /// </summary>
    [Test]
    public async ValueTask MissingConfigurationIsIgnoredAsync()
    {
        var gameConfiguration = new MUnique.OpenMU.Persistence.BasicModel.GameConfiguration();
        var gameContext = new Mock<IGameContext>();
        gameContext.SetupGet(context => context.Configuration).Returns(gameConfiguration);
        var plugIn = new CastleSiegePlugIn();

        await plugIn.ExecuteTaskAsync(gameContext.Object).ConfigureAwait(false);

        Assert.That(plugIn.GetContext(gameContext.Object), Is.Null);
    }

    /// <summary>
    /// Verifies three-second mini-map scheduling and skipping missed intervals instead of sending catch-up bursts.
    /// </summary>
    [Test]
    public async ValueTask MiniMapUpdatesFollowThreeSecondCadenceAsync()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(false);
        var start = new DateTimeOffset(2026, 8, 8, 20, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        var plugIn = new CastleSiegePlugIn(timeProvider);
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        var context = plugIn.GetContext(fixture.GameContext.Object)!;
        Assert.That(context.NextMiniMapUpdateUtc, Is.EqualTo(start.AddSeconds(3).UtcDateTime));

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        Assert.That(context.NextMiniMapUpdateUtc, Is.EqualTo(start.AddSeconds(3).UtcDateTime));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        Assert.That(context.NextMiniMapUpdateUtc, Is.EqualTo(start.AddSeconds(6).UtcDateTime));

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await plugIn.ExecuteTaskAsync(fixture.GameContext.Object).ConfigureAwait(false);
        Assert.That(context.NextMiniMapUpdateUtc, Is.EqualTo(start.AddSeconds(15).UtcDateTime));
    }

    private static DateTime ParseUtc(string utcTime)
    {
        return DateTime.Parse(utcTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
    }

    private static IEnumerable<CastleSiegeStateScheduleEntry> CreateSchedule()
    {
        yield return CreateScheduleEntry(CastleSiegeState.Idle1, DayOfWeek.Sunday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.RegisterGuild, DayOfWeek.Monday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.Idle2, DayOfWeek.Tuesday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.RegisterMark, DayOfWeek.Wednesday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.Idle3, DayOfWeek.Thursday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.Notify, DayOfWeek.Friday, 0, 0);
        yield return CreateScheduleEntry(CastleSiegeState.Ready, DayOfWeek.Saturday, 18, 0);
        yield return CreateScheduleEntry(CastleSiegeState.Start, DayOfWeek.Saturday, 20, 0);
        yield return CreateScheduleEntry(CastleSiegeState.End, DayOfWeek.Saturday, 22, 0);
        yield return CreateScheduleEntry(CastleSiegeState.EndCycle, DayOfWeek.Saturday, 22, 5);
    }

    private static CastleSiegeStateScheduleEntry CreateScheduleEntry(CastleSiegeState state, DayOfWeek dayOfWeek, byte hour, byte minute)
    {
        return new CastleSiegeStateScheduleEntry
        {
            State = state,
            DayOfWeek = dayOfWeek,
            Hour = hour,
            Minute = minute,
        };
    }

    private static CastleSiegeNpcRuntime CreateNpcRuntime(short monsterNumber, byte instanceId, int currentHp, bool isPersisted, bool isAlive)
    {
        return new CastleSiegeNpcRuntime
        {
            Definition = new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeNpcDefinition
            {
                MonsterDefinition = new MUnique.OpenMU.Persistence.BasicModel.MonsterDefinition { Number = monsterNumber },
                InstanceId = instanceId,
                IsPersistedToDatabase = isPersisted,
            },
            PersistedState = new CastleSiegeNpcState
            {
                MonsterNumber = monsterNumber,
                InstanceId = instanceId,
                CurrentHp = currentHp,
            },
            IsAlive = isAlive,
        };
    }

    private static async ValueTask<Guid> AddNpcStateAsync(
        TestFixture fixture,
        short monsterNumber,
        byte instanceId,
        int currentHp)
    {
        using var context = fixture.PersistenceContextProvider.CreateNewTypedContext(
            typeof(CastleSiegeData),
            false,
            fixture.GameConfiguration);
        var siegeData = (await context.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        var npcState = context.CreateNew<CastleSiegeNpcState>();
        npcState.MonsterNumber = monsterNumber;
        npcState.InstanceId = instanceId;
        npcState.CurrentHp = currentHp;
        siegeData.NpcStates.Add(npcState);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return npcState.Id;
    }

    private static async ValueTask<TestFixture> CreateFixtureAsync(bool createSiegeData = true)
    {
        var persistenceContextProvider = new InMemoryPersistenceContextProvider();
        using var context = persistenceContextProvider.CreateNewContext();
        var gameConfiguration = context.CreateNew<MUnique.OpenMU.Persistence.BasicModel.GameConfiguration>();
        var castleSiegeConfiguration = context.CreateNew<MUnique.OpenMU.Persistence.BasicModel.CastleSiegeConfiguration>();
        castleSiegeConfiguration.Enabled = true;
        foreach (var scheduleEntry in CreateSchedule())
        {
            var persistentEntry = context.CreateNew<CastleSiegeStateScheduleEntry>();
            persistentEntry.State = scheduleEntry.State;
            persistentEntry.DayOfWeek = scheduleEntry.DayOfWeek;
            persistentEntry.Hour = scheduleEntry.Hour;
            persistentEntry.Minute = scheduleEntry.Minute;
            castleSiegeConfiguration.StateSchedule.Add(persistentEntry);
        }

        gameConfiguration.CastleSiegeConfiguration = castleSiegeConfiguration;

        if (createSiegeData)
        {
            var siegeData = context.CreateNew<CastleSiegeData>();
            siegeData.TributeMoney = 42;
        }

        var registeredGuildId = Guid.NewGuid();
        var registration = context.CreateNew<CastleSiegeGuildRegistration>();
        registration.GuildId = registeredGuildId;
        registration.GuildName = "Test";
        await context.SaveChangesAsync().ConfigureAwait(false);

        var notifications = new List<string>();
        var gameContext = new Mock<IGameContext>();
        gameContext.SetupGet(game => game.Configuration).Returns(gameConfiguration);
        gameContext.SetupGet(game => game.PersistenceContextProvider).Returns(persistenceContextProvider);
        gameContext.SetupGet(game => game.LoggerFactory).Returns(NullLoggerFactory.Instance);
        gameContext.SetupGet(game => game.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        gameContext
            .Setup(game => game.SendGlobalNotificationAsync(It.IsAny<string>()))
            .Callback<string>(notifications.Add)
            .Returns(ValueTask.CompletedTask);

        return new(
            persistenceContextProvider,
            gameConfiguration,
            castleSiegeConfiguration,
            gameContext,
            registeredGuildId,
            notifications);
    }

    private static async ValueTask<CastleSiegeContext> CreateDetachedContextAsync(TestFixture fixture, DetachedSiegeStore store)
    {
        var game = new Mock<IGameContext>();
        game.SetupGet(value => value.Configuration).Returns(fixture.GameConfiguration);
        game.SetupGet(value => value.PersistenceContextProvider).Returns(store.Provider);
        game.SetupGet(value => value.ServerTimeZone).Returns(TimeZoneInfo.Utc);
        game.Setup(value => value.GetPlayersAsync()).ReturnsAsync(Array.Empty<Player>());
        var context = new CastleSiegeContext(game.Object, fixture.CastleSiegeConfiguration);
        await context.InitializeAsync(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);
        return context;
    }

    // Ordinary InMemory contexts share entity references. This adapter models detached EF reads and atomic saves.
    internal sealed class DetachedSiegeStore
    {
        private readonly IPersistenceContextProvider _inner;

        public DetachedSiegeStore(IPersistenceContextProvider inner)
        {
            this._inner = inner;
            var provider = new Mock<IPersistenceContextProvider>();
            provider.Setup(value => value.CreateNewTypedContext(It.IsAny<Type>(), It.IsAny<bool>(), It.IsAny<GameConfiguration>()))
                .Returns<Type, bool, GameConfiguration>((type, cached, configuration) => type == typeof(CastleSiegeData)
                    ? this.CreateContext()
                    : inner.CreateNewTypedContext(type, cached, configuration));
            this.Provider = provider.Object;
        }

        public IPersistenceContextProvider Provider { get; }

        public int RejectedSaves { get; set; }

        public int WriteReads { get; private set; }

        public int SuccessfulSaves { get; private set; }

        public Func<ValueTask>? BeforeSave { get; set; }

        public async Task<CastleSiegeData> ReadAsync()
        {
            using var context = this._inner.CreateNewContext();
            return (await context.GetAsync<CastleSiegeData>().ConfigureAwait(false)).Single();
        }

        private static MUnique.OpenMU.Persistence.BasicModel.CastleSiegeData Clone(CastleSiegeData source)
        {
            var result = new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeData { Id = source.Id };
            Copy(source, result);
            return result;
        }

        private static void Copy(CastleSiegeData source, CastleSiegeData target)
        {
            target.OwnerGuildId = source.OwnerGuildId;
            target.IsOccupied = source.IsOccupied;
            target.TaxChaos = source.TaxChaos;
            target.TaxStore = source.TaxStore;
            target.TaxHunt = source.TaxHunt;
            target.IsHuntZoneEnabled = source.IsHuntZoneEnabled;
            target.TributeMoney = source.TributeMoney;
            target.NpcStates.Clear();
            foreach (var npc in source.NpcStates)
            {
                target.NpcStates.Add(new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeNpcState
                {
                    Id = npc.Id, MonsterNumber = npc.MonsterNumber, InstanceId = npc.InstanceId,
                    DefenseLevel = npc.DefenseLevel, RegenLevel = npc.RegenLevel, LifeLevel = npc.LifeLevel, CurrentHp = npc.CurrentHp,
                });
            }

            target.Guilds.Clear();
            foreach (var guild in source.Guilds)
            {
                target.Guilds.Add(new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeGuild
                {
                    Id = guild.Id, GuildId = guild.GuildId, GuildName = guild.GuildName,
                    Side = guild.Side, Score = guild.Score, IsAllianceMaster = guild.IsAllianceMaster,
                });
            }
        }

        private IContext CreateContext()
        {
            CastleSiegeData? snapshot = null;
            var context = new Mock<IContext>();
            context.Setup(value => value.CreateNew<CastleSiegeNpcState>(It.IsAny<object?[]>()))
                .Returns(() => new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeNpcState { Id = Guid.NewGuid() });
            context.Setup(value => value.CreateNew<CastleSiegeGuild>(It.IsAny<object?[]>()))
                .Returns(() => new MUnique.OpenMU.Persistence.BasicModel.CastleSiegeGuild { Id = Guid.NewGuid() });
            context.Setup(value => value.GetAsync<CastleSiegeData>(It.IsAny<CancellationToken>()))
                .Returns(async () => new[] { Clone(await this.ReadAsync().ConfigureAwait(false)) });
            context.Setup(value => value.GetByIdAsync<CastleSiegeData>(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    this.WriteReads++;
                    return snapshot = Clone(await this.ReadAsync().ConfigureAwait(false));
                });
            context.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    if (this.BeforeSave is { } beforeSave)
                    {
                        await beforeSave().ConfigureAwait(false);
                    }

                    if (this.RejectedSaves > 0)
                    {
                        this.RejectedSaves--;
                        throw new PersistenceConflictException(new InvalidOperationException("Synthetic noncommitting concurrency rejection."));
                    }

                    Copy(snapshot!, await this.ReadAsync().ConfigureAwait(false));
                    this.SuccessfulSaves++;
                    return true;
                });
            return context.Object;
        }
    }

    private sealed record TestFixture(
        InMemoryPersistenceContextProvider PersistenceContextProvider,
        GameConfiguration GameConfiguration,
        CastleSiegeConfiguration CastleSiegeConfiguration,
        Mock<IGameContext> GameContext,
        Guid RegisteredGuildId,
        List<string> Notifications);

    [Guid("8D96455D-B16B-4E02-91DE-5DF862FB4386")]
    private sealed class StateChangeRecordingPlugIn : ICastleSiegeStateChangedPlugIn
    {
        public List<(CastleSiegeState Previous, CastleSiegeState Current)> Changes { get; } = new();

        public ValueTask CastleSiegeStateChangedAsync(IGameContext gameContext, CastleSiegeContext castleSiege, CastleSiegeState previousState)
        {
            this.Changes.Add((previousState, castleSiege.CurrentState));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            this._utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => this._utcNow;

        public void Advance(TimeSpan duration)
        {
            this._utcNow += duration;
        }
    }
}
