﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Atomex.Blockchain.Tezos;
using Atomex.Core;
using Serilog;

namespace Atomex.Wallet.Tezos
{
    public partial class TezosRevealChecker
    {
        private readonly Network _network;
        private readonly IDictionary<string, TezosAddressInfo> _addresses;

        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(60);

        public TezosRevealChecker(Network network)
        {
            _network = network;
            _addresses = new Dictionary<string, TezosAddressInfo>();
        }

        public async Task<bool> IsRevealedAsync(
            string address,
            CancellationToken cancellationToken)
        {
            lock (_addresses)
            {
                if (_addresses.TryGetValue(address, out var info))
                {
                    if (info.LastCheckTimeUtc + UpdateInterval > DateTime.UtcNow)
                        return info.IsRevealed;
                }
            }

            var isRevealedResult = await new TzktApi(_network)
                .IsRevealedAsync(address, cancellationToken)
                .ConfigureAwait(false);

            if (isRevealedResult == null)
            {
                Log.Error("Connection error while checking reveal status for address {@address}", address);

                return false;
            }

            if (isRevealedResult.HasError && isRevealedResult.Error.Code != (int)HttpStatusCode.NotFound)
            {
                Log.Error("Error while checking reveal status for address {@address}. Code: {@code}. Description: {@desc}",
                    address,
                    isRevealedResult.Error.Code,
                    isRevealedResult.Error.Description);

                return false;
            }

            lock (_addresses)
            {
                if (_addresses.TryGetValue(address, out var info))
                {
                    info.Address = address;
                    info.IsRevealed = isRevealedResult.Value;
                    info.LastCheckTimeUtc = DateTime.UtcNow;
                }
                else
                {
                    _addresses.Add(address, new TezosAddressInfo()
                    {
                        Address = address,
                        IsRevealed = isRevealedResult.Value,
                        LastCheckTimeUtc = DateTime.UtcNow
                    });
                }
            }

            return isRevealedResult.Value;
        }
    }
}