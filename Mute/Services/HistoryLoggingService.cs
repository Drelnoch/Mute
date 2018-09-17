﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using JetBrains.Annotations;
using Mute.Extensions;

namespace Mute.Services
{
    public class HistoryLoggingService
    {
        private const string InsertMessageSql = "INSERT OR IGNORE into ChatLog (Uid, UtcTime, ChannelId, Content, UserId) values(@Uid, @UserId, @ChannelId, @Content, @UtcTime)";
        private const string InsertMonitorSql = "INSERT OR IGNORE into ChatLogMonitoring (ChannelId, GuildId) values(@ChannelId, @GuildId)";

        private const string SelectMonitorSql = "SELECT * From ChatLogMonitoring";

        private const string SelectByUser = "SELECT Uid From ChatLog WHERE UserId = @UserId";
        private const string SelectById = "SELECT Content, ChannelId, GuildId From ChatLog WHERE Uid = @Uid";
        private const string SelectByTimeRange = "SELECT Content, ChannelId, GuildId From ChatLog WHERE UtcTime < @Max and UtcTime > @Min";

        [NotNull] private readonly DatabaseService _database;
        [NotNull] private readonly DiscordSocketClient _client;

        private readonly ConcurrentDictionary<ulong, bool> _subscriptions = new ConcurrentDictionary<ulong, bool>();

        public HistoryLoggingService([NotNull] DatabaseService database, [NotNull] DiscordSocketClient client)
        {
            _database = database;
            _client = client;

            // Create database structure
            try
            {
                _database.Exec("CREATE TABLE IF NOT EXISTS `ChatLog` (`UID` TEXT NOT NULL PRIMARY KEY, `UtcTime` TEXT NOT NULL, `ChannelId` TEXT NOT NULL, `Content` TEXT NOT NULL, `UserId` TEXT NOT NULL)");
                _database.Exec("CREATE TABLE IF NOT EXISTS `ChatLogMonitoring` (`ChannelId` TEXT NOT NULL, `GuildId` TEXT NOT NULL, PRIMARY KEY(ChannelId, GuildId))");

                _database.Exec("CREATE INDEX IF NOT EXISTS `ChatLogByUserId` ON `ChatLog` ( `UserId` DESC, `UtcTime` DESC, `ChannelId` DESC, `UID` DESC, `Content` DESC )");
                _database.Exec("CREATE INDEX IF NOT EXISTS `ChatLogByTime` ON `ChatLog` ( `UtcTime` DESC, `ChannelId` DESC, `UserId` DESC, `UID` DESC, `Content` DESC )");
                _database.Exec("CREATE INDEX IF NOT EXISTS `ChatLogByChannelId` ON `ChatLog` ( `ChannelId` DESC, `UID` DESC, `UtcTime` DESC, `UserId` DESC, `Content` DESC )");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            // Subscribe to message events
            _client.MessageReceived += OnMessageReceived;

            // Get the channels we should subscribe to
            _client.Ready += () => Task.Factory.StartNew(SetupChannelLoggingFromDatabase);
        }

        private async Task SetupChannelLoggingFromDatabase()
        {
            try
            {
                using (var cmd = _database.CreateCommand())
                {
                    cmd.CommandText = SelectMonitorSql;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var cid = ulong.Parse((string)reader["ChannelId"]);
                            var gid = ulong.Parse((string)reader["GuildId"]);
                            var guild = _client.GetGuild(gid);
                            if (guild == null)
                            {
                                Console.WriteLine($"Failed to initialize channel history logging, guild:{gid} is null");
                            }
                            else
                            {
                                if (guild.GetChannel(cid) is ITextChannel channel)
                                {
                                    Console.WriteLine($"Initializing logging of channel: {channel.Name}");
                                    await BeginMonitoring(channel);
                                }
                                else
                                {
                                    Console.Write($"Failed to initialize channel history logging, channel:{cid} is null");
                                }
                            }
                        }
                    }
                }

                Console.WriteLine("Finished initializing channel logging");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private async Task OnMessageReceived([NotNull] SocketMessage message)
        {
            if (_subscriptions.ContainsKey(message.Channel.Id))
                await InsertMessage(message);
        }

        /// <summary>
        /// Start logging all messages in the given channel to database
        /// </summary>
        /// <param name="channel">Channel to log</param>
        /// <param name="initialScrapeMax">Amount of history to grab right now</param>
        /// <returns></returns>
        public async Task BeginMonitoring([NotNull] ITextChannel channel, int initialScrapeMax = int.MaxValue)
        {
            // Subscribe to updates for the channel
            _subscriptions.AddOrUpdate(channel.Id, true, (_, __) => true);

            // Insert a record into the database indicating that we should log a channel
            Console.WriteLine($"Monitoring channel {channel.Name}");
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = InsertMonitorSql;
                cmd.Parameters.Add(new SQLiteParameter("@ChannelId", System.Data.DbType.String) { Value = channel.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@GuildId", System.Data.DbType.String) { Value = channel.Guild.Id.ToString() });

                await cmd.ExecuteNonQueryAsync();
            }

            // Scrape all the history of this channel. Keep going until the scrape gets no new messages (max of 10 repeats)
            Console.WriteLine("Scraping channel history...");
            var totalMsg = 0;
            for (var i = 0; i < 10; i++)
            {
                //Get all channel messages starting from the latest
                var n = await ScrapeChannel(channel, start: null, max: initialScrapeMax, breakOnOverwrite: true);
                totalMsg += n;
                Console.WriteLine($"Scraped {n} new messages from {channel.Name} history");

                //If we got no new messages break out
                if (n == 0)
                    break;
            }
            Console.WriteLine($"Scraped a total of {totalMsg} new messages from {channel.Name} history");
        }

        private async Task<int> ScrapeChannel([NotNull] ITextChannel channel, IMessage start = null, int max = 1024, [CanBeNull] IProgress<int> progress = null, bool breakOnOverwrite = false)
        {
            //If start message is not set then get the latest message in the channel now
            if (start == null)
                start = (await channel.GetMessagesAsync(1).FlattenAsync()).SingleOrDefault();

            //If message is still null that means we failed to get a start message (no messages in channel?)
            if (start == null)
                return 0;

            var counter = 0;
            var counterNew = 0;
            while (counter < max)
            {
                //Get some messages
                const int batchSize = 100;
                var messages = (await channel.GetMessagesAsync(start, Direction.Before, batchSize).FlattenAsync()).ToArray();
                foreach (var message in messages)
                {
                    //Insert message into the database
                    var n = await InsertMessage(message);
                    if (n == 0 && breakOnOverwrite)
                        return counterNew;
                    else
                        counterNew += n;

                    //Update counter for how many messages we have logged
                    counter++;
                    progress?.Report(counter);

                    //Break out once we've scraped enough messages
                    if (counter >= max)
                        break;

                    //Update the "start" message to be the oldest message we've encountered, so the search moves backwards
                    start = message.CreatedAt < start.CreatedAt ? message : start;
                }

                //If the number we fetched is less than a batch it means that there are no older messages to fetch
                if (messages.Length < batchSize)
                    break;
            }

            return counterNew;
        }

        private async Task<int> InsertMessage([NotNull] IMessage message)
        {
            var time = message.Timestamp.UtcDateTime.UnixTimestamp();

            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = InsertMessageSql;
                cmd.Parameters.Add(new SQLiteParameter("@Uid", System.Data.DbType.String) { Value = message.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@UserId", System.Data.DbType.String) { Value = message.Author.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@ChannelId", System.Data.DbType.String) { Value = message.Channel.Id.ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@Content", System.Data.DbType.String) { Value = message.Content });
                cmd.Parameters.Add(new SQLiteParameter("@UtcTime", System.Data.DbType.String) { Value = time.ToString() });

                return await cmd.ExecuteNonQueryAsync();
            }
        }

        #region query
        [ItemCanBeNull] public async Task<string> MessageContent(ulong id)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = SelectById;
                cmd.Parameters.Add(new SQLiteParameter("@Uid", System.Data.DbType.String) {Value = id.ToString()});

                var reader = await cmd.ExecuteReaderAsync();

                if (reader.HasRows)
                {
                    await reader.ReadAsync();
                    return (string)reader["Content"];
                }
                else
                    return null;
            }
        }

        [ItemCanBeNull] public async Task<IMessage> Message(ulong id)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = SelectById;
                cmd.Parameters.Add(new SQLiteParameter("@Uid", System.Data.DbType.String) {Value = id.ToString()});

                var reader = await cmd.ExecuteReaderAsync();

                if (reader.HasRows)
                {
                    await reader.ReadAsync();
                    var cid = ulong.Parse((string)reader["ChannelId"]);
                    var gid = ulong.Parse((string)reader["GuildId"]);

                    if (_client.GetGuild(gid)?.GetChannel(cid) is ITextChannel tch)
                        return await tch.GetMessageAsync(id);
                }
            }

