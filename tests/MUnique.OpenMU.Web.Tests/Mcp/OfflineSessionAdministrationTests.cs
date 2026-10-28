// <copyright file="OfflineSessionAdministrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Web.Tests.AdminAuth;

/// <summary>Verifies offline-session safety through authenticated MCP rather than the login snapshot alone.</summary>
[TestFixture]
public sealed class OfflineSessionAdministrationTests
{
    private McpIntegrationTests _host = null!;
    private McpClient _client = null!;
    private Mock<IGameServer> _server = null!;
    private Mock<IGameServerAdministration> _administration = null!;
    private Account _account = null!;
    private GameConfiguration _configuration = null!;
    private InMemoryAdminAuditRepository _audit = null!;
    private bool _offlineSession;
    private bool _reserved;

    /// <summary>Creates a persisted account and a remote-style game server absent from the login snapshot.</summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        this._host = new McpIntegrationTests { EnableWrites = true };
        await this._host.SetUpAsync().ConfigureAwait(false);
        this._client = await this._host.ConnectAsync("omu_mcp-admin-test").ConfigureAwait(false);
        var services = this._host.App.Services;
        this._audit = (InMemoryAdminAuditRepository)services.GetRequiredService<IAdminAuditRepository>();
        this._configuration = await services.GetRequiredService<IDataSource<GameConfiguration>>().GetOwnerAsync().ConfigureAwait(false);
        this._configuration.MaximumInventoryMoney = 100000;
        using var context = services.GetRequiredService<IPersistenceContextProvider>().CreateNewPlayerContext(this._configuration);
        this._account = (await context.GetAccountByLoginNameAsync("tester").ConfigureAwait(false))!;
        this._account.Characters.Single().Inventory = context.CreateNew<ItemStorage>();
        this._account.Characters.Single().Inventory!.Money = 1000;
        this._account.Vault = context.CreateNew<ItemStorage>();
        var item = this._configuration.Items.Single();
        item.Width = 1;
        item.Height = 1;
        item.MaximumItemLevel = 15;
        item.Durability = 20;
        await context.SaveChangesAsync().ConfigureAwait(false);
        var login = Mock.Get(services.GetRequiredService<ILoginServer>());
        login.Setup(l => l.GetSnapshotAsync()).ReturnsAsync(new Dictionary<string, byte>());
        this._offlineSession = false;
        this._reserved = false;
        login.Setup(l => l.TryReserveAccountAsync("tester", It.IsAny<int>())).Returns(() =>
        {
            this._reserved = true;
            return ValueTask.FromResult(true);
        });
        login.Setup(l => l.ReleaseAccountReservationAsync("tester", It.IsAny<int>())).Returns(() =>
        {
            this._reserved = false;
            return ValueTask.CompletedTask;
        });
        this._server = new Mock<IGameServer>();
        this._server.SetupGet(s => s.Id).Returns(7);
        this._server.SetupGet(s => s.ServerState).Returns(ServerState.Started);
        this._administration = this._server.As<IGameServerAdministration>();
        this._administration.Setup(s => s.HasOfflineSessionAsync("tester", It.IsAny<CancellationToken>())).Returns(() =>
        {
            Assert.That(this._reserved, Is.True, "Check offline ownership only after the login reservation.");
            return ValueTask.FromResult(this._offlineSession);
        });
        Mock.Get(services.GetRequiredService<IServerProvider>()).SetupGet(s => s.Servers).Returns([this._server.Object]);
    }

    /// <summary>Stops the HTTP client and host.</summary>
    [TearDown]
    public async Task TearDownAsync()
    {
        await this._client.DisposeAsync().ConfigureAwait(false);
        await this._host.TearDownAsync().ConfigureAwait(false);
    }

    /// <summary>An offline session blocks previews and also invalidates a previously authorized edit.</summary>
    /// <param name="addItem">Whether to add an item instead of editing money.</param>
    /// <param name="apply">Whether the session starts after a successful preview.</param>
    /// <param name="vault">Whether an item targets the account vault.</param>
    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    public async Task OfflineSessionRejectsCharacterChangesAsync(bool addItem, bool apply, bool vault)
    {
        var tool = addItem ? "characters_add_item" : "characters_update";
        var arguments = new Dictionary<string, object?> { ["characterName"] = "Hero" };
        if (addItem)
        {
            arguments["itemId"] = this._configuration.Items.Single().GetId();
            arguments["vault"] = vault;
        }
        else
        {
            arguments["patch"] = "{\"money\":123}";
        }

        if (apply)
        {
            var preview = await this._client.CallToolAsync(tool, arguments).ConfigureAwait(false);
            Assert.That(preview.IsError, Is.Not.True, JsonSerializer.Serialize(preview));
            arguments["apply"] = true;
            arguments["confirmationToken"] = JsonSerializer.SerializeToNode(preview.StructuredContent)!["confirmationToken"]!.GetValue<string>();
        }

        this._offlineSession = true;
        var result = await this._client.CallToolAsync(tool, arguments).ConfigureAwait(false);
        var audit = await this._audit.ReadAsync(0, 100).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, JsonSerializer.Serialize(result));
            Assert.That(this._account.Characters.Single().Inventory!.Money, Is.EqualTo(1000));
            Assert.That(this._account.Characters.Single().Inventory!.Items, Is.Empty);
            Assert.That(this._account.Vault!.Items, Is.Empty);
            Assert.That(audit.Items.Any(e => e.Outcome == "Succeeded"), Is.False);
            Assert.That(this._reserved, Is.False);
        });
    }

    /// <summary>State and password writes refuse a session even when the login snapshot is empty.</summary>
    /// <param name="password">Whether to reset the password instead of banning the account.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task OfflineSessionRejectsAccountChangesAsync(bool password)
    {
        var originalState = this._account.State;
        var originalHash = this._account.PasswordHash;
        var arguments = new Dictionary<string, object?> { ["loginName"] = "tester", ["reason"] = "Regression test" };
        arguments[password ? "password" : "state"] = password ? "test-new-password" : "Banned";
        this._offlineSession = true;
        var result = await this._client.CallToolAsync(password ? "accounts_reset_password" : "accounts_set_state", arguments).ConfigureAwait(false);
        var audit = await this._audit.ReadAsync(0, 100).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, JsonSerializer.Serialize(result));
            Assert.That(this._account.State, Is.EqualTo(originalState));
            Assert.That(this._account.PasswordHash, Is.EqualTo(originalHash));
            Assert.That(audit.Items.Any(e => e.Outcome == "Succeeded"), Is.False);
            Assert.That(this._reserved, Is.False);
        });
    }

    /// <summary>Disconnect reaches a game server whose account is absent from the normal login snapshot.</summary>
    [Test]
    public async Task DisconnectStopsSessionMissingFromLoginSnapshotAsync()
    {
        this._offlineSession = true;
        this._server.Setup(s => s.DisconnectAccountAsync("tester")).ReturnsAsync(true);
        var result = await this._client.CallToolAsync("players_disconnect", new Dictionary<string, object?> { ["loginName"] = "tester" }).ConfigureAwait(false);
        Assert.That(result.IsError, Is.Not.True, JsonSerializer.Serialize(result));
        Assert.That(JsonSerializer.SerializeToNode(result.StructuredContent)!["disconnected"]!.GetValue<bool>(), Is.True);
        Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Single().ResultJson, Does.Contain("true"));
    }

    /// <summary>An unavailable remote owner cannot authorize an edit, and transport details remain private.</summary>
    [Test]
    public async Task OfflineQueryFailureRejectsEditAndReleasesReservationAsync()
    {
        this._administration.Setup(s => s.HasOfflineSessionAsync("tester", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private-transport-detail"));
        var result = await this._client.CallToolAsync("characters_update", new Dictionary<string, object?> { ["characterName"] = "Hero", ["patch"] = "{\"money\":123}" }).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(JsonSerializer.Serialize(result), Does.Contain("Unavailable").And.Not.Contain("private-transport-detail"));
            Assert.That(this._account.Characters.Single().Inventory!.Money, Is.EqualTo(1000));
            Assert.That(this._reserved, Is.False);
        });
        Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Any(e => e.Outcome == "Succeeded"), Is.False);
    }

    /// <summary>A server in transition or with unknown health cannot establish that the account is unused.</summary>
    /// <param name="state">The uncertain game server state.</param>
    [TestCase(ServerState.Starting)]
    [TestCase(ServerState.Stopping)]
    [TestCase(ServerState.Timeout)]
    public async Task UnknownServerStateRejectsAccountWriteAsync(ServerState state)
    {
        this._server.SetupGet(s => s.ServerState).Returns(state);
        var original = this._account.State;
        var result = await this._client.CallToolAsync("accounts_set_state", new Dictionary<string, object?> { ["loginName"] = "tester", ["state"] = "Banned", ["reason"] = "Safety regression" }).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(JsonSerializer.Serialize(result), Does.Contain("Unavailable"));
            Assert.That(this._account.State, Is.EqualTo(original));
            Assert.That(this._reserved, Is.False);
        });
    }

    /// <summary>A stopped server has no active data owner and must not make a network query.</summary>
    [Test]
    public async Task StoppedServerDoesNotBlockAccountWriteAsync()
    {
        this._server.SetupGet(s => s.ServerState).Returns(ServerState.Stopped);
        this._administration.Setup(s => s.HasOfflineSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("must not call"));
        var result = await this._client.CallToolAsync("accounts_set_state", new Dictionary<string, object?> { ["loginName"] = "tester", ["state"] = "Banned", ["reason"] = "Stopped server regression" }).ConfigureAwait(false);
        Assert.That(result.IsError, Is.Not.True, JsonSerializer.Serialize(result));
        Assert.That(this._account.State, Is.EqualTo(AccountState.Banned));
        Assert.That(this._reserved, Is.False);
    }

    /// <summary>A reservation rejected by the login coordinator prevents both ownership queries and data writes.</summary>
    [Test]
    public async Task RejectedReservationPreventsAccountWriteAsync()
    {
        Mock.Get(this._host.App.Services.GetRequiredService<ILoginServer>()).Setup(l => l.TryReserveAccountAsync("tester", It.IsAny<int>())).ReturnsAsync(false);
        this._administration.Setup(s => s.HasOfflineSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("query ran before reservation"));
        var original = this._account.PasswordHash;
        var result = await this._client.CallToolAsync("accounts_reset_password", new Dictionary<string, object?> { ["loginName"] = "tester", ["password"] = "test-new-password", ["reason"] = "Reservation regression" }).ConfigureAwait(false);
        Assert.That(result.IsError, Is.True);
        Assert.That(JsonSerializer.Serialize(result), Does.Contain("online, reserved").And.Not.Contain("query ran before"));
        Assert.That(this._account.PasswordHash, Is.EqualTo(original));
    }

    /// <summary>A missing account keeps its input-error contract even when the login coordinator rejects reservation.</summary>
    /// <param name="password">Whether to reset a password instead of setting state.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingAccountIsInvalidInputBeforeReservationAsync(bool password)
    {
        Mock.Get(this._host.App.Services.GetRequiredService<ILoginServer>()).Setup(l => l.TryReserveAccountAsync("missing", It.IsAny<int>())).ReturnsAsync(false);
        var arguments = new Dictionary<string, object?> { ["loginName"] = "missing", ["reason"] = "Missing account regression" };
        arguments[password ? "password" : "state"] = password ? "test-new-password" : "Banned";
        var result = await this._client.CallToolAsync(password ? "accounts_reset_password" : "accounts_set_state", arguments).ConfigureAwait(false);
        Assert.That(result.IsError, Is.True);
        Assert.That(JsonSerializer.Serialize(result), Does.Contain("InvalidInput").And.Contain("Account not found"));
        Mock.Get(this._host.App.Services.GetRequiredService<ILoginServer>()).Verify(l => l.TryReserveAccountAsync("missing", It.IsAny<int>()), Times.Never);
        Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Any(e => e.Outcome == "Succeeded"), Is.False);
    }

    /// <summary>The operation remains pending until the owning server confirms its stop completed.</summary>
    [Test]
    public async Task DisconnectWaitsForStopAndAuditCompletionAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._server.Setup(s => s.DisconnectAccountAsync("tester")).Returns(async () =>
        {
            started.SetResult();
            return await stopped.Task.ConfigureAwait(false);
        });
        var call = this._client.CallToolAsync("players_disconnect", new Dictionary<string, object?> { ["loginName"] = "tester" }).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.That(call.IsCompleted, Is.False);
            Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Single().Outcome, Is.EqualTo("Pending"));
        }
        finally
        {
            stopped.TrySetResult(true);
        }

        var result = await call.ConfigureAwait(false);
        Assert.That(JsonSerializer.SerializeToNode(result.StructuredContent)!["disconnected"]!.GetValue<bool>(), Is.True);
        Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Single().Outcome, Is.EqualTo("Succeeded"));
    }

    /// <summary>A failed stop response is uncertain rather than proof that no disconnect happened.</summary>
    [Test]
    public async Task LostDisconnectResponseRemainsUncertainAsync()
    {
        this._server.Setup(s => s.DisconnectAccountAsync("tester")).ThrowsAsync(new InvalidOperationException("private-stop-response"));
        var result = await this._client.CallToolAsync("players_disconnect", new Dictionary<string, object?> { ["loginName"] = "tester" }).ConfigureAwait(false);
        Assert.That(result.IsError, Is.True);
        Assert.That(JsonSerializer.Serialize(result), Does.Contain("Uncertain").And.Not.Contain("private-stop-response"));
        Assert.That((await this._audit.ReadAsync(0, 100).ConfigureAwait(false)).Items.Single().Outcome, Is.EqualTo("Pending"));
    }
}
