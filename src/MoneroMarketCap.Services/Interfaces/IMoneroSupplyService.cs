using System;
using System.Collections.Generic;
using System.Text;

namespace MoneroMarketCap.Services.Interfaces
{

    public interface IMoneroSupplyService
    {
         Task<(ulong height, decimal supply)?> GetHeightAndSupplyAsync(
         CancellationToken ct);

    }

    public readonly record struct MoneroSupplyDelta(
    ulong FromHeight,
    ulong ToHeight,
    ulong EmissionLow64,
    ulong EmissionHigh64);
}
