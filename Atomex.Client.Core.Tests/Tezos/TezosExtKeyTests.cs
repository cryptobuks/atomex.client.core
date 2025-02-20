﻿using System.Text;

using NBitcoin;
using Xunit;

using Atomex.Common.Memory;
using Atomex.Wallets.Keys;

namespace Atomex.Client.Core.Tests
{
    public class TezosExtKeyTests
    {
        private const string Message = "I LOVE TEZOS";

        private const string Mnemonic =
            "return auction present awesome blast excess receive obtain explain spider iron hip curtain recipe tent aim bonus hip cliff shrug lyrics pass right spend";

        [Fact]
        public void TrustWalletTezosExtKeyTest()
        {
            var messageBytes = Encoding.UTF8.GetBytes(Message);

            using var seed = new SecureBytes(new Mnemonic(Mnemonic).DeriveSeed());
            using var extKey = new Ed25519ExtKey(seed);
            using var childKey = extKey.Derive("m/44'/1729'/0'/0'");
            using var secureChildPublicKey = childKey.GetPublicKey();
            var childPublicKey = secureChildPublicKey.ToUnsecuredBytes();

            var signature = childKey.Sign(messageBytes);
            Assert.True(childKey.Verify(messageBytes, signature));

            var address = Common.CurrenciesTestNet.Get<TezosConfig>("XTZ").AddressFromKey(childPublicKey);
            Assert.NotNull(address);
        }

        [Fact]
        public void TezosExtKeyTest()
        {
            var messageBytes = Encoding.UTF8.GetBytes(Message);

            using var seed = new SecureBytes(new Mnemonic(Mnemonic).DeriveSeed());
            using var extKey = new Bip32Ed25519ExtKey(seed);
            using var childKey = extKey.Derive("m/44'/1729'/0'/0'");
            using var secureChildPublicKey = childKey.GetPublicKey();
            var childPublicKey = secureChildPublicKey.ToUnsecuredBytes();

            var signature = childKey.Sign(messageBytes);
            Assert.True(childKey.Verify(messageBytes, signature));

            var address = Common.CurrenciesTestNet.Get<TezosConfig>("XTZ").AddressFromKey(childPublicKey);
            Assert.NotNull(address);
        }

        [Fact]
        public void TezosExtKeyDerivationTest()
        {
            var messageBytes = Encoding.UTF8.GetBytes(Message);

            using var seed = new SecureBytes(new Mnemonic(Mnemonic).DeriveSeed());
            using var extKey = new Bip32Ed25519ExtKey(seed);

            for (var i = 0; i < 100; ++i)
            {
                using var childKey = extKey.Derive($"m/44'/1729'/0'/0/{i}");
                using var secureChildPublicKey = childKey.GetPublicKey();
                var childPublicKey = secureChildPublicKey.ToUnsecuredBytes();

                var signature = childKey.Sign(messageBytes);
                Assert.True(childKey.Verify(messageBytes, signature));

                var address = Common.CurrenciesTestNet.Get<TezosConfig>("XTZ").AddressFromKey(childPublicKey);
                Assert.NotNull(address);
            }
        }
    }
}