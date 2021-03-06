﻿using System.Globalization;
using System.Threading.Tasks;
using Discord.Commands;
using JetBrains.Annotations;
using Mute.Extensions;
using Mute.Services;

namespace Mute.Modules
{
    public class Finance
        : BaseModule
    {
        private readonly CryptoCurrencyService _cryptoService;
        private readonly AlphaAdvantageService _stockService;

        public Finance(CryptoCurrencyService cryptoService, AlphaAdvantageService stockService)
        {
            _cryptoService = cryptoService;
            _stockService = stockService;
        }

        [Command("ticker"), Summary("I will find out information about a stock or currency")]
        public async Task Ticker([NotNull] string symbolOrName, string quote = "USD")
        {
            if (await TickerAsCrypto(symbolOrName, quote))
                return;

            if (await TickerAsStock(symbolOrName))
                return;

            if (await TickerAsForex(symbolOrName, quote))
                return;

            await TypingReplyAsync($"I can't find a stock or a currency called '{symbolOrName}'");
        }

        private async Task<bool> TickerAsStock(string symbolOrName)
        {
            var result = await _stockService.StockQuote(symbolOrName);

            if (result != null)
            {
                var change = "";
                var delta = result.Price - result.Open;
                if (delta > 0)
                    change += "up ";
                else
                    change += "down";
                change += $"{(delta / result.Price):P}";

                await TypingReplyAsync($"{result.Symbol} is trading at {result.Price:0.00}, {change} since opening today");
                return true;
            }

            return false;
        }

        private async Task<bool> TickerAsForex(string symbolOrName, string quote)
        {
            var result = await _stockService.CurrencyExchangeRate(symbolOrName, quote);

            if (result != null)
            {
                await TypingReplyAsync($"{result.FromName} ({result.FromCode}) is worth {result.ToCode.TryGetCurrencySymbol()}{result.ExchangeRate.ToString("0.00", CultureInfo.InvariantCulture)}");
                return true;
            }

            return false;
        }

        private async Task<bool> TickerAsCrypto([NotNull] string symbolOrName, string quote)
        {
            //Try to parse the sym/name as a cryptocurrency
            var currency = await _cryptoService.Find(symbolOrName);
            if (currency == null)
                return false;

            var ticker = await _cryptoService.GetTicker(currency, quote);

            //Begin forming the reply
            var reply = $"{ticker.Name} ({ticker.Symbol})";

            //Try to find quote in selected currency, if not then default to USD
            Task ongoingTask = null;
            if (!ticker.Quotes.TryGetValue(quote.ToUpperInvariant(), out var val) && quote != "USD")
            {
                ongoingTask = TypingReplyAsync($"I'm not sure what the value is in '{quote.ToUpperInvariant()}', I'll try 'USD' instead");

                quote = "USD";
                ticker.Quotes.TryGetValue(quote, out val);
            }

            //Format the value part of the quote
            if (val != null)
            {
                var price = val.Price.ToString("0.00", CultureInfo.InvariantCulture);
                reply += $" is worth {quote.TryGetCurrencySymbol().ToUpperInvariant()}{price}";

                if (val.PctChange24H.HasValue)
                {
                    if (val.PctChange24H > 0)
                        reply += " (up";
                    else
                        reply += " (down";

                    reply += $" {val.PctChange24H}% in 24H)";
                }
            }

            //If we were typing a previous response, wait for that to complete
            if (ongoingTask != null)
                await ongoingTask;

            await TypingReplyAsync(reply);

            return true;
        }
    }
}
