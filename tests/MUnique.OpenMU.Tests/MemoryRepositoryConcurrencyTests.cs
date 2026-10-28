// <copyright file="MemoryRepositoryConcurrencyTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Concurrent event contexts share the same in-memory repositories in demo mode.
/// </summary>
[TestFixture]
public class MemoryRepositoryConcurrencyTests
{
    /// <summary>
    /// Adding an object while another context saves must not invalidate the pending-id enumeration.
    /// </summary>
    [Test]
    public async Task AddingDuringSavePreservesBothObjectsAsync()
    {
        var repository = new MemoryRepository<TestIdentity>();
        using var readingId = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        var first = new TestIdentity { Id = Guid.NewGuid() };
        var second = new TestIdentity { Id = Guid.NewGuid() };
        var firstId = first.Id;
        var secondId = second.Id;
        repository.Add(firstId, first);
        first.OnRead = () =>
        {
            readingId.Set();
            if (!releaseRead.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the pending save.");
            }
        };

        var saving = Task.Run(repository.OnSaveChanges);
        var addingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? adding = null;
        try
        {
            Assert.That(readingId.Wait(TimeSpan.FromSeconds(5)), Is.True);
            adding = Task.Run(() =>
            {
                addingStarted.SetResult();
                repository.Add(secondId, second);
            });
            await addingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAny(adding, Task.Delay(100)).ConfigureAwait(false);
        }
        finally
        {
            releaseRead.Set();
            await Task.WhenAll(saving, adding ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        repository.OnSaveChanges();
        Assert.That(await repository.GetAllAsync().ConfigureAwait(false), Is.EquivalentTo(new[] { first, second }));
    }

    /// <summary>
    /// A caller can enumerate its query result while another event adds an object.
    /// </summary>
    [Test]
    public async Task QueryResultRemainsEnumerableAfterAnotherContextAddsAsync()
    {
        var repository = new MemoryRepository<TestIdentity>();
        var first = new TestIdentity { Id = Guid.NewGuid() };
        repository.Add(first.Id, first);
        var result = await repository.GetAllAsync().ConfigureAwait(false);
        using var iterator = result.GetEnumerator();
        Assert.That(iterator.MoveNext(), Is.True);
        var second = new TestIdentity { Id = Guid.NewGuid() };
        repository.Add(second.Id, second);

        Assert.That(iterator.MoveNext(), Is.False);
        Assert.That((await repository.GetAllAsync().ConfigureAwait(false)).Count(), Is.EqualTo(2));
    }

    private sealed class TestIdentity : IIdentifiable
    {
        private Guid _id;

        public Action? OnRead { get; set; }

        public Guid Id
        {
            get
            {
                var onRead = this.OnRead;
                this.OnRead = null;
                onRead?.Invoke();
                return this._id;
            }

            set => this._id = value;
        }
    }
}
