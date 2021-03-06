﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Addons.Interactive;
using Discord.Commands;
using Discord.WebSocket;
using JetBrains.Annotations;
using MoreLinq;
using Mute.Extensions;
using Mute.Services;

namespace Mute.Modules
{
    public class Iou
        : BaseModule
    {
        private readonly IouDatabaseService _database;
        private readonly Random _random;
        private readonly DiscordSocketClient _client;

        public Iou(IouDatabaseService database, Random random, DiscordSocketClient client)
        {
            _database = database;
            _random = random;
            _client = client;
        }

        #region debts
        [Command("iou"), Summary("I will remember that you owe something to another user")]
        public async Task CreateDebt([NotNull] IUser user, decimal amount, [NotNull] string unit, [CanBeNull, Remainder] string note = null)
        {
            if (amount < 0)
                await TypingReplyAsync("You cannot owe a negative amount!");

            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                await _database.InsertDebt(user.Id, Context.User.Id, amount, unit, note);

                var symbol = unit.TryGetCurrencySymbol();
                if (unit == symbol)
                    await ReplyAsync($"{Context.User.Mention} owes {amount}{unit} to {user.Mention}");
                else
                    await ReplyAsync($"{Context.User.Mention} owes {symbol}{amount} to {user.Mention}");
            }
        }

