﻿namespace Atomex.Swaps.Abstract
{
    public interface ISwapClient
    {
        void SwapInitiateAsync(
            long id,
            byte[] secretHash,
            string symbol,
            string toAddress,
            decimal rewardForRedeem,
            string refundAddress);

        void SwapAcceptAsync(
            long id,
            string symbol,
            string toAddress,
            decimal rewardForRedeem,
            string refundAddress);

        void SwapStatusAsync(
            string requestId,
            long swapId);
    }
}