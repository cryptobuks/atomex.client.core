﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Serilog;

using Atomex.Cryptography.Abstract;
using Atomex.Wallet.Abstract;
using WalletAddress = Atomex.Core.WalletAddress;
using DtoWalletAddress = Atomex.Client.V1.Entities.WalletAddress;

namespace Atomex.Common
{
    public static class WalletAddressExtensions
    {
        public static async Task<IEnumerable<DtoWalletAddress>> CreateProofOfPossessionAsync(
            this IEnumerable<WalletAddress> fromWallets,
            DateTime timeStamp,
            IAccount account)
        {
            try
            {
                var result = new List<DtoWalletAddress>();

                foreach (var address in fromWallets)
                {
                    var nonce = Guid.NewGuid().ToString();

                    var data = Encoding.Unicode
                        .GetBytes($"{nonce}{timeStamp.ToUniversalTime():yyyy.MM.dd HH:mm:ss.fff}");

                    var hashToSign = HashAlgorithm.Sha256.Hash(data);

                    var currencyConfig = account.Currencies
                        .GetByName(address.Currency);

                    var signature = await account.Wallet
                        .SignAsync(hashToSign, address, currencyConfig)
                        .ConfigureAwait(false);

                    if (signature == null)
                        throw new Exception("Error during creation of proof of possession. Sign is null");

                    var proofOfPossession = Convert.ToBase64String(signature);

                    Log.Verbose("ProofOfPossession: {@signature}", proofOfPossession);

                    using var securePublicKey = account.Wallet.GetPublicKey(
                        currency: currencyConfig,
                        address.KeyIndex,
                        address.KeyType);

                    result.Add(new DtoWalletAddress
                    {
                        Address           = address.Address,
                        Currency          = address.Currency,
                        Nonce             = nonce,
                        ProofOfPossession = proofOfPossession,
                        PublicKey         = Convert.ToBase64String(securePublicKey.ToUnsecuredBytes())
                    });
                }

                return result;
            }
            catch (Exception e)
            {
                Log.Error(e, "Proof of possession creating error");
            }

            return null;
        }
    }
}