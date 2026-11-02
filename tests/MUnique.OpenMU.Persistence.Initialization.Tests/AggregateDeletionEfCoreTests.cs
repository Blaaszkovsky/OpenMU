// <copyright file="AggregateDeletionEfCoreTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.Json;
using MUnique.OpenMU.PlugIns;
using Npgsql;
using ItemStorageEntity = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemStorage;

/// <summary>
/// Tests the deletion of an aggregate with the entity framework core, which requires a running postgres database.
/// </summary>
/// <remarks>
/// The interesting part is what the database is left with, so this can't be tested without one.
/// </remarks>
[TestFixture]
[NonParallelizable]
internal class AggregateDeletionEfCoreTests
{
    private readonly string _databaseName = "openmu_aggregate_deletion_" + Guid.NewGuid().ToString("N");
    private IDisposable? _connectionSettings;
    private PersistenceContextProvider _contextProvider = null!;

    /// <summary>Creates only an opt-in, uniquely named test database.</summary>
    [OneTimeSetUp]
    public async Task CreateDatabaseAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("OPENMU_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("Set OPENMU_TEST_POSTGRES to run the PostgreSQL aggregate deletion test.");
        }

        var server = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseConnection = $"Server={server.Host};Port={server.Port};User Id={server.Username};Password={server.Password};Database={this._databaseName};Command Timeout=120;";
        this._connectionSettings = ConnectionConfigurator.UseTemporarily(new OneDatabaseSettingProvider(databaseConnection));
        if (!JsonConverterRegistry.Converters.OfType<LocalizedStringJsonConverter>().Any())
        {
            JsonConverterRegistry.RegisterConverter(new LocalizedStringJsonConverter());
        }

        if (!JsonConverterRegistry.Converters.OfType<BinaryAsHexJsonConverter>().Any())
        {
            JsonConverterRegistry.RegisterConverter(new BinaryAsHexJsonConverter());
        }

        this._contextProvider = new PersistenceContextProvider(new NullLoggerFactory(), null);
        await using (var context = new EntityDataContext())
        {
            Assert.That(new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Database, Is.EqualTo(this._databaseName));
        }

        await this._contextProvider.ApplyAllPendingUpdatesAsync().ConfigureAwait(false);
        await new VersionSeasonSix.DataInitialization(this._contextProvider, new NullLoggerFactory())
            .CreateInitialDataAsync(1, false).ConfigureAwait(false);
    }

    /// <summary>Drops only this fixture's database and restores the previous connection provider.</summary>
    [OneTimeTearDown]
    public async Task DropDatabaseAsync()
    {
        if (this._connectionSettings is null)
        {
            return;
        }

        try
        {
            await using var context = new EntityDataContext();
            Assert.That(new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString()).Database, Is.EqualTo(this._databaseName));
            await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
        finally
        {
            this._connectionSettings.Dispose();
        }
    }

    /// <summary>
    /// Deletes an account which owns a vault and a character with an inventory, and checks that their
    /// storages are removed while existing stores remain. The inventory used to leak: it's referenced BY
    /// the character, so no delete cascade of the database reaches it, and the traversal of the
    /// aggregate didn't go deeper than the account's own members.
    /// </summary>
    [Test]
    public async Task DeletingAnAccountDeletesTheItemStoragesOfItsCharactersAsync()
    {
        var contextProvider = this._contextProvider;
        GameConfiguration configuration;
        using (var context = contextProvider.CreateNewContext())
        {
            configuration = (await context.GetAsync<GameConfiguration>().ConfigureAwait(false)).Single();
        }

        var initialStorageIds = await GetItemStorageIdsAsync().ConfigureAwait(false);
        using (var context = contextProvider.CreateNewPlayerContext(configuration))
        {
            var account = context.CreateNew<Account>();
            account.LoginName = "deleteme";
            account.PasswordHash = "hash";
            account.Vault = context.CreateNew<ItemStorage>();

            var characterClass = configuration.CharacterClasses.First(c => c is { CanGetCreated: true, HomeMap: not null });
            var character = context.CreateNew<Character>();
            character.Name = "DeleteMe";
            character.CharacterClass = characterClass;
            character.CurrentMap = characterClass.HomeMap;
            character.CreateDate = DateTime.UtcNow;
            character.KeyConfiguration = new byte[30];
            character.Inventory = context.CreateNew<ItemStorage>();
            account.Characters.Add(character);

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        Assert.That(await GetItemStorageIdsAsync().ConfigureAwait(false), Has.Length.EqualTo(initialStorageIds.Length + 2), "The vault and the inventory should have been created.");

        using (var context = contextProvider.CreateNewPlayerContext(configuration))
        {
            var account = await context.GetAccountByLoginNameAsync("deleteme").ConfigureAwait(false);
            Assert.That(account, Is.Not.Null);
            await context.DeleteAsync(account!).ConfigureAwait(false);

            // Must not throw: nothing may delete a row which the context has marked as deleted itself.
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        Assert.That(await GetItemStorageIdsAsync().ConfigureAwait(false), Is.EquivalentTo(initialStorageIds));
    }

    private static async ValueTask<Guid[]> GetItemStorageIdsAsync()
    {
        await using var context = new EntityDataContext();
        return await context.Set<ItemStorageEntity>().Select(storage => storage.Id).ToArrayAsync().ConfigureAwait(false);
    }

    private sealed class OneDatabaseSettingProvider : IDatabaseConnectionSettingProvider
    {
        private readonly string _connectionString;

        public OneDatabaseSettingProvider(string connectionString) => this._connectionString = connectionString;

        public Task? Initialization => null;

        public Task InitializeAsync(CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext
            => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType)
            => new() { ContextTypeName = contextType.FullName, ConnectionString = this._connectionString, DatabaseEngine = DatabaseEngine.Npgsql };
    }
}
