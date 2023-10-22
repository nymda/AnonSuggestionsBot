using Microsoft.Extensions.Logging;
using Npgsql;
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
            selectSchema();
        }

        public async void selectSchema() {
            await using var command = dataSource.CreateCommand("SET schema 'AnonSuggestionsBot'");
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync()) {
                Console.WriteLine("DB RESPONSE: " + reader.GetString(0));
            }
        }

        public async void createServerEntry(ulong discord_id, string inputChannel, string outputChannel) {
            await using var getCurrentDiscordIdInitialized = dataSource.CreateCommand(string.Format("select count(server_id) from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", discord_id));
            await using var getCurrentDiscordIdInitializedReader = await getCurrentDiscordIdInitialized.ExecuteReaderAsync();

            while (getCurrentDiscordIdInitializedReader.Read()) {
                if (getCurrentDiscordIdInitializedReader.GetValue(0).ToString() != "0") {
                    await using var deleteOldDiscordEntry = dataSource.CreateCommand(string.Format("delete from \"AnonSuggestionsBot\".servers where discord_id like '{0}';", discord_id));
                    await deleteOldDiscordEntry.ExecuteReaderAsync();
                }

                await using var createCurrentDiscordEntry = dataSource.CreateCommand(string.Format("insert into \"AnonSuggestionsBot\".servers (discord_id, discord_submit_channel, discord_post_channel) values ({0}, '{1}', '{2}');", discord_id, inputChannel, outputChannel));
                await createCurrentDiscordEntry.ExecuteReaderAsync();
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
            string query = string.Format("insert into \"AnonSuggestionsBot\".submissions (server_id, user_hash, suggestion_uid, suggestion_time, suggestion_title, suggestion_text) values ({0}, '{1}', '{2}', '{3}', '{4}', '{5}');", serverID.ToString(), user_hash, suggestion_uid, DateTime.Now.ToString(), suggestion_title, suggestion_text);
            await using var logSuggestion = dataSource.CreateCommand(query);
            await using var logSuggestionReader = await logSuggestion.ExecuteReaderAsync();
        }

        public async void createBanEntry(ulong serverID, string user_hash, int length_minutes, bool perma) {
            string query = string.Format("insert into \"AnonSuggestionsBot\".bans (server_id, user_hash, ban_time, ban_duration, ban_perma) values ({0}, '{1}', '{2}', '{3}');", serverID.ToString(), user_hash, DateTime.Now.ToString(), length_minutes, perma);
            await using var createBanEntry = dataSource.CreateCommand(query);
            await using var createBanEntryReader = await createBanEntry.ExecuteReaderAsync();
        }
    }
}
