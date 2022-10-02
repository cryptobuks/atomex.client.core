﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using Serilog;

using Atomex.Abstract;
using Atomex.Blockchain.Abstract;
using Atomex.Blockchain.Tezos;
using Atomex.Common;
using Atomex.Core;
using Atomex.Swaps.Abstract;
using Atomex.Swaps.Helpers;
using Atomex.Swaps.Tezos.Fa12.Helpers;
using Atomex.TezosTokens;
using Atomex.Wallet.Tezos;

namespace Atomex.Swaps.Tezos.Fa12
{
    public class Fa12Swap : CurrencySwap
    {
        private Fa12Account Fa12Account { get; }
        private TezosAccount TezosAccount { get; }
        private Fa12Config Fa12Config => Currencies.Get<Fa12Config>(Currency);
        private TezosConfig XtzConfig => Currencies.Get<TezosConfig>(TezosAccount.Currency);
        public static TimeSpan InitiationTimeout = TimeSpan.FromMinutes(10);
        public static TimeSpan InitiationCheckInterval = TimeSpan.FromSeconds(15);

        public Fa12Swap(
            Fa12Account account,
            TezosAccount tezosAccount,
            ICurrencies currencies)
            : base(account.Currency, currencies)
        {
            Fa12Account = account ?? throw new ArgumentNullException(nameof(account));
            TezosAccount = tezosAccount ?? throw new ArgumentNullException(nameof(account));
        }

        public override async Task PayAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            if (!await CheckPayRelevanceAsync(swap, cancellationToken))
                return;

            var lockTimeInSeconds = swap.IsInitiator
                ? DefaultInitiatorLockTimeInSeconds
                : DefaultAcceptorLockTimeInSeconds;

            var paymentTx = await CreatePaymentTxAsync(swap, lockTimeInSeconds, cancellationToken)
                .ConfigureAwait(false);

            if (paymentTx == null)
            {
                Log.Error("Can't create payment transaction");
                return;
            }

