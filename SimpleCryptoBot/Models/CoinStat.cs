using CoinbasePro.Services.Products.Models;
using CoinbasePro.Services.Products.Types;
using System;
using System.Collections.Generic;
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
            IsGoodInvestment = IsWorthy(productId, client);
        }

        public string ProductId { get; set; }

        public bool IsGoodInvestment { get; set; }
        
        public decimal AskStopPrice { get; set; }

        public decimal AskLimitPrice { get; set; }

        public decimal BidStopPrice { get; set; }

        public decimal BidLimitPrice { get; set; }

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
        
        private bool IsGreen(Candle candle)
        {
            return candle.Open < candle.Close;
        }

        private bool IsRed(Candle candle)
        {
            return candle.Open > candle.Close;
        }

        private bool AreRed(IEnumerable<Candle> candles)
        {
            return candles.All(x => IsRed(x));
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

        private bool IsDownUp(Candle previousCandle, Candle currentCandle)
        {
            return !IsGreen(previousCandle) && IsGreen(currentCandle) &&
                GetCandleBodySize(previousCandle) <= GetCandleTotalSize(previousCandle) / 3 &&
                GetCandleBodySize(currentCandle) <= GetCandleTotalSize(currentCandle) / 3 &&
                !IsLongWickDown(previousCandle) && IsLongWickDown(currentCandle) &&
                GetCandleBodySize(previousCandle) < GetCandleBodySize(currentCandle) &&
                IsStrongTrend(previousCandle, currentCandle);
        }

        private bool IsDecreasingSize(IEnumerable<Candle> candles)
        {
            var orderByTime = candles.OrderBy(x => x.Time);
            var orderBySize = candles.OrderByDescending(x => GetCandleBodySize(x));
            return Equals(orderByTime, orderBySize);
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
            var product = client.ProductsService.GetSingleProductAsync(productId).Result;
            ThrottleSpeedPublic();
            
            var end = DateTime.UtcNow;
            var start = end.AddMinutes(-60);

            var candles = client.ProductsService
                .GetHistoricRatesAsync(productId, start, end, CandleGranularity.Minutes15)
                .Result.OrderBy(x => x.Time);
            ThrottleSpeedPublic();

            if(candles?.Count() != 4)
            {
                return false;
            }

            var isWorthy = IsDecreasingSize(candles.Skip(1).Take(2)) &&
                AreRed(candles.Take(3));

            BidStopPrice = candles.Last().High.Value;
            BidLimitPrice = BidStopPrice + (BidStopPrice * (decimal)0.01);
            BidLimitPrice = GetTruncatedValue(BidLimitPrice, product.QuoteIncrement);

            AskStopPrice = candles.Last().Low.Value;
            AskLimitPrice = AskStopPrice - (AskStopPrice * (decimal)0.01);
            AskLimitPrice = GetTruncatedValue(AskLimitPrice, product.QuoteIncrement);

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

        private decimal GetTruncatedValue(decimal value, decimal increment)
        {
            var remainder = value % increment;

            if (remainder > 0)
            {
                value -= remainder;
            }

            return value;
        }
    }
}
