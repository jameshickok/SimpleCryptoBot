using CoinbasePro.Network.Authentication;
using Serilog;
using SimpleCryptoBot.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCryptoBot
{
    class Program
    {
        static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                            .MinimumLevel.Information()
                            .WriteTo.Console()
                            .WriteTo.File("log.txt",
                                rollingInterval: RollingInterval.Day,
                                rollOnFileSizeLimit: true,
                                retainedFileCountLimit: 3)
                            .CreateLogger();

            Log.Information("SimpleCryptoBot (c) James Hickok");
            Log.Information("Not for sale or distribution.");
            Log.Information("All rights reserved.");

            var sb = ConfigurationSettings.AppSettings["sandbox"];
            var sandbox = bool.Parse(sb);

            var clientOptions = InitializePrivateClientOptions();

            var reportsSent = new Dictionary<string, bool>();

            foreach(var opt in clientOptions)
            {
                reportsSent.Add(opt.Name, false);
            }

            while(true)
            {
                foreach (var option in clientOptions)
                {
                    var authenticator = new Authenticator(option.Key, option.Secret, option.Passphrase);

                    using (var client = new PrivateClient(option.Name, option.Email, authenticator, sandbox))
                    {
                        try
                        {
                            Log.Information($"Now managing {client.Name}'s account...");

                            var allCoins = client.ProductsService.GetAllProductsAsync().Result
                                        .Where(x =>
                                                    x.QuoteCurrency == "USD" &&
                                                    x.BaseCurrency != "USDC" &&
                                                    x.BaseCurrency != "USDT" &&
                                                    !x.TradingDisabled &&
                                                    !x.CancelOnly &&
                                                    !x.PostOnly
                                                    )
                                        .ToList();
                            ThrottleSpeedPublic();

                            var allAccounts = client.AccountsService.GetAllAccountsAsync().Result;
                            ThrottleSpeedPrivate();
                            var usdAccountId = allAccounts.FirstOrDefault(x => x.Currency == "USD")?.Id.ToString();

                            Log.Information("Scanning coins...");
                            foreach (var coin in allCoins.Where(x => !allAccounts.Any(y => y.Currency == x.BaseCurrency && y.Available >= x.MinMarketFunds)))
                            {
                                try
                                {
                                    Console.Write($" {coin.Id} ");
                                    var coinStat = new CoinStat(coin.Id, client);

                                    if (coinStat.IsGoodInvestment)
                                    {
                                        // Buy the coin.
                                        var usdAccount = client.AccountsService.GetAccountByIdAsync(usdAccountId).Result;
                                        ThrottleSpeedPrivate();

                                        var spendingAmountAvailable = usdAccount.Available * (decimal)0.9;
                                        var feeRates = client.FeesService.GetCurrentFeesAsync().Result;
                                        ThrottleSpeedPrivate();
                                        
                                        var investment = spendingAmountAvailable / 10;
                                        var profitFactor = coinStat.ChangePercent - (feeRates.MakerFeeRate * 2); // Can be positive or negative depending on short term profit potential.

                                        investment = investment + (investment * profitFactor);

                                        var ticker = client.ProductsService.GetProductTickerAsync(coin.Id).Result;
                                        var price = ticker.Price;
                                        var stopPrice = price + (price * feeRates.TakerFeeRate);
                                        var limitPrice = stopPrice + (stopPrice * feeRates.TakerFeeRate);
                                        var size = investment / limitPrice;

                                        if (size > coin.BaseMaxSize)
                                        {
                                            size = coin.BaseMaxSize;
                                        }

                                        stopPrice = GetTruncatedValue(stopPrice, coin.QuoteIncrement);
                                        limitPrice = GetTruncatedValue(limitPrice, coin.QuoteIncrement);
                                        size = GetTruncatedValue(size, coin.BaseIncrement);

                                        if (size >= coin.BaseMinSize)
                                        {
                                            client.OrdersService.PlaceStopOrderAsync(
                                                 CoinbasePro.Services.Orders.Types.OrderSide.Buy,
                                                 coin.Id,
                                                 size,
                                                 limitPrice,
                                                 stopPrice
                                            ).Wait();
                                            ThrottleSpeedPrivate();

                                            Log.Information($"Purchase order created for coin {coin.Id} with a starting bid of ${Math.Round(limitPrice, 4)}.");
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                    continue;
                                }
                            }

                            allAccounts = client.AccountsService.GetAllAccountsAsync().Result;
                            ThrottleSpeedPrivate();

                            Console.WriteLine();
                            Log.Information("Scanning stop loss...");
                            foreach (var account in allAccounts.Where(x => x.Currency != "USD" && x.Currency != "USDC" && x.Currency != "USDT" && x.Available > 0))
                            {
                                try
                                {
                                    var productId = $"{account.Currency}-USD";
                                    var coin = allCoins.FirstOrDefault(x => x.Id == productId);
                                    var ticker = client.ProductsService.GetProductTickerAsync(coin.Id).Result;
                                    ThrottleSpeedPublic();
                                    var price = ticker.Price;
                                    var stopPrice = price * (decimal)0.81;
                                    var limitPrice = price * (decimal)0.80; // 20% stop loss
                                    var size = account.Available;

                                    if (size > coin.BaseMaxSize)
                                    {
                                        size = coin.BaseMaxSize;
                                    }

                                    stopPrice = GetTruncatedValue(stopPrice, coin.QuoteIncrement);
                                    limitPrice = GetTruncatedValue(limitPrice, coin.QuoteIncrement);
                                    size = GetTruncatedValue(size, coin.BaseIncrement);

                                    if (size >= coin.BaseMinSize)
                                    {
                                        client.OrdersService.PlaceStopOrderAsync(
                                             CoinbasePro.Services.Orders.Types.OrderSide.Sell,
                                             coin.Id,
                                             size,
                                             limitPrice,
                                             stopPrice
                                        ).Wait();
                                        ThrottleSpeedPrivate();

                                        Log.Information($"Stop loss order created for coin {coin.Id}.");
                                    }
                                }
                                catch (Exception exc)
                                {
                                    Log.Error(exc, exc.Message);
                                    if (exc.InnerException != null)
                                    {
                                        Log.Error(exc.InnerException, exc.InnerException.Message);
                                    }
                                }
                            }

                            // Handle existing orders
                            var orders = client.OrdersService.GetAllOrdersAsync(new CoinbasePro.Services.Orders.Types.OrderStatus[] {
                                             CoinbasePro.Services.Orders.Types.OrderStatus.Active,
                                             CoinbasePro.Services.Orders.Types.OrderStatus.Open
                                        }).Result.SelectMany(x => x).ToList();
                            ThrottleSpeedPrivate();

                            Log.Information("Scanning orders...");
                            foreach (var order in orders)
                            {
                                try
                                {
                                    var feeRates = client.FeesService.GetCurrentFeesAsync().Result;
                                    ThrottleSpeedPrivate();

                                    var ticker = client.ProductsService.GetProductTickerAsync(order.ProductId).Result;
                                    ThrottleSpeedPublic();
                                    var price = ticker.Price;

                                    var coin = allCoins.FirstOrDefault(x => x.Id == order.ProductId);
                                    var coinStat = new CoinStat(order.ProductId, client);

                                    switch (order.Side)
                                    {
                                        case CoinbasePro.Services.Orders.Types.OrderSide.Buy:
                                            var newBidStop = price + (price * feeRates.TakerFeeRate);
                                            var newBidLimit = newBidStop + (newBidStop * feeRates.TakerFeeRate);
                                            newBidStop = GetTruncatedValue(newBidStop, coin.QuoteIncrement);
                                            newBidLimit = GetTruncatedValue(newBidLimit, coin.QuoteIncrement);

                                            if (newBidLimit < order.Price)
                                            {
                                                client.OrdersService.CancelOrderByIdAsync(order.Id.ToString()).Wait();
                                                ThrottleSpeedPrivate();

                                                client.OrdersService.PlaceStopOrderAsync(
                                                     order.Side,
                                                     order.ProductId,
                                                     order.Size,
                                                     newBidLimit,
                                                     newBidStop
                                                ).Wait();
                                                ThrottleSpeedPrivate();

                                                Log.Information($"Buy order for coin {coin.Id} price driven from ${order.Price} to ${Math.Round(newBidLimit, 4)}.");
                                            }
                                            break;
                                        case CoinbasePro.Services.Orders.Types.OrderSide.Sell:

                                            if(order.Price < price * (decimal)0.80)
                                            {
                                                // This only changes order price locally for calculations.
                                                // This is the original price, or as close as popssible to it.
                                                order.Price *= (decimal)1.25;
                                            }
                                            var cost = order.Price + (order.Price * feeRates.MakerFeeRate) + (price * feeRates.MakerFeeRate);
                                            var profitMargin = coinStat.ChangePercent > (feeRates.MakerFeeRate * 2) ?
                                                coinStat.ChangePercent - (feeRates.MakerFeeRate * 2) :
                                                feeRates.TakerFeeRate;
                                            var minimumPrice = cost + (cost * profitMargin);

                                            var stopPrice = price - (price * feeRates.TakerFeeRate);
                                            var limitPrice = stopPrice - (stopPrice * feeRates.TakerFeeRate);

                                            stopPrice = GetTruncatedValue(stopPrice, coin.QuoteIncrement);
                                            limitPrice = GetTruncatedValue(limitPrice, coin.QuoteIncrement);

                                            // Let the stop loss order season before trailing to sell.
                                            var timer = DateTime.UtcNow;

                                            switch (coinStat.Granularity)
                                            {
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Minutes1:
                                                    timer = timer.AddMinutes(-2);
                                                    break;
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Minutes5:
                                                    timer = timer.AddMinutes(-10);
                                                    break;
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Minutes15:
                                                    timer = timer.AddMinutes(-30);
                                                    break;
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Hour1:
                                                    timer = timer.AddMinutes(-120);
                                                    break;
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Hour6:
                                                    timer = timer.AddMinutes(-720);
                                                    break;
                                                case CoinbasePro.Services.Products.Types.CandleGranularity.Hour24:
                                                    timer = timer.AddMinutes(-2880);
                                                    break;
                                                default:
                                                    break;
                                            }
                                            
                                            if (limitPrice > minimumPrice)
                                            {
                                                client.OrdersService.CancelOrderByIdAsync(order.Id.ToString()).Wait();
                                                ThrottleSpeedPrivate();

                                                client.OrdersService.PlaceStopOrderAsync(
                                                     order.Side,
                                                     order.ProductId,
                                                     order.Size,
                                                     limitPrice,
                                                     stopPrice
                                                ).Wait();
                                                ThrottleSpeedPrivate();

                                                Log.Information($"Sell order for coin {coin.Id} price driven from ${order.Price} to ${Math.Round(limitPrice, 4)}.");
                                            }
                                            break;
                                        default:
                                            break;
                                    }
                                }
                                catch (Exception exc)
                                {
                                    Log.Error(exc, exc.Message);
                                    if (exc.InnerException != null)
                                    {
                                        Log.Error(exc.InnerException, exc.InnerException.Message);
                                    }
                                }
                            }

                            // Send weekly reports once on Sundays.
                            if(DateTime.Now.DayOfWeek == DayOfWeek.Sunday && reportsSent.Any(x => x.Key == client.Name && x.Value))
                            {
                                SendWeeklyReports(client).Wait();
                            }
                        }
                        catch (Exception exc)
                        {
                            Log.Error(exc, exc.Message);
                            if (exc.InnerException != null)
                            {
                                Log.Error(exc.InnerException, exc.InnerException.Message);
                            }
                        }
                    }
                }
            }
        }

        private static void ThrottleSpeedPublic()
        {
            Thread.Sleep(67);
        }

        private static void ThrottleSpeedPrivate()
        {
            Thread.Sleep(34);
        }

        private static decimal GetTruncatedValue(decimal value, decimal increment)
        {
            var remainder = value % increment;

            if (remainder > 0)
            {
                value -= remainder;
            }

            return value;
        }

        /// <summary>
        /// Emails weekly reports of fills.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private static async Task SendWeeklyReports(PrivateClient client)
        {
            try
            {
                Log.Information($"Emailing weekly reports to {client.Name}.");

                var email = client.Email;
                var start = DateTime.UtcNow.AddDays(-7);
                var end = DateTime.UtcNow;

                var accounts = await client.AccountsService.GetAllAccountsAsync();
                ThrottleSpeedPrivate();

                var usdAccount = accounts.FirstOrDefault(x => x.Currency == "USD");

                foreach (var account in accounts.Where(x => x.Currency != "USD" && x.Currency != "USDC" && x.Currency != "USDT"))
                {
                    try
                    {
                        var productId = $"{account.Currency}-USD";
                        var fills = client.FillsService.GetFillsByProductIdAsync(productId).Result.SelectMany(x => x);
                        ThrottleSpeedPrivate();

                        if (fills.Any(x => x.CreatedAt > start))
                        {
                            Log.Information($"Sending {account.Currency} report.");

                            var responseFill = await client.ReportsService.CreateNewFillsReportAsync(
                                start,
                                end,
                                accountId: usdAccount.Id.ToString(),
                                productType: productId,
                                email: email,
                                fileFormat: CoinbasePro.Services.Reports.Types.FileFormat.Pdf
                                );
                            ThrottleSpeedPrivate();

                            // Wait for the report to email.
                            while (responseFill.Status != CoinbasePro.Services.Reports.Types.ReportStatus.Ready)
                            {
                                responseFill = await client.ReportsService.GetReportStatus(responseFill.Id.ToString());
                                ThrottleSpeedPrivate();
                            }

                            Log.Information($"{account.Currency} report sent.");
                        }
                    }
                    catch (Exception ex1)
                    {
                        Log.Error(ex1, ex1.Message);
                        if (ex1.InnerException != null)
                        {
                            Log.Error(ex1.InnerException, ex1.InnerException.Message);
                        }
                    }
                }
            }
            catch (Exception e1)
            {
                Log.Error(e1, e1.Message);
                if (e1.InnerException != null)
                {
                    Log.Error(e1.InnerException, e1.InnerException.Message);
                }
            }

            Log.Information($"Reporting finished for {client.Name}.");
        }

        private static List<ClientOptions> InitializePrivateClientOptions()
        {
            var options = new List<ClientOptions>();
            var contents = File.ReadAllLines("keys.csv");
            var headerRow = contents.FirstOrDefault();
            if (headerRow == "Name,Email,Passphrase,Secret,Key")
            {
                foreach (var friend in contents.Where(x => x != headerRow))
                {
                    var friendKeys = friend.Split(',');

                    if (friendKeys.Count() == 5)
                    {
                        var name = friendKeys.ElementAt(0);
                        var email = friendKeys.ElementAt(1);
                        var passphrase = friendKeys.ElementAt(2);
                        var secret = friendKeys.ElementAt(3);
                        var key = friendKeys.ElementAt(4);

                        var option = new ClientOptions
                        {
                            Name = name,
                            Email = email,
                            Passphrase = passphrase,
                            Secret = secret,
                            Key = key
                        };

                        options.Add(option);
                    }
                }
            }
            return options;
        }
    }
}