            try
            {
                try
                {
                    await TezosAccount.AddressLocker
                        .LockAsync(paymentTx.From, cancellationToken)
                        .ConfigureAwait(false);

                    // create token approve transactions
                    var txsToBroadcast = await CreateApproveTxsAsync(swap, paymentTx, cancellationToken)
                        .ConfigureAwait(false) ?? throw new Exception($"Can't get allowance for {paymentTx.From}");

                    txsToBroadcast.Add(paymentTx);

                    foreach (var tx in txsToBroadcast)
                    {
                        var isInitiateTx = tx.Type.HasFlag(BlockchainTransactionType.SwapPayment);

                        var isAlreadyRevealed = false;

                        var address = await TezosAccount
                            .GetAddressAsync(tx.From, cancellationToken)
                            .ConfigureAwait(false);

                        using var securePublicKey = TezosAccount.Wallet
                            .GetPublicKey(XtzConfig, address.KeyIndex, address.KeyType);

                        // temporary fix: check operation sequence
                        await TezosOperationsSequencer
                            .WaitAsync(tx.From, TezosAccount, cancellationToken)
                            .ConfigureAwait(false);

                        // fill operation
                        var (fillResult, isRunSuccess, hasReveal) = await tx
                            .FillOperationsAsync(
                                securePublicKey: securePublicKey,
                                tezosConfig: TezosAccount.Config,
                                headOffset: TezosConfig.HeadOffset,
                                isAlreadyRevealed: isAlreadyRevealed,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        isAlreadyRevealed = hasReveal;

                        var signResult = await SignTransactionAsync(tx, cancellationToken)
                            .ConfigureAwait(false);

                        if (!signResult)
                        {
                            Log.Error("Transaction signing error.");
                            return;
                        }

                        if (isInitiateTx)
                        {
                            swap.PaymentTx = tx;
                            swap.StateFlags |= SwapStateFlags.IsPaymentSigned;

                            await UpdateSwapAsync(swap, SwapStateFlags.IsPaymentSigned, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await BroadcastTxAsync(
                                swap: swap,
                                tx: tx,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                    throw;
                }
                finally
                {
                    TezosAccount.AddressLocker.Unlock(paymentTx.From);
                }

                swap.PaymentTx = paymentTx;
                swap.StateFlags |= SwapStateFlags.IsPaymentBroadcast;

                await UpdateSwapAsync(swap, SwapStateFlags.IsPaymentBroadcast, cancellationToken)
                    .ConfigureAwait(false);

                var isInitiateConfirmed = await WaitPaymentConfirmationAsync(
                        txId: paymentTx.Id,
                        timeout: Fa12Swap.InitiationTimeout,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                    
                if (!isInitiateConfirmed)
                {
                    Log.Error("Initiation payment tx not confirmed after timeout {@timeout}", InitiationTimeout.Minutes);
                    return;
                }
                
                swap.StateFlags |= SwapStateFlags.IsPaymentConfirmed;
                await UpdateSwapAsync(swap, SwapStateFlags.IsPaymentConfirmed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Swap payment error for swap {@swapId}", swap.Id);
            }
        }

        public override Task StartPartyPaymentControlAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Start party {@currency} payment control for swap {@swap}", Currency, swap.Id);

            // initiator waits "accepted" event, acceptor waits "initiated" event
            var initiatedHandler = swap.IsInitiator
                ? new Func<Swap, CancellationToken, Task>(SwapAcceptedHandler)
                : new Func<Swap, CancellationToken, Task>(SwapInitiatedHandler);

            var lockTimeSeconds = swap.IsInitiator
                ? DefaultAcceptorLockTimeInSeconds
                : DefaultInitiatorLockTimeInSeconds;

            var refundTimeUtcInSec = new DateTimeOffset(swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeSeconds)).ToUnixTimeSeconds();

            _ = Fa12SwapInitiatedHelper.StartSwapInitiatedControlAsync(
                swap: swap,
                currency: Fa12Config,
                tezos: XtzConfig,
                refundTimeStamp: refundTimeUtcInSec,
                interval: ConfirmationCheckInterval,
                initiatedHandler: initiatedHandler,
                canceledHandler: SwapCanceledHandler,
                cancellationToken: cancellationToken);

            return Task.CompletedTask;
        }

        public override async Task RedeemAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            var fa12 = Fa12Config;
            
            var secretResult = await Fa12SwapRedeemedHelper
                .IsRedeemedAsync(
                    swap: swap,
                    currency: fa12,
                    tezos: XtzConfig,
                    attempts: TezosSwap.MaxRedeemCheckAttempts,
                    attemptIntervalInSec: TezosSwap.RedeemCheckAttemptIntervalInSec,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!secretResult.HasError && secretResult.Value != null)
            {
                await RedeemConfirmedEventHandler(swap, null, cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            if (swap.StateFlags.HasFlag(SwapStateFlags.IsRedeemBroadcast) &&
                swap.RedeemTx != null &&
                swap.RedeemTx.CreationTime != null &&
                swap.RedeemTx.CreationTime.Value.ToUniversalTime() + TimeSpan.FromMinutes(5) > DateTime.UtcNow)
            {
                // redeem already broadcast
                _ = TrackTransactionConfirmationAsync<TezosTransaction>(
                    swap: swap,
                    dataRepository: Fa12Account.LocalStorage,
                    txId: swap.RedeemTx.Id,
                    confirmationHandler: RedeemConfirmedEventHandler,
                    cancellationToken: cancellationToken);

                return;
            }

            // check already refunded by initiator
            if (swap.IsAcceptor &&
                swap.TimeStamp.ToUniversalTime().AddSeconds(DefaultInitiatorLockTimeInSeconds) < DateTime.UtcNow)
            {
                var isRefundedByParty = await Fa12SwapRefundedHelper
                    .IsRefundedAsync(swap, fa12, XtzConfig, cancellationToken)
                    .ConfigureAwait(false);

                if (isRefundedByParty != null &&
                    !isRefundedByParty.HasError &&
                    isRefundedByParty.Value)
                {
                    swap.StateFlags |= SwapStateFlags.IsUnsettled;

                    await UpdateSwapAsync(swap, SwapStateFlags.IsUnsettled, cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }
            }

            if (swap.IsInitiator)
            {
                var redeemDeadline = swap.TimeStamp.ToUniversalTime().AddSeconds(DefaultAcceptorLockTimeInSeconds) - RedeemTimeReserve;

                if (DateTime.UtcNow > redeemDeadline)
                {
                    Log.Error("Redeem dedline reached for swap {@swap}", swap.Id);
                    return;
                }
            }

            Log.Debug("Create redeem for swap {@swapId}", swap.Id);

            var walletAddress = await TezosAccount
                .GetAddressAsync(swap.RedeemFromAddress, cancellationToken)
                .ConfigureAwait(false);

            if (walletAddress == null)
            {
                Log.Error("Can't get address {@address} for redeem from local db", swap.RedeemFromAddress);
                return;
            }

            if (walletAddress == null)
            {
                Log.Error("Insufficient funds for redeem");
                return;
            }

            var feeAmountInMtz = fa12.RedeemFee + fa12.RevealFee;
            var storageLimitInMtz = fa12.RedeemStorageLimit * fa12.StorageFeeMultiplier;

            if (walletAddress.Balance.ToMicroTez() < feeAmountInMtz + storageLimitInMtz)
            {
                Log.Error("Insufficient funds for redeem");
                return;
            }

            var redeemTx = new TezosTransaction
            {
                Currency     = XtzConfig.Name,
                CreationTime = DateTime.UtcNow,
                From         = walletAddress.Address,
                To           = fa12.SwapContractAddress,
                Amount       = 0,
                Fee          = fa12.RedeemFee + fa12.RevealFee,
                GasLimit     = fa12.RedeemGasLimit,
                StorageLimit = fa12.RedeemStorageLimit,
                Params       = CreateRedeemParams(swap),
                Type         = BlockchainTransactionType.Output | BlockchainTransactionType.SwapRedeem,

                UseRun              = true,
                UseSafeStorageLimit = true,
                UseOfflineCounter   = true
            };

            try
            {
                await TezosAccount.AddressLocker
                    .LockAsync(redeemTx.From, cancellationToken)
                    .ConfigureAwait(false);

                // temporary fix: check operation sequence
                await TezosOperationsSequencer
                    .WaitAsync(redeemTx.From, TezosAccount, cancellationToken)
                    .ConfigureAwait(false);

                using var securePublicKey = TezosAccount.Wallet
                    .GetPublicKey(XtzConfig, walletAddress.KeyIndex, walletAddress.KeyType);

                // fill operation
                var (fillResult, isRunSuccess, hasReveal) = await redeemTx
                    .FillOperationsAsync(
                        securePublicKey: securePublicKey,
                        tezosConfig: XtzConfig,
                        headOffset: TezosConfig.HeadOffset,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var signResult = await SignTransactionAsync(redeemTx, cancellationToken)
                    .ConfigureAwait(false);

                if (!signResult)
                {
                    Log.Error("Transaction signing error");
                    return;
                }

                swap.RedeemTx = redeemTx;
                swap.StateFlags |= SwapStateFlags.IsRedeemSigned;

                await UpdateSwapAsync(swap, SwapStateFlags.IsRedeemSigned, cancellationToken)
                    .ConfigureAwait(false);

                await BroadcastTxAsync(swap, redeemTx, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                throw;
            }
            finally
            {
                TezosAccount.AddressLocker.Unlock(redeemTx.From);
            }

            swap.RedeemTx = redeemTx;
            swap.StateFlags |= SwapStateFlags.IsRedeemBroadcast;

            await UpdateSwapAsync(swap, SwapStateFlags.IsRedeemBroadcast, cancellationToken)
                .ConfigureAwait(false);

            _ = TrackTransactionConfirmationAsync<TezosTransaction>(
                swap: swap,
                dataRepository: Fa12Account.LocalStorage,
                txId: redeemTx.Id,
                confirmationHandler: RedeemConfirmedEventHandler,
                cancellationToken: cancellationToken);
        }

        public override async Task RedeemForPartyAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            if (swap.IsInitiator)
            {
                var partyRedeemDeadline = swap.TimeStamp.ToUniversalTime().AddSeconds(DefaultAcceptorLockTimeInSeconds) - PartyRedeemTimeReserve;

                if (DateTime.UtcNow > partyRedeemDeadline)
                {
                    Log.Error("Party redeem deadline reached for swap {@swap}", swap.Id);
                    return;
                }
            }

            Log.Debug("Create redeem for acceptor for swap {@swapId}", swap.Id);

            var fa12 = Fa12Config;

            var walletAddress = await TezosAccount
                .GetAddressAsync(swap.FromAddress, cancellationToken)
                .ConfigureAwait(false);

            if (walletAddress == null)
            {
                Log.Error("Can't get address {@address} for redeem for party from local db", swap.FromAddress);
                return;
            }

            var feeAmountInMtz = fa12.RedeemFee + fa12.RevealFee;
            var storageLimitInMtz = fa12.RedeemStorageLimit * fa12.StorageFeeMultiplier;

            if (walletAddress.Balance.ToMicroTez() < feeAmountInMtz + storageLimitInMtz)
            {
                Log.Error("Insufficient funds for redeem for party");
            }

            var redeemTx = new TezosTransaction
            {
                Currency     = XtzConfig.Name,
                CreationTime = DateTime.UtcNow,
                From         = walletAddress.Address,
                To           = fa12.SwapContractAddress,
                Amount       = 0,
                Fee          = fa12.RedeemFee + fa12.RevealFee,
                GasLimit     = fa12.RedeemGasLimit,
                StorageLimit = fa12.RedeemStorageLimit,
                Params       = CreateRedeemParams(swap),
                Type         = BlockchainTransactionType.Output | BlockchainTransactionType.SwapRedeem,

                UseRun              = true,
                UseSafeStorageLimit = true,
                UseOfflineCounter   = true
            };

            using var addressLock = await TezosAccount.AddressLocker
                .GetLockAsync(redeemTx.From, cancellationToken)
                .ConfigureAwait(false);

            // temporary fix: check operation sequence
            await TezosOperationsSequencer
                .WaitAsync(redeemTx.From, TezosAccount, cancellationToken)
                .ConfigureAwait(false);

            using var securePublicKey = TezosAccount.Wallet
                .GetPublicKey(XtzConfig, walletAddress.KeyIndex, walletAddress.KeyType);

            // fill operation
            var (fillResult, isRunSuccess, hasReveal) = await redeemTx
                .FillOperationsAsync(
                    securePublicKey: securePublicKey,
                    tezosConfig: XtzConfig,
                    headOffset: TezosConfig.HeadOffset,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var signResult = await SignTransactionAsync(redeemTx, cancellationToken)
                .ConfigureAwait(false);

            if (!signResult)
            {
                Log.Error("Transaction signing error");
                return;
            }

            await BroadcastTxAsync(swap, redeemTx, cancellationToken)
                .ConfigureAwait(false);
        }

        public override async Task RefundAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            var fa12 = Fa12Config;

            if (swap.StateFlags.HasFlag(SwapStateFlags.IsRefundBroadcast) &&
                swap.RefundTx != null &&
                swap.RefundTx.CreationTime != null &&
                swap.RefundTx.CreationTime.Value.ToUniversalTime() + TimeSpan.FromMinutes(5) > DateTime.UtcNow)
            {
                _ = TrackTransactionConfirmationAsync<TezosTransaction>(
                    swap: swap,
                    dataRepository: Fa12Account.LocalStorage,
                    txId: swap.RefundTx.Id,
                    confirmationHandler: RefundConfirmedEventHandler,
                    cancellationToken: cancellationToken);

                return;
            }

            var lockTimeInSeconds = swap.IsInitiator
                ? DefaultInitiatorLockTimeInSeconds
                : DefaultAcceptorLockTimeInSeconds;

            var lockTime = swap.TimeStamp.ToUniversalTime() + TimeSpan.FromSeconds(lockTimeInSeconds);

            await RefundTimeDelayAsync(lockTime, cancellationToken)
                .ConfigureAwait(false);

            Log.Debug("Create refund for swap {@swap}", swap.Id);

            var walletAddress = await TezosAccount
                .GetAddressAsync(swap.FromAddress, cancellationToken)
                .ConfigureAwait(false);

            if (walletAddress == null)
            {
                Log.Error("Can't get address {@address} for refund from local db", swap.FromAddress);
                return;
            }

            var feeAmountInMtz = fa12.RefundFee + fa12.RevealFee;
            var storageLimitInMtz = fa12.RefundStorageLimit * fa12.StorageFeeMultiplier;

            if (walletAddress.Balance.ToMicroTez() < feeAmountInMtz + storageLimitInMtz)
            {
                Log.Error("Insufficient funds for refund");
            }

            var refundTx = new TezosTransaction
            {
                Currency     = XtzConfig.Name,
                CreationTime = DateTime.UtcNow,
                From         = walletAddress.Address,
                To           = fa12.SwapContractAddress,
                Fee          = fa12.RefundFee + fa12.RevealFee,
                GasLimit     = fa12.RefundGasLimit,
                StorageLimit = fa12.RefundStorageLimit,
                Params       = CreateRefundParams(swap),
                Type         = BlockchainTransactionType.Output | BlockchainTransactionType.SwapRefund,

                UseRun              = true,
                UseSafeStorageLimit = true,
                UseOfflineCounter   = true
            };

            try
            {
                await TezosAccount.AddressLocker
                    .LockAsync(refundTx.From, cancellationToken)
                    .ConfigureAwait(false);

                // temporary fix: check operation sequence
                await TezosOperationsSequencer
                    .WaitAsync(refundTx.From, TezosAccount, cancellationToken)
                    .ConfigureAwait(false);

                using var securePublicKey = TezosAccount.Wallet
                    .GetPublicKey(XtzConfig, walletAddress.KeyIndex, walletAddress.KeyType);

                // fill operation
                var (fillResult, isRunSuccess, hasReveal) = await refundTx
                    .FillOperationsAsync(
                        securePublicKey: securePublicKey,
                        tezosConfig: XtzConfig,
                        headOffset: TezosConfig.HeadOffset,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var signResult = await SignTransactionAsync(refundTx, cancellationToken)
                    .ConfigureAwait(false);

                if (!signResult)
                {
                    Log.Error("Transaction signing error");
                    return;
                }

                swap.RefundTx = refundTx;
                swap.StateFlags |= SwapStateFlags.IsRefundSigned;

                await UpdateSwapAsync(swap, SwapStateFlags.IsRefundSigned, cancellationToken)
                    .ConfigureAwait(false);

                await BroadcastTxAsync(swap, refundTx, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                throw;
            }
            finally
            {
                TezosAccount.AddressLocker.Unlock(refundTx.From);
            }

            swap.RefundTx = refundTx;
            swap.StateFlags |= SwapStateFlags.IsRefundBroadcast;

            await UpdateSwapAsync(swap, SwapStateFlags.IsRefundBroadcast, cancellationToken)
                .ConfigureAwait(false);

            _ = TrackTransactionConfirmationAsync<TezosTransaction>(
                swap: swap,
                dataRepository: Fa12Account.LocalStorage,
                txId: refundTx.Id,
                confirmationHandler: RefundConfirmedEventHandler,
                cancellationToken: cancellationToken);
        }

        public override Task StartWaitingForRedeemAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Start waiting for {@currency} redeem for swap {@swap}", Currency, swap.Id);

            var lockTimeInSeconds = swap.IsInitiator
                ? DefaultInitiatorLockTimeInSeconds
                : DefaultAcceptorLockTimeInSeconds;

            // start redeem control async
            _ = Fa12SwapRedeemedHelper.StartSwapRedeemedControlAsync(
                swap: swap,
                currency: Fa12Config,
                tezos: XtzConfig,
                refundTimeUtc: swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeInSeconds),
                interval: TimeSpan.FromSeconds(30),
                redeemedHandler: RedeemCompletedEventHandler,
                canceledHandler: RedeemCanceledEventHandler,
                cancellationToken: cancellationToken);

            return Task.CompletedTask;
        }

        public override Task StartWaitForRedeemBySomeoneAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Wait redeem for swap {@swapId}", swap.Id);

            // start redeem control async
            _ = Fa12SwapRedeemedHelper.StartSwapRedeemedControlAsync(
                swap: swap,
                currency: Fa12Config,
                tezos: XtzConfig,
                refundTimeUtc: swap.TimeStamp.ToUniversalTime().AddSeconds(DefaultAcceptorLockTimeInSeconds),
                interval: TimeSpan.FromSeconds(30),
                redeemedHandler: RedeemBySomeoneCompletedEventHandler,
                canceledHandler: RedeemBySomeoneCanceledEventHandler,
                cancellationToken: cancellationToken);

            return Task.CompletedTask;
        }

        public override async Task<Result<IBlockchainTransaction>> TryToFindPaymentAsync(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            var currency = Currencies
                .GetByName(swap.SoldCurrency);

            return await Fa12SwapInitiatedHelper
                .TryToFindPaymentAsync(
                    swap: swap,
                    currency: currency,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        #region Event Handlers

        protected override async Task RefundTimeReachedHandler(
            Swap swap,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Refund time reached for swap {@swapId}", swap.Id);

            try
            {
                var isRefundedResult = await Fa12SwapRefundedHelper.IsRefundedAsync(
                        swap: swap,
                        currency: Fa12Config,
                        tezos: XtzConfig,
                        attempts: TezosSwap.MaxRefundCheckAttempts,
                        attemptIntervalInSec: TezosSwap.RefundCheckAttemptIntervalInSec,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!isRefundedResult.HasError)
                {
                    if (isRefundedResult.Value)
                    {
                        await RefundConfirmedEventHandler(swap, swap.RefundTx, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await RefundAsync(swap, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error in refund time reached handler");
            }
        }

        private async Task RedeemBySomeoneCompletedEventHandler(
            Swap swap,
            byte[] secret,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Handle redeem party control completed event for swap {@swapId}", swap.Id);

            if (swap.IsAcceptor)
            {
                swap.Secret = secret;
                swap.StateFlags |= SwapStateFlags.IsRedeemConfirmed;

                await UpdateSwapAsync(swap, SwapStateFlags.IsRedeemConfirmed, cancellationToken)
                    .ConfigureAwait(false);

                // get transactions & update balance for address async 
                _ = AddressHelper.UpdateAddressBalanceAsync<TezosTokensWalletScanner, TezosAccount>(
                    account: TezosAccount,
                    address: swap.ToAddress,
                    cancellationToken: cancellationToken);
            }
        }

        private async Task RedeemBySomeoneCanceledEventHandler(
            Swap swap,
            DateTime refundTimeUtc,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Handle redeem party control canceled event for swap {@swapId}", swap.Id);

            try
            {
                if (swap.Secret?.Length > 0)
                {
                    await RedeemAsync(swap, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Redeem party control canceled event error");
            }
        }

        #endregion Event Handlers

        #region Helpers
        
        private async Task<bool> WaitPaymentConfirmationAsync(
            string txId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var timeStamp = DateTime.UtcNow;

            while (DateTime.UtcNow < timeStamp + timeout)
            {
                await Task.Delay(Fa12Swap.InitiationCheckInterval, cancellationToken)
                    .ConfigureAwait(false);

                var tx = await Fa12Account
                    .LocalStorage
                    .GetTransactionByIdAsync<TezosTransaction>(XtzConfig.Name, txId)
                    .ConfigureAwait(false);

                if (tx is not { IsConfirmed: true }) continue;

                return tx.IsConfirmed;
            }

            return false;
        }

        public static decimal RequiredAmountInTokens(Swap swap, Fa12Config fa12)
        {
            var requiredAmountInTokens = AmountHelper.QtyToSellAmount(swap.Side, swap.Qty, swap.Price, fa12.DigitsMultiplier);

            // maker network fee
            if (swap.MakerNetworkFee > 0 && swap.MakerNetworkFee < requiredAmountInTokens) // network fee size check
                requiredAmountInTokens += AmountHelper.RoundDown(swap.MakerNetworkFee, fa12.DigitsMultiplier);

            return requiredAmountInTokens;
        }

        protected async Task<TezosTransaction> CreatePaymentTxAsync(
            Swap swap,
            int lockTimeSeconds,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Create {@currency} payment transaction from address {@address} for swap {@swapId}",
                Currency,
                swap.FromAddress,
                swap.Id);

            var fa12 = Fa12Config;
            var requiredAmountInTokens = RequiredAmountInTokens(swap, fa12); 
            var refundTimeStampUtcInSec = new DateTimeOffset(swap.TimeStamp.ToUniversalTime().AddSeconds(lockTimeSeconds)).ToUnixTimeSeconds();

            var rewardForRedeemInTokenDigits = swap.IsInitiator
                ? swap.PartyRewardForRedeem.ToTokenDigits(fa12.DigitsMultiplier)
                : 0;

            var walletAddress = await Fa12Account
                .GetAddressAsync(swap.FromAddress, cancellationToken)
                .ConfigureAwait(false);

            var balanceInTz = (await TezosAccount
                .GetAddressBalanceAsync(
                    address: walletAddress.Address,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
                .Confirmed;

            var balanceInMtz = balanceInTz.ToMicroTez();

            var isRevealed = await TezosAccount
                .IsRevealedSourceAsync(walletAddress.Address, cancellationToken)
                .ConfigureAwait(false);

            var feeAmountInMtz = fa12.ApproveFee * 2 + fa12.InitiateFee +
                (isRevealed ? 0 : fa12.RevealFee);

            var storageLimitInMtz = (fa12.ApproveStorageLimit * 2 + fa12.InitiateStorageLimit) *
                fa12.StorageFeeMultiplier;

            if (balanceInMtz < feeAmountInMtz + storageLimitInMtz + XtzConfig.MicroTezReserve)
            {
                Log.Error(
                    "Insufficient funds at {@address} for fee. Balance: {@balance}, " +
                    "feeAmount: {@feeAmount}, storageLimit: {@storageLimit}, missing: {@missing}.",
                    walletAddress.Address,
                    balanceInMtz,
                    feeAmountInMtz,
                    storageLimitInMtz,
                    balanceInMtz - feeAmountInMtz - storageLimitInMtz - XtzConfig.MicroTezReserve);

                return null;
            }

            Log.Debug("Available balance: {@balance}", walletAddress.Balance);

            if (walletAddress.Balance < requiredAmountInTokens)
            {
                Log.Error(
                    "Insufficient funds at {@address}. Balance: {@balance}, " +
                    "required: {@required}, missing: {@missing}.",
                    walletAddress.Address,
                    walletAddress.Balance,
                    requiredAmountInTokens,
                    walletAddress.Balance - requiredAmountInTokens);

                return null;
            }

            var amountInTokens = AmountHelper.DustProofMin(
                walletAddress.Balance,
                requiredAmountInTokens,
                fa12.DigitsMultiplier,
                fa12.DustDigitsMultiplier);
                
            return new TezosTransaction
            {
                Currency     = XtzConfig.Name,
                CreationTime = DateTime.UtcNow,
                From         = walletAddress.Address,
                To           = fa12.SwapContractAddress,
                Fee          = feeAmountInMtz,
                GasLimit     = fa12.InitiateGasLimit,
                StorageLimit = fa12.InitiateStorageLimit,
                Params       = CreateInitParams(
                    swap: swap,
                    tokenContractAddress: fa12.TokenContractAddress,
                    tokenAmountInDigits: amountInTokens.ToTokenDigits(fa12.DigitsMultiplier),
                    refundTimeStamp: refundTimeStampUtcInSec,
                    redeemFeeAmount: (long)rewardForRedeemInTokenDigits),
                Type         = BlockchainTransactionType.Output | BlockchainTransactionType.SwapPayment,

                UseRun              = true,
                UseSafeStorageLimit = true,
                UseOfflineCounter   = true
            };
        }

        private async Task<IList<TezosTransaction>> CreateApproveTxsAsync(
            Swap swap,
            TezosTransaction paymentTx,
            CancellationToken cancellationToken = default)
        {
            Log.Debug("Create approve txs for swap {@swap}", swap.Id);

            var walletAddress = await Fa12Account
                .GetAddressAsync(paymentTx.From, cancellationToken)
                .ConfigureAwait(false);

            using var callingAddressPublicKey = TezosAccount.Wallet.GetPublicKey(
                currency: TezosAccount.Config,
                keyIndex: walletAddress.KeyIndex,
                keyType: walletAddress.KeyType);

            var fa12 = Fa12Config;
            var fa12Api = fa12.BlockchainApi as ITokenBlockchainApi;

            var allowanceResult = await fa12Api
                .TryGetFa12AllowanceAsync(
                    holderAddress: walletAddress.Address,
                    spenderAddress: fa12.SwapContractAddress,
                    callingAddress: walletAddress.Address,
                    securePublicKey: callingAddressPublicKey,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (allowanceResult.HasError)
            {
                Log.Error("Error while getting token allowance for {@address} with code {@code} and description {@description}",
                    walletAddress.Address,
                    allowanceResult.Error.Code,
                    allowanceResult.Error.Description);

                return null; // todo: maybe add approve 0
            }

            var transactions = new List<TezosTransaction>();

            Log.Debug("Allowance: {@allowance}", allowanceResult.Value);

            if (allowanceResult.Value > 0)
            {
                transactions.Add(new TezosTransaction
                {
                    Currency     = XtzConfig.Name,
                    CreationTime = DateTime.UtcNow,
                    From         = walletAddress.Address,
                    To           = fa12.TokenContractAddress,
                    Fee          = fa12.ApproveFee,
                    GasLimit     = fa12.ApproveGasLimit,
                    StorageLimit = fa12.ApproveStorageLimit,
                    Params       = CreateApproveParams(fa12.SwapContractAddress, 0),
                    Type         = BlockchainTransactionType.TokenApprove,

                    UseRun              = true,
                    UseSafeStorageLimit = true,
                    UseOfflineCounter   = true
                });
            }

            var requiredAmountInTokens = RequiredAmountInTokens(swap, fa12);

            var amountInTokens = AmountHelper.DustProofMin(
                walletAddress.Balance,
                requiredAmountInTokens,
                fa12.DigitsMultiplier,
                fa12.DustDigitsMultiplier);

            transactions.Add(new TezosTransaction
            {
                Currency     = XtzConfig.Name,
                CreationTime = DateTime.UtcNow,
                From         = walletAddress.Address,
                To           = fa12.TokenContractAddress,
                Fee          = fa12.ApproveFee,
                GasLimit     = fa12.ApproveGasLimit,
                StorageLimit = fa12.ApproveStorageLimit,
                Params       = CreateApproveParams(fa12.SwapContractAddress, amountInTokens.ToTokenDigits(fa12.DigitsMultiplier)),
                Type         = BlockchainTransactionType.TokenApprove,

                UseRun              = true,
                UseSafeStorageLimit = true,
                UseOfflineCounter   = true
            });

            return transactions;
        }

        private async Task<bool> SignTransactionAsync(
            TezosTransaction tx,
            CancellationToken cancellationToken = default)
        {
            var walletAddress = await TezosAccount
                .GetAddressAsync(
                    address: tx.From,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return await TezosAccount.Wallet
                .SignAsync(
                    tx: tx,
                    address: walletAddress,
                    currency: XtzConfig,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task BroadcastTxAsync(
            Swap swap,
            TezosTransaction tx,
            CancellationToken cancellationToken = default)
        {
            var broadcastResult = await XtzConfig.BlockchainApi
                .TryBroadcastAsync(tx, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (broadcastResult.HasError)
                throw new Exception($"Error while broadcast transaction with code {broadcastResult.Error.Code} and description {broadcastResult.Error.Description}");

            var txId = broadcastResult.Value;

            if (txId == null)
                throw new Exception("Transaction Id is null");

            Log.Debug("TxId {@id} for swap {@swapId}", txId, swap.Id);

            // account new unconfirmed transaction
            await TezosAccount
                .LocalStorage
                .UpsertTransactionAsync(
                    tx: tx,
                    notifyIfNewOrChanged: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        private JObject CreateApproveParams(
            string spender,
            decimal amount)
        {
            return JObject.Parse(@"{'entrypoint':'approve','value':{'prim':'Pair','args':[{'string':'" + spender + "'},{'int':'" + amount + "'}]}}");
        }

        private JObject CreateInitParams(
            Swap swap,
            string tokenContractAddress,
            decimal tokenAmountInDigits,
            long refundTimeStamp,
            long redeemFeeAmount)
        {
            return JObject.Parse(@"{'entrypoint':'initiate','value':{'prim':'Pair','args':[{'prim':'Pair','args':[{'prim':'Pair','args':[{'bytes':'" + swap.SecretHash.ToHexString() + "'},{'string':'" + swap.PartyAddress + "'}]},{'prim':'Pair','args':[{'int':'" + redeemFeeAmount + "'},{'int':'" + refundTimeStamp + "'}]}]},{'prim':'Pair','args':[{'string':'" + tokenContractAddress + "'},{'int':'" + tokenAmountInDigits + "'}]}]}}");
        }

        private JObject CreateRedeemParams(Swap swap)
        {
            return JObject.Parse(@"{'entrypoint':'redeem','value':{'bytes':'" + swap.Secret.ToHexString() + "'}}");
        }

        private JObject CreateRefundParams(Swap swap)
        {
            return JObject.Parse(@"{'entrypoint':'refund','value':{'bytes':'" + swap.SecretHash.ToHexString() + "'}}");
        }

        #endregion Helpers
    }
}