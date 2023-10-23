﻿/*
 * This file is part of AnonSuggestionBot (https://github.com/nymda/AnonSuggestionsBot).
 * Copyright (c) 2023 github/nymda
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnonSuggestionsBot {
    public class Database {

        private NpgsqlDataSource dataSource;

        public async void login(string IP, string password) {
            var connectionString = string.Format("Host={0};Username=postgres;Password={1};Database=postgres", IP, password);
            dataSource = NpgsqlDataSource.Create(connectionString);
        }

        public async void createServerEntry(ulong discord_id, string inputChannel, string outputChannel) {
            await using var getCurrentDiscordIdInitialized = dataSource.CreateCommand(string.Format("select count(server_id) from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", discord_id.ToString()));
            await using var getCurrentDiscordIdInitializedReader = await getCurrentDiscordIdInitialized.ExecuteReaderAsync();

            while (getCurrentDiscordIdInitializedReader.Read()) {
                if (getCurrentDiscordIdInitializedReader.GetInt32(0) > 0) {
                    await using var deleteOldDiscordEntry = dataSource.CreateCommand(string.Format("update \"AnonSuggestionsBot\".servers set discord_submit_channel = '{0}', discord_post_channel = '{1}' where discord_id like '{2}';", inputChannel, outputChannel, discord_id.ToString()));
                    await deleteOldDiscordEntry.ExecuteReaderAsync();
                }
                else {
                    await using var createCurrentDiscordEntry = dataSource.CreateCommand(string.Format("insert into \"AnonSuggestionsBot\".servers (discord_id, discord_submit_channel, discord_post_channel) values ({0}, '{1}', '{2}');", discord_id.ToString(), inputChannel, outputChannel));
                    await createCurrentDiscordEntry.ExecuteReaderAsync();
                }
            }
        }

        public async Task<ulong> lookupServerOutputChannel(ulong discord_id) {
            await using var getOutputChannel = dataSource.CreateCommand(string.Format("select discord_post_channel from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", discord_id));
            await using var getOutputChannelReader = await getOutputChannel.ExecuteReaderAsync();

            while (getOutputChannelReader.Read()) {
                return ulong.Parse(getOutputChannelReader.GetString(0));
            }

            return 0;
        }

        public async Task<string> getBotToken() {
            await using var getBotToken = dataSource.CreateCommand("select bot_token from \"AnonSuggestionsBot\".config;");
            await using var getBotTokenReader = await getBotToken.ExecuteReaderAsync();

            while (getBotTokenReader.Read()) {
                return getBotTokenReader.GetString(0);
            }

            return "";
        }

        public async void logSuggestion(ulong serverID, string user_hash, string suggestion_uid, string suggestion_title, string suggestion_text) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".submissions (server_id, user_hash, suggestion_uid, suggestion_time, suggestion_title, suggestion_text) values ('{0}', '{1}', '{2}', '{3}', @s_title, @s_text);", serverID.ToString(), user_hash, suggestion_uid, DateTime.Now.ToString());
            await using var logSuggestion = dataSource.CreateCommand(query);

            var titleParam = logSuggestion.CreateParameter();
            titleParam.ParameterName = "s_title";
            titleParam.Value = suggestion_title;

            var textParam = logSuggestion.CreateParameter();
            textParam.ParameterName = "s_text";
            textParam.Value = suggestion_text;

            logSuggestion.Parameters.Add(titleParam);
            logSuggestion.Parameters.Add(textParam);

            await using var logSuggestionReader = await logSuggestion.ExecuteReaderAsync();
        }

        public async void createBanEntry(ulong serverID, string user_hash, int length_minutes, bool perma) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".bans (server_id, user_hash, ban_time, ban_duration, ban_perma) values ('{0}', '{1}', '{2}', '{3}', '{4}');", serverID.ToString(), user_hash, DateTime.Now.ToString(), length_minutes, perma);
            await using var createBanEntry = dataSource.CreateCommand(query);
            await using var createBanEntryReader = await createBanEntry.ExecuteReaderAsync();
        }

        public async Task<string> getUserHashFromSuggestionUID(ulong serverID, string suggestion_UID) {
            string query = string.Format("select user_hash from \"AnonSuggestionsBot\".submissions where server_id like '{0}' and suggestion_uid like '{1}';", serverID.ToString(), suggestion_UID);
            await using var getHashQuery = dataSource.CreateCommand(query);
            await using var getHashQueryReader = await getHashQuery.ExecuteReaderAsync();
            while (getHashQueryReader.Read()) {
                return getHashQueryReader.GetString(0);
            }
            return "";
        }

        public async Task<int> checkUserBanned(ulong serverID, string userHash) {
            string query = string.Format("select * from \"AnonSuggestionsBot\".bans where server_id like '{0}' and user_hash like '{1}';", serverID.ToString(), userHash);
            await using var checkUserBanned = dataSource.CreateCommand(query);
            await using var checkUserBannedReader = await checkUserBanned.ExecuteReaderAsync();

            DateTime now = DateTime.Now;

            int minutesUntilNextUnban = 0;
            bool banned = false;

            while (checkUserBannedReader.Read()) {
                DateTime banTime = checkUserBannedReader.GetDateTime(3);
                int banDuration = checkUserBannedReader.GetInt32(4);
                bool banPerma = checkUserBannedReader.GetBoolean(5);

                DateTime banTimePlusDuration = banTime.AddMinutes(banDuration);

                int minutesToUnban = (int)Math.Round((banTimePlusDuration - now).TotalMinutes);

                if (banPerma) { return -2; }
                if (banTimePlusDuration > now && minutesToUnban > minutesUntilNextUnban) { 
                    minutesUntilNextUnban = minutesToUnban;
                    banned = true;
                }
            }

            if (banned) { return minutesUntilNextUnban; }
            return -1;
        }
    }
}
