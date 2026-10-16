using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Tallyard.Api.Infrastructure;

namespace Tallyard.Api.Modules.Inventory;

public interface IStockNotifier
{
    Task BalancesChangedAsync(Guid tenantId, IReadOnlyList<BalanceChanged> changes, CancellationToken ct);
}

/// <summary>Live stock updates. Each connection joins its tenant's group, so a tenant only ever
/// receives its own changes; the browser drops warehouses it can't see.</summary>
[Authorize]
public sealed class StockHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenant = Context.User!.FindFirst(CurrentUser.Claims.Tenant)!.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(tenant));
        await base.OnConnectedAsync();
    }

    public static string Group(string tenantId) => "tenant:" + tenantId;
}

/// <summary>Only quantities go over the wire, never costs.</summary>
public sealed class SignalRStockNotifier(IHubContext<StockHub> hub) : IStockNotifier
{
    public Task BalancesChangedAsync(Guid tenantId, IReadOnlyList<BalanceChanged> changes, CancellationToken ct) =>
        hub.Clients.Group(StockHub.Group(tenantId.ToString())).SendAsync("balances", changes, ct);
}