            return null;
        }

        [ItemNotNull] private static async Task<IReadOnlyList<ulong>> ParseIds([NotNull] DbDataReader reader)
        {
            var results = new List<ulong> { Capacity = reader.RecordsAffected };

            while (await reader.ReadAsync())
            {
                var uid = ulong.Parse((string)reader["Uid"]);
                results.Add(uid);
            }

            return results;
        }

        [ItemNotNull] public async Task<IReadOnlyList<ulong>> MessagesByUser([NotNull] IUser user)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = SelectByUser;
                cmd.Parameters.Add(new SQLiteParameter("@UserId", System.Data.DbType.String) { Value = user.Id.ToString() });

                var reader = await cmd.ExecuteReaderAsync();

                return await ParseIds(reader);
            }
        }

        [ItemNotNull] public async Task<IReadOnlyList<ulong>> MessagesByTimeRange(DateTime min, DateTime max)
        {
            using (var cmd = _database.CreateCommand())
            {
                cmd.CommandText = SelectByTimeRange;
                cmd.Parameters.Add(new SQLiteParameter("@Max", System.Data.DbType.String) { Value = max.UnixTimestamp().ToString() });
                cmd.Parameters.Add(new SQLiteParameter("@Min", System.Data.DbType.String) { Value = min.UnixTimestamp().ToString() });

                var reader = await cmd.ExecuteReaderAsync();

                return await ParseIds(reader);
            }
        }
        #endregion
    }
}