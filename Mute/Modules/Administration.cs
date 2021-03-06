﻿using System;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using JetBrains.Annotations;
using Mute.Extensions;
using Mute.Services;
using Mute.Services.Responses;

namespace Mute.Modules
{
    [Group]
    [RequireOwner]
    public class Administration
        : BaseModule
    {
        private readonly DiscordSocketClient _client;
        private readonly IDatabaseService _database;
        private readonly ConversationalResponseService _conversations;

        public Administration( DiscordSocketClient client, IDatabaseService database, ConversationalResponseService conversations)
        {
            _client = client;
            _database = database;
            _conversations = conversations;
        }

        [Command("say"), Summary("I will say whatever you want, but I won't be happy about it >:(")]
        [RequireOwner]
        public async Task Say([NotNull] string message, IMessageChannel channel = null)
        {
            if (channel == null)
                channel = Context.Channel;

            await channel.TypingReplyAsync(message);
        }

        [Command("sql"), Summary("I will execute an arbitrary SQL statement. Please be very careful x_x")]
        [RequireOwner]
        public async Task Sql([Remainder] string sql)
        {
            using (var result = await _database.ExecReader(sql))
                await TypingReplyAsync($"SQL affected {result.RecordsAffected} rows");
        }

        [Command("conversation-status"), Summary("I will show the status of my current conversation with a user")]
        public async Task ConversationState([CanBeNull] IGuildUser user = null)
        {
            if (user == null)
                user = Context.Message.Author as IGuildUser;

            if (user == null)
                await TypingReplyAsync("No user!");
            else
            {
                var c = _conversations.GetConversation(user);
                if (c == null)
                    await TypingReplyAsync("No active conversation");
                else if (c.IsComplete)
                    await TypingReplyAsync($"Conversation is complete `{c.GetType()}`");
                else
                {
                    await TypingReplyAsync($"Conversation is active `{c.GetType()}`...");
                    await ReplyAsync(c.ToString());
                }
            }
        }

        [Command("presence"), Summary("I will set my presence")]
        public async Task SetPresence(ActivityType activity, [CanBeNull, Remainder] string presence)
        {
            if (!string.IsNullOrEmpty(presence))
                await _client.SetActivityAsync(new Game(presence, activity));
        }

        [Command("status"), Summary("I will set my status")]
        public async Task SetPresence(UserStatus status)
        {
            await _client.SetStatusAsync(status);
        }

        [Command("kill"), Alias("die", "self-destruct", "terminate"), Summary("I will immediately terminate my process ⊙︿⊙")]
        public async Task Kill(int exitCode = -1)
        {
            Environment.Exit(exitCode);
        }
    }
}
