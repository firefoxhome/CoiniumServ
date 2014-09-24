﻿#region License
// 
//     CoiniumServ - Crypto Currency Mining Pool Server Software
//     Copyright (C) 2013 - 2014, CoiniumServ Project - http://www.coinium.org
//     http://www.coiniumserv.com - https://github.com/CoiniumServ/CoiniumServ
// 
//     This software is dual-licensed: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//    
//     For the terms of this license, see licenses/gpl_v3.txt.
// 
//     Alternatively, you can license this software under a commercial
//     license or white-label it as set out in licenses/commercial.txt.
// 
#endregion
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoiniumServ.Daemon;
using CoiniumServ.Daemon.Exceptions;
using CoiniumServ.Persistance.Layers;
using CoiniumServ.Pools;
using Serilog;

namespace CoiniumServ.Payments
{
    public class PaymentProcessor:IPaymentProcessor
    {
        public bool Active { get; private set; }

        private readonly Stopwatch _stopWatch = new Stopwatch();

        private readonly IPoolConfig _poolConfig;

        private readonly IStorageLayer _storageLayer;

        private readonly IDaemonClient _daemonClient;

        private string _poolAccount = string.Empty;

        private readonly ILogger _logger;

        public PaymentProcessor(IPoolConfig poolConfig, IStorageLayer storageLayer, IDaemonClient daemonClient)
        {
            _poolConfig = poolConfig;
            _storageLayer = storageLayer;
            _daemonClient = daemonClient;
            _logger = Log.ForContext<PaymentProcessor>().ForContext("Component", poolConfig.Coin.Name);

            if (!_poolConfig.Payments.Enabled) // make sure payments are enabled.
                return;

            if (!ValidatePoolAddress()) // try to validate the pool wallet.
                return; // if we can't, stop the payment processor.

            if (!GetPoolAccount()) // get the pool's account name if any.
                return; // if we can't, stop the payment processor.

            Active = true;
        }

        public void Run()
        {
            _stopWatch.Start();

            var candidates = GetTransactionCandidates(); // get the pending payments available for execution.            
            var executedPayments = ExecutePayments(candidates); // try to execute the payments.
            CommitTransactions(executedPayments); // commit them to storage layer.

            if (executedPayments.Count > 0)
                _logger.Information("Executed {0} payments, took {1:0.000} seconds", executedPayments.Count, (float)_stopWatch.ElapsedMilliseconds / 1000);
            else
                _logger.Information("No pending payments found");

            _stopWatch.Reset();
        }

        private IEnumerable<KeyValuePair<string, List<ITransaction>>> GetTransactionCandidates()
        {
            var pendingPayments = _storageLayer.GetPendingPayments(); // get all pending payments.
            var perUserTransactions = new Dictionary<string, List<ITransaction>>();  // list of payments to be executed.
             
            foreach (var payment in pendingPayments)
            {
                // query the user for the payment.
                var user = _storageLayer.GetAccountById(payment.AccountId);

                if (user == null)
                    continue;

                if (!perUserTransactions.ContainsKey(user.Username)) // check if our list of transactions to be executed already contains an entry for the user.
                {
                    // if not, create an entry that contains the list of transactions for the user.

                    // see if user payout address is directly payable from the pool's main daemon connection
                    // which happens when a user connects an XYZ pool and want his payments in XYZ coin.

                    var result = _daemonClient.ValidateAddress(user.Address); // does the user have a directly payable address set?

                    if (!result.IsValid) // if not skip the payment and let it handled by auto-exchange module.
                        continue;

                    perUserTransactions.Add(user.Username, new List<ITransaction>());
                }

                perUserTransactions[user.Username].Add(new Transaction(user, payment, _poolConfig.Coin.Symbol)); // add the payment to user.
            }

            return perUserTransactions;
        }

        private IList<ITransaction> ExecutePayments(IEnumerable<KeyValuePair<string, List<ITransaction>>> paymentsToExecute)
        {
            var executed = new List<ITransaction>();

            try
            {
                // filter out users whom total amount doesn't exceed the configured minimum payment amount.
                var filtered = paymentsToExecute.Where(
                        x => x.Value.Sum(y => y.Payment.Amount) >= (decimal)_poolConfig.Payments.Minimum)
                        .ToDictionary(x => x.Key, x => x.Value);

                if (filtered.Count <= 0)  // make sure we have payments to execute even after our filter.
                    return executed;

                // coin daemon expects us to handle outputs in <wallet_address,amount> format, create the data structure so.
                var outputs = filtered.ToDictionary(x => x.Key, x => x.Value.Sum(y => y.Payment.Amount));

                // send the payments all-together.
                var txId = _daemonClient.SendMany(_poolAccount, outputs);

                // loop through all executed payments
                filtered.ToList().ForEach(x => x.Value.ForEach(y =>
                {
                    y.TxId = txId; // set transaction id.
                    y.Payment.Completed = true; // set as completed.
                }));

                executed = filtered.SelectMany(x => x.Value).ToList();

                return executed;
            }
            catch (RpcException e)
            {
                _logger.Error("An error occured while trying to execute payment; {0}", e.Message);
                return executed;
            }
        }

        private void CommitTransactions(IList<ITransaction> executedPayments)
        {
            if (executedPayments.Count == 0) // make sure we have payments to execute.
                return;

            // commit transactions & update payments.
            foreach (var transaction in executedPayments)
            {
                _storageLayer.AddTransaction(transaction);
                _storageLayer.UpdatePayment(transaction.Payment);
            }
        }

        private bool ValidatePoolAddress()
        {
            try
            {
                var result = _daemonClient.ValidateAddress(_poolConfig.Wallet.Adress);

                // make sure the pool central wallet address is valid and belongs to the daemon we are connected to.
                if (result.IsValid && result.IsMine)
                    return true;

                _logger.Error("Halted as daemon we are connected to does not own the pool address: {0:l}.", _poolConfig.Wallet.Adress);
                return false;
            }
            catch (RpcException e)
            {
                _logger.Error("Halted as we can not connect to configured coin daemon: {0:l}", e.Message);
                return false;
            }
        }

        private bool GetPoolAccount()
        {
            try
            {
                _poolAccount = !_poolConfig.Coin.Options.UseDefaultAccount // if UseDefaultAccount is not set
                    ? _daemonClient.GetAccount(_poolConfig.Wallet.Adress) // find the account of the our pool address.
                    : ""; // use the default account.
                return true;
            }
            catch (RpcException e)
            {
                _logger.Error("Cannot determine pool's central wallet account: {0:l}", e.Message);
                return false;
            }
        }
    }
}