        [Command("io"), Summary("I will tell you what you currently owe")]
        public async Task ListDebtsByBorrower([CanBeNull, Summary("Filter debts by this borrower")] IUser borrower = null)
        {
            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var owed = (await _database.GetOwed(Context.User.Id))
                    .Where(o => borrower == null || o.LenderId == borrower.Id)
                    .OrderBy(o => o.LenderId)
                    .ToArray();

                if (owed.Length == 0)
                    await TypingReplyAsync("You are debt free");
                else
                    await PaginatedTransactions(owed, "owes", false);
            }
        }

        [Command("oi"), Summary("I will tell you what you are currently owed")]
        public async Task ListDebtsByLender([CanBeNull, Summary("Filter debts by this lender")] IUser lender = null)
        {
            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var owed = (await _database.GetLent(Context.User.Id))
                    .Where(o => lender == null || o.BorrowerId == lender.Id)
                    .OrderBy(o => o.LenderId)
                    .ToArray();

                if (owed.Length == 0)
                    await TypingReplyAsync("No one owes you anything");
                else
                    await PaginatedTransactions(owed, "owes", false);
            }
        }
        #endregion

        #region payments/demands
        [Command("uoi"), Summary("I will notify someone that they owe you money")]
        public async Task CreateDebtDemand([NotNull] IUser debter, decimal amount, [NotNull] string unit, [CanBeNull] [Remainder] string note = null)
        {
            if (amount < 0)
                await TypingReplyAsync("You cannot demand a negative amount!");

            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var id = unchecked((uint)_random.Next()).MeaninglessString();

                await _database.InsertUnconfirmedPayment(Context.User.Id, debter.Id, amount, unit, note, id);
                await TypingReplyAsync($"{debter.Mention} type `!confirm {id}` to confirm that you owe this");
                await TypingReplyAsync($"{debter.Mention} type `!deny {id}` to deny this request. Please talk to the other user about why!");
            }
        }
        
        [Command("pay"), Summary("I will record that you have paid someone else some money")]
        public async Task CreatePendingPayment([NotNull] IUser receiver, decimal amount, [NotNull] string unit, [CanBeNull] [Remainder] string note = null)
        {
            if (amount < 0)
                await TypingReplyAsync("You cannot pay a negative amount!");

            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var id = unchecked((uint)_random.Next()).MeaninglessString();

                await _database.InsertUnconfirmedPayment(Context.User.Id, receiver.Id, amount, unit, note, id);
                await TypingReplyAsync($"{receiver.Mention} type `!confirm {id}` to confirm that you have received this payment");
            }
        }

        [Command("confirm"), Summary("I will record that you confirm the pending transaction")]
        public async Task ConfirmPendingPayment(string id)
        {
            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var result = await _database.ConfirmPending(id, Context.User.Id);

                if (result.HasValue)
                    await ReplyAsync($"{Context.User.Mention} Confirmed transaction of {FormatCurrency(result.Value.Amount, result.Value.Unit)} from {Context.Client.GetUser(result.Value.PayerId).Mention} to {Context.Client.GetUser(result.Value.ReceiverId).Mention}");
                else
                    await ReplyAsync($"{Context.User.Mention} I can't find a pending payment with that ID");
            }
        }

        [Command("deny"), Summary("I will record that you denied the pending transaction")]
        public async Task DenyPendingPayment(string id)
        {
            await CheckDebugger();

            using (Context.Channel.EnterTypingState())
            {
                var result = await _database.DenyPending(id, Context.User.Id);

                if (result.HasValue)
                {
                    var note = string.IsNullOrWhiteSpace(result.Value.Note) ? "" : $" '{result.Value.Note}";
                    await ReplyAsync($"{Context.User.Mention} *Denied* transaction of {FormatCurrency(result.Value.Amount, result.Value.Unit)} from {Context.Client.GetUser(result.Value.PayerId).Mention} to {Context.Client.GetUser(result.Value.ReceiverId).Mention} {note}");
                }
                else
                    await ReplyAsync($"{Context.User.Mention} I can't find a pending payment with that ID");
            }
        }

        [Command("pending"), Summary("I will list all pending transactions to or from you")]
        public async Task ListPendingPayments()
        {
            try
            {
                await ListPendingPaymentsForUser(Context.User);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        [Command("pending"), Summary("I will list all pending transactions to or from another user"), RequireOwner]
        public async Task ListPendingPaymentsForUser([NotNull] IUser user)
        {
            await ListPendingPaymentsFromUser(user);
            await ListPendingPaymentsToUser(user);
        }

        [Command("pending-out"), Summary("I will list all pending transactions another user has yet to confirm"), RequireOwner]
        public async Task ListPendingPaymentsFromUser([NotNull] IUser user)
        {
            await CheckDebugger();

            var pending = (await _database.GetPendingForReceiver(user.Id)).ToArray();

            if (pending.Length == 0)
                await TypingReplyAsync("No pending outgoing transactions");
            else
                await PaginatedPending(pending, "You have {0} payments to confirm. Type `!confirm $id` to confirm that it has happened or `!deny $id` otherwise", false);;
        }

        [Command("pending-in"), Summary("I will list all pending transactions another user has yet to confirm"), RequireOwner]
        public async Task ListPendingPaymentsToUser([NotNull] IUser user)
        {
            await CheckDebugger();

            var pending = (await _database.GetPendingForSender(user.Id)).ToArray();

            if (pending.Length == 0)
                await TypingReplyAsync("No pending incoming transactions");
            else
                await PaginatedPending(pending, "There are {0} unconfirmed payments to you. The other person should type `!confirm $id` or `!deny $id` to confirm or deny that the payment has happened", true);
        }
        #endregion

        #region transaction list
        [Command("transactions"), Summary("I will show all your transactions, optionally filtered to only with another user")]
        public async Task ListTransactions([CanBeNull] IUser other = null)
        {
            await CheckDebugger();

            var tsx = (await (other == null
                ? _database.GetTransactions(Context.Message.Author.Id)
                : _database.GetTransactions(Context.Message.Author.Id, other.Id))).ToArray();

            if (tsx.Length == 0)
                await TypingReplyAsync("No transactions");
            else
                await PaginatedTransactions(tsx);
        }
        #endregion

        #region helpers
        private async Task CheckDebugger()
        {
            if (Debugger.IsAttached)
                await ReplyAsync("**Warning - Debugger is attached. This is likely not the main version of mute!**");
        }

        private string Name(ulong id, bool mention = false)
        {
            var user = _client.GetUser(id);

            if (user == null)
                return $"?{id}?";

            if (user is IGuildUser gu)
            {
                if (mention)
                    return gu.Mention;
                else
                    return gu.Nickname;
            }

            return user.Mention;
        }

        private async Task PaginatedTransactions([NotNull] IEnumerable<Owed> owed, string connector = "➜", bool lenderFirst = true, bool showTotals = true)
        {
            string FormatSingleTsx(Owed d)
            {
                var borrower = Name(d.BorrowerId);
                var lender = Name(d.LenderId);
                var note = string.IsNullOrEmpty(d.Note) ? "" : $"'{d.Note}'";

                return $"{(lenderFirst ? lender : borrower)} {connector} {(lenderFirst ? borrower : lender)} {FormatCurrency(d.Amount, d.Unit)} {note}";
            }

            async Task DebtTotalsPerCurrency()
            {
                if (owed.Count() > 1)
                {
                    var totals = owed.GroupBy(a => a.Unit)
                                     .Select(a => (a.Key, a.Sum(o => o.Amount)))
                                     .ToArray();

                    await TypingReplyAsync("Totals:");
                    foreach (var (currency, total) in totals)
                        await TypingReplyAsync(" - " + FormatCurrency(total, currency));
                }
            }

            var owedArr = owed.ToArray();

            //If the number of transactions is small, display them all.
            //Otherwise batch and show them in pages
            if (owedArr.Length < 10)
                await ReplyAsync(string.Join("\n", owedArr.Select(FormatSingleTsx)));
            else
                await PagedReplyAsync(new PaginatedMessage {Pages = owedArr.Batch(7).Select(d => string.Join("\n", d.Select(FormatSingleTsx)))});

            await DebtTotalsPerCurrency();
        }

        private async Task PaginatedPending([NotNull] IEnumerable<Pending> pending, string paginatedHeader, bool mentionReceiver)
        {
            string FormatSinglePending(Pending p, bool longForm)
            {
                var receiver = Name(p.ReceiverId, mentionReceiver);
                var payer = Name(p.PayerId);
                var note = string.IsNullOrEmpty(p.Note) ? "" : $"'{p.Note}'";
                var amount = FormatCurrency(p.Amount, p.Unit);

                if (longForm)
                    return $"{receiver} Type `!confirm {p.Id}` or `!deny {p.Id}` to confirm/deny transaction of {amount} from {payer} {note}";
                else
                    return $"`{p.Id}`: {payer} paid {amount} to {receiver} {note}";
            }

            var pendingArr = pending.ToArray();

            //If the number of transactions is small, display them all.
            //Otherwise batch and show them in pages
            if (pendingArr.Length < 0)
                await ReplyAsync(string.Join("\n", pendingArr.Select(p => FormatSinglePending(p, true))));
            else
            {
                await TypingReplyAsync(string.Format(paginatedHeader, pendingArr.Length));
                await PagedReplyAsync(new PaginatedMessage {Pages = pendingArr.Batch(5).Select(d => string.Join("\n", d.Select(p => FormatSinglePending(p, false))))});
            }
        }

        /// <summary>
        /// Generate a human readable string to represent the given amount/currency pair
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="unit"></param>
        /// <returns></returns>
        [NotNull] private static string FormatCurrency(decimal amount, [NotNull] string unit)
        {
            var sym = unit.TryGetCurrencySymbol();

            if (unit == sym)
                return $"{amount}({unit.ToUpperInvariant()})";
            else
                return $"{sym}{amount}";
        }
        #endregion
    }
}
