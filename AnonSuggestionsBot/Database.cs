/*
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

        public struct pollButtonResponse {
            public string poll_uid;
            public int option_num;
            public string serverID;
        }
        
        public async void login(string IP, string password) {
            var connectionString = string.Format("Host={0};Username=postgres;Password={1};Database=postgres;Keepalive=1", IP, password);
            dataSource = NpgsqlDataSource.Create(connectionString);
        }

        public async void createServerEntry(ulong serverID, string inputChannel, string outputChannel, string? loggingChannel = null) {
            await using var getCurrentDiscordIdInitialized = dataSource.CreateCommand(string.Format("select count(server_id) from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", serverID.ToString()));
            await using var getCurrentDiscordIdInitializedReader = await getCurrentDiscordIdInitialized.ExecuteReaderAsync();

            string update = "";
            if(loggingChannel != null) { update = string.Format("update \"AnonSuggestionsBot\".servers set discord_submit_channel = '{0}', discord_post_channel = '{1}', discord_log_channel = '{2}' where discord_id like '{3}';", inputChannel, outputChannel, loggingChannel, serverID.ToString()); }
            else { update = string.Format("update \"AnonSuggestionsBot\".servers set discord_submit_channel = '{0}', discord_post_channel = '{1}', discord_log_channel = NULL where discord_id like '{2}';", inputChannel, outputChannel, serverID.ToString()); }

            string create = "";
            if (loggingChannel != null) { create = string.Format("insert into \"AnonSuggestionsBot\".servers (discord_id, discord_submit_channel, discord_post_channel, discord_log_channel) values ({0}, '{1}', '{2}', '{3}');", serverID.ToString(), inputChannel, outputChannel, loggingChannel); }
            else { create = string.Format("insert into \"AnonSuggestionsBot\".servers (discord_id, discord_submit_channel, discord_post_channel) values ({0}, '{1}', '{2}');", serverID.ToString(), inputChannel, outputChannel); }

            while (getCurrentDiscordIdInitializedReader.Read()) {
                if (getCurrentDiscordIdInitializedReader.GetInt32(0) > 0) {
                    await using var deleteOldDiscordEntry = dataSource.CreateCommand(update);
                    await deleteOldDiscordEntry.ExecuteReaderAsync();
                }
                else {
                    await using var createCurrentDiscordEntry = dataSource.CreateCommand(create);
                    await createCurrentDiscordEntry.ExecuteReaderAsync();
                }
            }
        }

        private async Task i_updateBans(ulong serverID) {
            string query = string.Format("update \"AnonSuggestionsBot\".bans set ban_expired = true where server_id like '{0}' and ban_perma = false and not (ban_time + (ban_duration * interval '1 minute')) >= now();", serverID.ToString());
            await using var updateBans = dataSource.CreateCommand(query);
            await updateBans.ExecuteReaderAsync();
        }

        public async Task<ulong> getServerOutputChannel(ulong serverID) {
            await using var getOutputChannel = dataSource.CreateCommand(string.Format("select discord_post_channel from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", serverID));
            await using var getOutputChannelReader = await getOutputChannel.ExecuteReaderAsync();

            while (getOutputChannelReader.Read()) {
                if (getOutputChannelReader.IsDBNull(0)) { return 0; }
                return ulong.Parse(getOutputChannelReader.GetString(0));
            }

            return 0;
        }

        public async Task<ulong> getServerLoggingChannel(ulong serverID) {
            await using var getOutputChannel = dataSource.CreateCommand(string.Format("select discord_log_channel from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", serverID));
            await using var getOutputChannelReader = await getOutputChannel.ExecuteReaderAsync();

            while (getOutputChannelReader.Read()) {
                if (getOutputChannelReader.IsDBNull(0)) { return 0; }
                return ulong.Parse(getOutputChannelReader.GetString(0));
            }

            return 0;
        }

        public async Task<string> getBotToken(bool beta) {
            await using var getBotToken = dataSource.CreateCommand(string.Format("select bot_token from \"AnonSuggestionsBot\".config where version like '{0}'", beta == false ? "LIVE" : "BETA"));
            await using var getBotTokenReader = await getBotToken.ExecuteReaderAsync();

            while (getBotTokenReader.Read()) {
                if (getBotTokenReader.IsDBNull(0)) { return ""; }
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

        public async void setSuggestionMessageId(ulong serverID, ulong messageID, string suggestion_uid) {
            string query = string.Format("update \"AnonSuggestionsBot\".submissions set message_id = '{0}' where server_id like '{1}' and suggestion_uid like '{2}';", messageID.ToString(), serverID.ToString(), suggestion_uid);
            await using var setSuggestionMessageId = dataSource.CreateCommand(query);
            await setSuggestionMessageId.ExecuteReaderAsync();
        }

        public async void createBanEntry(ulong serverID, string user_hash, string suggestion_UID, int length_minutes, bool perma) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".bans (server_id, user_hash, ban_time, ban_suggestion, ban_duration, ban_perma) values ('{0}', '{1}', '{2}', '{3}', '{4}', '{5}');", serverID.ToString(), user_hash, DateTime.Now.ToString(), suggestion_UID, length_minutes, perma);
            await using var createBanEntry = dataSource.CreateCommand(query);
            await createBanEntry.ExecuteReaderAsync();
        }

        public async Task<string> getUserHashFromSuggestionUID(ulong serverID, string suggestion_UID) {
            string query = string.Format("select user_hash from \"AnonSuggestionsBot\".submissions where server_id like '{0}' and suggestion_uid like '{1}';", serverID.ToString(), suggestion_UID);
            await using var getHashQuery = dataSource.CreateCommand(query);
            await using var getHashQueryReader = await getHashQuery.ExecuteReaderAsync();
            while (getHashQueryReader.Read()) {
                if (getHashQueryReader.IsDBNull(0)) { return ""; }
                return getHashQueryReader.GetString(0);
            }
            return "";
        }

        public async Task<int> checkUserBanned(ulong serverID, string user_hash) {
            await i_updateBans(serverID);

            string query = string.Format("select * from \"AnonSuggestionsBot\".bans where server_id like '{0}' and user_hash like '{1}' and (ban_expired != true);", serverID.ToString(), user_hash);
            await using var checkUserBanned = dataSource.CreateCommand(query);
            await using var checkUserBannedReader = await checkUserBanned.ExecuteReaderAsync();

            DateTime now = DateTime.Now;

            int minutesUntilNextUnban = 0;
            bool banned = false;

            while (checkUserBannedReader.Read()) {
                DateTime banTime = checkUserBannedReader.GetDateTime(4);
                int banDuration = checkUserBannedReader.GetInt32(5);
                bool banPerma = checkUserBannedReader.GetBoolean(6);

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

        public async Task<string[]> getSuggestionFromUid(ulong serverID, string suggestion_UID) {
            string query = string.Format("select suggestion_time, suggestion_title, suggestion_text, message_id from \"AnonSuggestionsBot\".submissions where server_id like '{0}' and suggestion_uid like '{1}'", serverID.ToString(), suggestion_UID);
            await using var getSuggestionFromUid = dataSource.CreateCommand(query);
            await using var getSuggestionFromUidReader = await getSuggestionFromUid.ExecuteReaderAsync();

            string[] response = {"", "", "", ""};

            while (getSuggestionFromUidReader.Read()) {
                response[0] = getSuggestionFromUidReader.GetDateTime(0).ToString();
                response[1] = getSuggestionFromUidReader.GetString(1);
                response[2] = getSuggestionFromUidReader.GetString(2);
                response[3] = getSuggestionFromUidReader.GetString(3);
                return response;
            }

            return response;
        }

        public async Task<int[]> getUserPreviousBans(ulong serverID, string user_hash) { 
            string query = string.Format("select count(user_hash) as total, count(distinct ban_suggestion) as dist from \"AnonSuggestionsBot\".bans where server_id like '{0}' and user_hash like '{1}';", serverID.ToString(), user_hash);
            await using var getUserPreviousBans = dataSource.CreateCommand(query);
            await using var getUserPreviousBansReader = await getUserPreviousBans.ExecuteReaderAsync();

            int[] response = { -1, -1 };
            
            while (getUserPreviousBansReader.Read()) {
                response[0] = getUserPreviousBansReader.GetInt32(0);
                response[1] = getUserPreviousBansReader.GetInt32(1);
            }

            return response;
        }

        public async Task setBanExpired(ulong serverID, string userHash) {
            string query = string.Format("update \"AnonSuggestionsBot\".bans set ban_expired = true where server_id like '{0}' and user_hash like '{1}';", serverID.ToString(), userHash);
            await using var unbanUser = dataSource.CreateCommand(query);
            await unbanUser.ExecuteReaderAsync();
        }

        public async Task setServerSlowMode(ulong serverID, int slowmodeMinutes) {
            string query = string.Format("update \"AnonSuggestionsBot\".servers set discord_slowmode = '{0}' where discord_id like '{1}';", slowmodeMinutes, serverID.ToString());
            await using var setServerSlowMode = dataSource.CreateCommand(query);
            await setServerSlowMode.ExecuteReaderAsync();
        }

        public async Task<DateTime> getUserLastSuggestionTime(ulong serverID, string user_hash) {
            string query = string.Format("select suggestion_uid, suggestion_time from \"AnonSuggestionsBot\".submissions where suggestion_time = (select max(suggestion_time) from \"AnonSuggestionsBot\".submissions where server_id like '{0}' and user_hash like '{1}')", serverID.ToString(), user_hash);
            await using var getUserLastSuggestionTime = dataSource.CreateCommand(query);
            await using var getUserLastSuggestionTimeReader = await getUserLastSuggestionTime.ExecuteReaderAsync();

            while (getUserLastSuggestionTimeReader.Read()) {
                if (getUserLastSuggestionTimeReader.IsDBNull(0)) { return new DateTime(0); }
                return getUserLastSuggestionTimeReader.GetDateTime(1);
            }

            return new DateTime(0);
        }

        public async Task<int> getServerSlowMode(ulong serverID) {
            string query = string.Format("select discord_slowmode from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", serverID.ToString());
            await using var getServerSlowMode = dataSource.CreateCommand(query);
            await using var getServerSlowModeReader = await getServerSlowMode.ExecuteReaderAsync();
            
            while (getServerSlowModeReader.Read()) {
                if (getServerSlowModeReader.IsDBNull(0)) { return 0; }
                return getServerSlowModeReader.GetInt32(0);
            }

            return 0;
        }

        public async Task<string> getServerPostText(ulong serverID) {
            string query = string.Format("select discord_post_text from \"AnonSuggestionsBot\".servers where discord_id like '{0}'", serverID.ToString());
            await using var getServerPostText = dataSource.CreateCommand(query);
            await using var getServerPostTextReader = await getServerPostText.ExecuteReaderAsync();

            string def = "Click here to submit a suggestion:";

            while (getServerPostTextReader.Read()) {
                if (getServerPostTextReader.IsDBNull(0)) { return def; }
                return getServerPostTextReader.GetString(0);
            }

            return def;
        }

        public async Task<string> getPollTitle(ulong serverID, string poll_uid) {
            string query = string.Format("select poll_title from \"AnonSuggestionsBot\".POLLS P where server_id like '{0}' and poll_uid like '{1}'", serverID.ToString(), poll_uid);
            await using var getPollTitle = dataSource.CreateCommand(query);
            await using var getPollTitleReader = await getPollTitle.ExecuteReaderAsync();

            string def = "Poll";

            while (getPollTitleReader.Read()) {
                if (getPollTitleReader.IsDBNull(0)) { return def; }
                return getPollTitleReader.GetString(0);
            }

            return def;
        }

        private async Task<bool> i_addPollOption(string poll_uid, string option, string button_uid, int option_index) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".poll_options (poll_uid, option_num, option_text, button_uid) values ('{0}', '{1}', '{2}', '{3}')", poll_uid, option_index, option, button_uid);
            await using var addPollOption = dataSource.CreateCommand(query);
            await addPollOption.ExecuteReaderAsync();
            return true;
        }

        public async Task<bool> createNewPoll(pollDataPackage PDP) {
            if (PDP.options.Count() < 2) { return false; }

            string query = string.Format("insert into \"AnonSuggestionsBot\".polls (server_id, user_hash, poll_uid, poll_title) values ('{0}', '{1}', '{2}', '{3}')", PDP.guildId.ToString(), PDP.userHash, PDP.pollUid, PDP.title);
            await using var createNewPoll = dataSource.CreateCommand(query);
            await createNewPoll.ExecuteReaderAsync();

            foreach(pollOption po in PDP.options) {
                await i_addPollOption(PDP.pollUid, po.text, po.button, po.ID);
            }

            await i_addPollOption(PDP.pollUid, "RESULTS", PDP.buttonUidResults, 10);
            await i_addPollOption(PDP.pollUid, "END", PDP.buttonUidEnd, 11);
            
            return true;
        }

        public async void setPollMessageId(ulong serverID, string poll_uid, ulong message_id) {
            string query = string.Format("update \"AnonSuggestionsBot\".POLLS set message_id = '{0}' where server_id like '{1}' and poll_uid like '{2}'", message_id.ToString(), serverID.ToString(), poll_uid);
            await using var updatePollMessageId = dataSource.CreateCommand(query);
            await updatePollMessageId.ExecuteReaderAsync();
        }

        public async Task<pollButtonResponse> getClickedPollButton(ulong serverID, string button_uid) {
            string query = string.Format("select \"AnonSuggestionsBot\".POLL_OPTIONS.poll_uid, \"AnonSuggestionsBot\".POLL_OPTIONS.option_num, \"AnonSuggestionsBot\".POLLS.server_id from \"AnonSuggestionsBot\".POLL_OPTIONS inner join \"AnonSuggestionsBot\".POLLS on POLL_OPTIONS.poll_uid like POLLS.poll_uid\r\nwhere \"AnonSuggestionsBot\".POLLS.server_id like '{0}' and \"AnonSuggestionsBot\".POLL_OPTIONS.button_uid like '{1}';", serverID.ToString(), button_uid);
            await using var getClickedPollButton = dataSource.CreateCommand(query);
            await using var getClickedPollButtonReader = await getClickedPollButton.ExecuteReaderAsync();

            pollButtonResponse response = new pollButtonResponse();
            response.poll_uid = "";
            response.option_num = -1;

            while (getClickedPollButtonReader.Read()) {
                if (getClickedPollButtonReader.IsDBNull(0)) { return response; }
                response.poll_uid = getClickedPollButtonReader.GetString(0);
                response.option_num = getClickedPollButtonReader.GetInt32(1);
                response.serverID = getClickedPollButtonReader.GetString(2);
            }

            return response;
        }

        public async Task<bool> hasUserVotedOnPoll(ulong serverID, string poll_uid, string user_hash) {
            string query = string.Format("select \"AnonSuggestionsBot\".POLL_VOTE.vote_pk from \"AnonSuggestionsBot\".POLL_VOTE inner join \"AnonSuggestionsBot\".POLLS on POLL_VOTE.poll_uid like POLLS.poll_uid where \"AnonSuggestionsBot\".POLLS.server_id like '{0}' and \"AnonSuggestionsBot\".POLL_VOTE.user_hash like '{1}' and \"AnonSuggestionsBot\".POLL_VOTE.poll_uid like '{2}';", serverID.ToString(), user_hash, poll_uid);
            await using var hasUserVotedOnPoll = dataSource.CreateCommand(query);
            await using var hasUserVotedOnPollReader = await hasUserVotedOnPoll.ExecuteReaderAsync();
            while (hasUserVotedOnPollReader.Read()) {
                if (hasUserVotedOnPollReader.IsDBNull(0)) { return false; }
                return true;
            }
            return false;
        }

        public async void voteOnPoll(ulong serverID, string poll_uid, string user_hash, int option) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".POLL_VOTE (poll_uid, user_hash, option_num) values ('{0}', '{1}', '{2}');", poll_uid, user_hash, option);
            await using var voteOnPoll = dataSource.CreateCommand(query);
            await voteOnPoll.ExecuteReaderAsync();
        }
        
        public async Task<int[]> getValidPollOptions(ulong serverID, string poll_uid) {
            string query = string.Format("select \"AnonSuggestionsBot\".POLL_OPTIONS.option_num from \"AnonSuggestionsBot\".POLL_OPTIONS\r\ninner join \"AnonSuggestionsBot\".POLLS on POLL_OPTIONS.poll_uid like POLLS.poll_uid where \"AnonSuggestionsBot\".POLLS.server_id like '{0}' and \"AnonSuggestionsBot\".POLL_OPTIONS.poll_uid like '{1}' and \"AnonSuggestionsBot\".POLL_OPTIONS.option_num not in (10, 11);", serverID.ToString(), poll_uid);
            await using var getValidPollOptions = dataSource.CreateCommand(query);
            await using var getValidPollOptionsReader = await getValidPollOptions.ExecuteReaderAsync();

            List<int> validOptions = new List<int>();

            while (getValidPollOptionsReader.Read()) {
                if (getValidPollOptionsReader.IsDBNull(0)) { return validOptions.ToArray(); }
                validOptions.Add(getValidPollOptionsReader.GetInt32(0));
            }

            return validOptions.ToArray();
        }
        
        public async Task<pollResultsPackage> getPollResults(ulong serverID, string poll_uid) {
            pollResultsPackage PRP = new pollResultsPackage();
            string query = string.Format("select \"AnonSuggestionsBot\".POLL_VOTE.option_num, count(\"AnonSuggestionsBot\".POLL_VOTE.option_num) as cnt from \"AnonSuggestionsBot\".POLL_VOTE\r\ninner join \"AnonSuggestionsBot\".POLLS on POLL_VOTE.poll_uid like POLLS.poll_uid where \"AnonSuggestionsBot\".POLLS.server_id like '{0}' and \"AnonSuggestionsBot\".POLL_VOTE.poll_uid like '{1}' group by option_num;", serverID.ToString(), poll_uid);
            await using var getPollResults = dataSource.CreateCommand(query);
            await using var getPollResultsReader = await getPollResults.ExecuteReaderAsync();

            while (getPollResultsReader.Read()) {
                int ID = getPollResultsReader.GetInt32(0);
                int COUNT = getPollResultsReader.GetInt32(1);
                switch (ID) {
                    case 0:
                        PRP.countA = COUNT;
                        break;
                    case 1:
                        PRP.countB = COUNT;
                        break;
                    case 2:
                        PRP.countC = COUNT;
                        break;
                    case 3:
                        PRP.countD = COUNT;
                        break;
                }
            }

            return PRP;
        }

        //public async Task<bool> closePoll(ulong serverID, string user_hash, string poll_uid) {

        //}
    }
}
