using CoinbasePro.Services.Products.Models;
using CoinbasePro.Services.Products.Types;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCryptoBot.Models
{
    public class CoinStat
    {
        public CoinStat(string productId, CoinbasePro.ICoinbaseProClient client)
        {
            ProductId = productId;
            GetCandles(productId, client).Wait();
            IsGoodInvestment = IsWorthy(productId, client);
        }

        public string ProductId { get; set; }

        public bool IsGoodInvestment { get; set; }

        public bool ProfitMultiplier { get; set; } // If true, then invest more.

        public decimal StopLossStopPrice { get; set; }

        public decimal StopLossLimitPrice { get; set; }

        public CandleGranularity Granularity { get; set; }

        public Candle Red { get; set; }

        public Candle Green { get; set; }

        private async Task<CandleGranularity> GetCandleGranularity(string productId, decimal feeRate, CoinbasePro.ICoinbaseProClient client, CandleGranularity granularity = CandleGranularity.Minutes1)
        {
            var start = DateTime.UtcNow;
            var end = start;

            switch (granularity)
            {
                case CandleGranularity.Minutes1:
                    start = start.AddMinutes(-1);
                    break;
                case CandleGranularity.Minutes5:
                    start = start.AddMinutes(-5);
                    break;
                case CandleGranularity.Minutes15:
                    start = start.AddMinutes(-15);
                    break;
                case CandleGranularity.Hour1:
                    start = start.AddMinutes(-60);
                    break;
                case CandleGranularity.Hour6:
                    start = start.AddMinutes(-360);
                    break;
                case CandleGranularity.Hour24:
                    start = start.AddMinutes(-1440);
                    break;
                default:
                    break;
            }

            var candles = await client.ProductsService.GetHistoricRatesAsync(productId, start, end, granularity);
            ThrottleSpeedPublic();

            var high = candles.Max(x => x.High);
            var low = candles.Min(x => x.Low);
            var change = (high - low) / high;

            if(change > feeRate || granularity == CandleGranularity.Hour24)
            {
                // Smallest unit of time that is profitable
                return granularity;
            }
            else
            {
                var newGranularity = granularity;

                switch (newGranularity)
                {
                    case CandleGranularity.Minutes1:
                        newGranularity = CandleGranularity.Minutes5;
                        break;
                    case CandleGranularity.Minutes5:
                        newGranularity = CandleGranularity.Minutes15;
                        break;
                    case CandleGranularity.Minutes15:
                        newGranularity = CandleGranularity.Hour1;
                        break;
                    case CandleGranularity.Hour1:
                        newGranularity = CandleGranularity.Hour6;
                        break;
                    case CandleGranularity.Hour6:
                        newGranularity = CandleGranularity.Hour24;
                        break;
                    case CandleGranularity.Hour24:
                        break;
                    default:
                        break;
                }

                return await GetCandleGranularity(productId, feeRate, client, newGranularity);
            }
        }

        private async Task GetCandles(string productId, CoinbasePro.ICoinbaseProClient client)
        {
            var feeRates = await client.FeesService.GetCurrentFeesAsync();
            ThrottleSpeedPrivate();
            var feeRate = feeRates.MakerFeeRate > 0 ? feeRates.MakerFeeRate * 2 : feeRates.TakerFeeRate;
            var granularity = await GetCandleGranularity(productId, feeRate, client);
            
            var start = DateTime.UtcNow;
            var end = start;

            switch (granularity)
            {
                case CandleGranularity.Minutes1:
                    start = start.AddMinutes(-2);
                    break;
                case CandleGranularity.Minutes5:
                    start = start.AddMinutes(-10);
                    break;
                case CandleGranularity.Minutes15:
                    start = start.AddMinutes(-30);
                    break;
                case CandleGranularity.Hour1:
                    start = start.AddMinutes(-120);
                    break;
                case CandleGranularity.Hour6:
                    start = start.AddMinutes(-720);
                    break;
                case CandleGranularity.Hour24:
                    start = start.AddMinutes(-2880);
                    break;
                default:
                    break;
            }

            var candles = client.ProductsService
                .GetHistoricRatesAsync(productId, start, end, granularity)
                .Result.OrderBy(x => x.Time)
                .ToList();
            ThrottleSpeedPublic();

            if (candles?.Count == 2)
            {
                Red = candles.First();
                Green = candles.Last();
                Granularity = granularity;
                StopLossStopPrice = Green.Low.Value - (Green.Low.Value * feeRate);
                StopLossLimitPrice = StopLossStopPrice - (Green.Low.Value * feeRate);

                // Smaller body on inside bar indicates low volatility and price change. Invest more.
                ProfitMultiplier = GetCandleBodySize(Green) < GetCandleTotalSize(Green) / 3;
            }
        }

        private bool IsGreen(Candle candle)
        {
            return candle.Open < candle.Close;
        }

        private bool IsStrongTrend(Candle previousCandle, Candle currentCandle)
        {
            return currentCandle.Volume > previousCandle.Volume;
        }

        private bool IsDecreasingVolatility(Candle previousCandle, Candle currentCandle)
        {
            return currentCandle.Volume < previousCandle.Volume;
        }

        private bool IsInsideBar(Candle previousCandle, Candle currentCandle)
        {
            return !IsGreen(previousCandle) &&
                IsGreen(currentCandle) &&
                GetCandleTotalSize(previousCandle) > GetCandleTotalSize(currentCandle) &&
                GetCandleBodySize(previousCandle) > GetCandleBodySize(currentCandle) &&
                previousCandle.Low < currentCandle.Low;
        }

        // Indicates resistance level reaching low point.
        private bool IsLongWickDown(Candle candle)
        {
            var highWick = IsGreen(candle) ? candle.High - candle.Close : candle.High - candle.Open;
            var lowWick = IsGreen(candle) ? candle.Open - candle.Low : candle.Close - candle.Low;

            return lowWick > highWick;
        }

        private decimal GetCandleBodySize(Candle candle)
        {
            if (IsGreen(candle))
            {
                return candle.Close.Value - candle.Open.Value;
            }
            else
            {
                return candle.Open.Value - candle.Close.Value;
            }
        }

        private decimal GetCandleTotalSize(Candle candle)
        {
            return candle.High.Value - candle.Low.Value;
        }

        private bool IsWorthy(string productId, CoinbasePro.ICoinbaseProClient client)
        {
            var feeRates = client.FeesService.GetCurrentFeesAsync().Result;
            ThrottleSpeedPrivate();

            var feeGap = feeRates.MakerFeeRate * 2;

            var isWorthy =  IsInsideBar(Red, Green) &&
                            IsDecreasingVolatility(Red, Green) &&
                            IsLongWickDown(Green)
                            ;

            return isWorthy;
        }

        private void ThrottleSpeedPublic()
        {
            Thread.Sleep(67);
        }

        private void ThrottleSpeedPrivate()
        {
            Thread.Sleep(34);
        }
    }
}
