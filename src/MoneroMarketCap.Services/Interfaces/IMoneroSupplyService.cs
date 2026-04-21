using System;
using System.Collections.Generic;
using System.Text;

namespace MoneroMarketCap.Services.Interfaces
{

    public interface IMoneroSupplyService
    {
        Task<(ulong Height, decimal SupplyXmr)> GetHeightAndSupplyAsync(CancellationToken ct);

    }

    public readonly record struct MoneroSupplyDelta(
    ulong FromHeight,
    ulong ToHeight,
    ulong EmissionLow64,
    ulong EmissionHigh64);
}
