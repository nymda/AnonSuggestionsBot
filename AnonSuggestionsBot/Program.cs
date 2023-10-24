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

using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Reflection;
using System.Windows.Input;

/*
 * TODO:
 * global server slowmode
 * voting
 * additional error handling
 * shitload of QA testing
 * DB / API optimizations
 */

namespace AnonSuggestionsBot
{
    internal class Program {
        private static DiscordSocketClient _client;
        private static IServiceProvider _services;
        private static Database _db = new Database();
        System.Security.Cryptography.MD5 _md5 = System.Security.Cryptography.MD5.Create();
        public static Task Main(string[] args) => new Program().MainAsync();

        //takes an input string and returns the MD5 hash of it, this is used for user ID anonymization
        public string stringToMD5(string input) {
            return Convert.ToHexString(_md5.ComputeHash(System.Text.Encoding.ASCII.GetBytes(input)));
        }

        public async Task MainAsync() {
            bool login = false;

            //login process for the postgres database, not included in github repo
            while (!login) {
                Console.Write("Enter postgresql IP: ");
                string? IPAddy = Console.ReadLine();
                Console.Write("Enter postgresql password: ");
                string? password = Console.ReadLine();
                Console.Clear();

                if(IPAddy == null || password == null) { continue; }

                try {
                    _db.login(IPAddy, password); //login to the local DB
                    login = true;
                }
                catch { }
            }

            //get the bot token from the DB
            string token = await _db.getBotToken();

            //set up discord client
            _client = new DiscordSocketClient(new DiscordSocketConfig {
                LogLevel = LogSeverity.Info,
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });

            var commandService = new CommandService(new CommandServiceConfig {
                CaseSensitiveCommands = false
            });

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(commandService)
                .AddSingleton<InteractionService>()
                .BuildServiceProvider();

            _client.Log += LogAsync;
            _client.Ready += ReadyAsync;
            _client.ButtonExecuted += buttonHandler;
            _client.ModalSubmitted += modalResponseHandler;

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private async Task LogAsync(LogMessage log) {
            Console.WriteLine(log);
        }

        //first setup, creates the global /initialize command
        private async Task ReadyAsync() {
            _client.SlashCommandExecuted += SlashCommandHandler;

            //initialize the global /initialize command
            var initializeCmd = new SlashCommandBuilder();
            initializeCmd.WithName("initialize");
            initializeCmd.WithDescription("initializes AnonSuggestionsBot for this server");
            initializeCmd.AddOption("input", ApplicationCommandOptionType.Channel, "the channel to post the create suggestions message in");
            initializeCmd.AddOption("output", ApplicationCommandOptionType.Channel, "the channel to send anonymous suggestions to");
            initializeCmd.AddOption("logging", ApplicationCommandOptionType.Channel, "(optional) the channel to send logs to when a user is banned");
            await _client.CreateGlobalApplicationCommandAsync(initializeCmd.Build());
        }

        //handles all future commands
        private async Task SlashCommandHandler(SocketSlashCommand command) {
            if(command.GuildId == null) {
                await command.RespondAsync("Commands cannot be run in DMs");
                return;
            }

            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);
            bool userIsAdmin = guild.GetUser(command.User.Id).GuildPermissions.Administrator;
            bool userIsModerator = guild.GetUser(command.User.Id).GuildPermissions.ModerateMembers;

            SocketRole? overrideRole = guild.Roles.Where(r => r.Name == "SuggestionPerms").FirstOrDefault();
            bool userHasOverrideRole = false;
            if (overrideRole != null) { userHasOverrideRole = guild.GetUser(command.User.Id).Roles.Contains(overrideRole); }

            if (!(userIsAdmin || userIsModerator || userHasOverrideRole)) {
                await command.RespondAsync("You do not have permission to run this command", ephemeral: true);
                return;
            }

            if (command.CommandName == "initialize") {
                await guildSetup(command);
            }
            if(command.CommandName == "suggestion-ban") {
                await banSuggestionCreator(command);
            }
            if(command.CommandName == "suggestion-timeout") {
                await timeoutSuggestionCreator(command);
            }
            if (command.CommandName == "suggestion-lookup") {
                await lookupSuggestionCreatorBans(command);
            }

        }

        public string timeString(int minutes, bool full) {
            int hours = (int)Math.Floor((decimal)(minutes / 60));
            int mins = minutes % 60;

            if (!full) {
                if (hours > 0) {
                    return string.Format("{0} hour{1}", hours, hours == 1 ? "" : "s");
                }
                else {
                    return string.Format("{0} minute{1}", mins, mins == 1 ? "" : "s");
                }
            }
            else {
                return string.Format("{0} hour{1}, {2} minute{3}", hours, hours == 1 ? "" : "s", mins, mins == 1 ? "" : "s");
            }
        }

        private async Task banSuggestionCreator(SocketSlashCommand command) {
            if(command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            SocketSlashCommandDataOption? suggestion_uid_option = command.Data.Options.ToArray().Where(x => x.Name == "id").FirstOrDefault();

            if (suggestion_uid_option == null) {
                await command.RespondAsync("Suggestion UID is required");
                return;
            }

            string? suggestion_uid = suggestion_uid_option.Value.ToString();

            //gets the hashed UID of the user who created the suggestion
            string userHash = await _db.getUserHashFromSuggestionUID((ulong)command.GuildId, suggestion_uid);

            if(userHash == "") {
                await command.RespondAsync("Suggestion UID not found");
                return;
            }

            //creates a ban entry using the retrieved user hash
            _db.createBanEntry((ulong)command.GuildId, userHash, 0, true);

            //log the ban in the logging channel if one is selected
            ulong loggingChannelId = await _db.getServerLoggingChannel((ulong)command.GuildId);
            string[] bannedSuggestion = await _db.getSuggestionFromUid((ulong)command.GuildId, suggestion_uid);

            if (loggingChannelId != 0) {
                SocketTextChannel? loggingChannel = guild.GetTextChannel(loggingChannelId);
                if (loggingChannel != null) {
                    string title = string.Format("@{0} has banned a user from creating suggestions", command.User.Username);
                    string body = string.Format("Suggestion UID: {0}\n Suggestion time: {1}, \nBan length: Permenant", suggestion_uid, bannedSuggestion[0]);
                    var embedNotification = new EmbedBuilder();
                    embedNotification.AddField(title, body);

                    var embedBannedSuggestion = new EmbedBuilder();
                    embedBannedSuggestion.AddField(bannedSuggestion[1], bannedSuggestion[2]);
                    embedBannedSuggestion.WithColor(100, 31, 34);

                    Embed[] embedPackage = {embedNotification.Build(), embedBannedSuggestion.Build() }; 

                    await loggingChannel.SendMessageAsync(embeds: embedPackage);
                }
            }

            await command.RespondAsync("User has been banned from creating suggestions");
        }

        private async Task timeoutSuggestionCreator(SocketSlashCommand command) {
            if (command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            if (command.Data.Options.ToArray().Count() < 2) {
                await command.RespondAsync("Suggestion UID and ban length are required");
                return;
            }

            SocketSlashCommandDataOption? suggestion_uid_option = command.Data.Options.ToArray().Where(x => x.Name == "id").FirstOrDefault();
            SocketSlashCommandDataOption? ban_length_option = command.Data.Options.ToArray().Where(x => x.Name == "length").FirstOrDefault();

            if (suggestion_uid_option == null || ban_length_option == null) {
                await command.RespondAsync("Suggestion UID and ban length are required");
                return;
            }

            string? suggestion_uid = suggestion_uid_option.Value.ToString();

            if (suggestion_uid == null) {
                await command.RespondAsync("Suggestion UID and ban length are required");
                return;
            }

            //gets the hashed UID of the user who created the suggestion
            string userHash = await _db.getUserHashFromSuggestionUID((ulong)command.GuildId, suggestion_uid);

            if (userHash == "") {
                await command.RespondAsync("Suggestion UID not found");
                return;
            }

            //creates a ban entry using the retrieved user hash, but its temporary
            _db.createBanEntry((ulong)command.GuildId, userHash, Convert.ToInt32((double)ban_length_option.Value), false);

            //log the ban in the logging channel if one is selected
            ulong loggingChannelId = await _db.getServerLoggingChannel((ulong)command.GuildId);
            string[] bannedSuggestion = await _db.getSuggestionFromUid((ulong)command.GuildId, suggestion_uid);

            if (loggingChannelId != 0) {
                SocketTextChannel? loggingChannel = guild.GetTextChannel(loggingChannelId);
                if (loggingChannel != null) {
                    string title = string.Format("@{0} has banned a user from creating suggestions", command.User.Username);
                    string body = string.Format("Suggestion UID: {0}\n Suggestion time: {1}, \nBan length: {2}", suggestion_uid, bannedSuggestion[0], timeString(Convert.ToInt32((double)ban_length_option.Value), true));

                    var embedNotification = new EmbedBuilder();
                    embedNotification.AddField(title, body);

                    var embedBannedSuggestion = new EmbedBuilder();
                    embedBannedSuggestion.AddField(bannedSuggestion[1], bannedSuggestion[2]);
                    embedBannedSuggestion.WithColor(100, 31, 34);

                    Embed[] embedPackage = { embedNotification.Build(), embedBannedSuggestion.Build() };

                    await loggingChannel.SendMessageAsync(embeds: embedPackage);
                }
            }

            await command.RespondAsync(string.Format("User has been banned from creating suggestions for {0}", timeString(Convert.ToInt32((double)ban_length_option.Value), true)));
        }

        private async Task lookupSuggestionCreatorBans(SocketSlashCommand command) {
            if (command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            SocketSlashCommandDataOption? suggestion_uid_option = command.Data.Options.ToArray().Where(x => x.Name == "id").FirstOrDefault();

            if (suggestion_uid_option == null) {
                await command.RespondAsync("Suggestion UID is required");
                return;
            }

            string? suggestion_uid = suggestion_uid_option.Value.ToString();

            if (suggestion_uid == null) {
                await command.RespondAsync("Suggestion UID is required");
                return;
            }

            //gets the hashed UID of the user who created the suggestion
            string userHash = await _db.getUserHashFromSuggestionUID((ulong)command.GuildId, suggestion_uid);

            if (userHash == "") {
                await command.RespondAsync("Suggestion UID not found");
                return;
            }

            //creates a ban entry using the retrieved user hash
            int userBans = await _db.getUserPreviousBans((ulong)command.GuildId, userHash);

            await command.RespondAsync(string.Format("The creator of `{0}` has been previously banned `{1}` time{2}", suggestion_uid, userBans, userBans == 1 ? "" : "s"));
        }

        private async Task guildSetup(SocketSlashCommand command) {
            if(command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            if(command.Data.Options.Count() < 2) {
                await command.RespondAsync("You must specify both an input text channel and output text channel");
                return;
            }

            //takes the input and output channel for the bot to use

            SocketSlashCommandDataOption? inputChannel_option = command.Data.Options.ToArray().Where(x => x.Name == "input").FirstOrDefault();
            SocketSlashCommandDataOption? outputChannel_option = command.Data.Options.ToArray().Where(x => x.Name == "output").FirstOrDefault();
            SocketSlashCommandDataOption? loggingChannel_option = command.Data.Options.ToArray().Where(x => x.Name == "logging").FirstOrDefault();

            if (inputChannel_option == null || outputChannel_option == null) {
                await command.RespondAsync("You must specify both an input text channel and output text channel");
                return;
            }

            SocketGuildChannel? inputChannel = guild.Channels.FirstOrDefault(c => c.Name == inputChannel_option.Value.ToString());
            SocketGuildChannel? outputChannel = guild.Channels.FirstOrDefault(c => c.Name == outputChannel_option.Value.ToString());
            SocketGuildChannel? loggingChannel = null;

            if(loggingChannel_option != null) { 
                loggingChannel = guild.Channels.FirstOrDefault(c => c.Name == loggingChannel_option.Value.ToString()); 
                if(loggingChannel.GetChannelType() != ChannelType.Text) {
                    await command.RespondAsync("Logging channel must be a text channel");
                    return;
                }
            }

            if (inputChannel == null || outputChannel == null || inputChannel.GetChannelType() != ChannelType.Text || outputChannel.GetChannelType() != ChannelType.Text) {
                await command.RespondAsync("You must specify both an input text channel and output text channel");
                return;
            }

            //entry for the server that /initialize was run on

            if (loggingChannel != null) {
                _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString(), loggingChannel.Id.ToString());
            }
            else {
                _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString());
            }

            //creates guild specific commands suggestion-ban and suggestion-timeout
            var suggestionBan = new SlashCommandBuilder();
            suggestionBan.WithName("suggestion-ban");
            suggestionBan.WithDescription("Bans the creator of suggestion");
            suggestionBan.AddOption("id", ApplicationCommandOptionType.String, "the ID of the suggestion to ban the creator of");
            await guild.CreateApplicationCommandAsync(suggestionBan.Build());

            var suggestionTimeout = new SlashCommandBuilder();
            suggestionTimeout.WithName("suggestion-timeout");
            suggestionTimeout.WithDescription("Timeouts the creator of a suggestion");
            suggestionTimeout.AddOption("id", ApplicationCommandOptionType.String, "the ID of the suggestion to timeout the creator of");
            suggestionTimeout.AddOption("length", ApplicationCommandOptionType.Number, "timeout length in minutes");
            await guild.CreateApplicationCommandAsync(suggestionTimeout.Build());

            var suggestionLookup = new SlashCommandBuilder();
            suggestionLookup.WithName("suggestion-lookup");
            suggestionLookup.WithDescription("Returns the number of previous bans the creator of a suggestion has");
            suggestionLookup.AddOption("id", ApplicationCommandOptionType.String, "the ID of the suggestion to timeout the creator of");
            await guild.CreateApplicationCommandAsync(suggestionLookup.Build());

            //posts the "create suggestion" message with the button to the input channel
            var btnBuilder = new ComponentBuilder().WithButton("Suggest", "btn-send-suggestion");
            await guild.GetTextChannel(inputChannel.Id).SendMessageAsync("Click here to submit a suggestion:", components: btnBuilder.Build());

            await command.RespondAsync("Initialization complete");
        }

        //run when the create suggestion button is clicked, shows the input box
        public async Task buttonHandler(SocketMessageComponent component) {
        if(component.Data.CustomId == "btn-send-suggestion") {
                var mb = new ModalBuilder();
                mb.WithTitle("AnonSuggestionBot");
                mb.WithCustomId("suggestion-input");
                mb.AddTextInput("Title:", "suggestion-input-title", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true);
                mb.AddTextInput("Suggestion:", "suggestion-input-body", TextInputStyle.Paragraph, minLength: 1, maxLength: 2000, required: true);
                await component.RespondWithModalAsync(mb.Build());
            }
        }

        public string createUid() {
            return Guid.NewGuid().ToString();
        }

        //handles actually sending a suggestion
        public async Task modalResponseHandler(SocketModal modal) {
            ulong? guildId = modal.GuildId;
            if(guildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)guildId);
            string title = modal.Data.Components.First(x => x.CustomId == "suggestion-input-title").Value;
            string body = modal.Data.Components.First(x => x.CustomId == "suggestion-input-body").Value;

            //gets the output channel for the current server
            ulong outputChannelId = await _db.getServerOutputChannel((ulong)guildId);
            SocketTextChannel? outputChannel = guild.GetTextChannel(outputChannelId);
            if (outputChannel == null) { return; }

            //hashes the sending users ID for storage and use later
            string userHash = stringToMD5(modal.User.Id.ToString());

            //checks if the sending user is banned using their hash
            int userBanned = await _db.checkUserBanned((ulong)guildId, userHash); //modal.User.Id is the user's discord ID, this is never stored
            if(userBanned > 0) {
                await modal.RespondAsync(string.Format("You are banned from creating suggestions, try again in {0}", timeString(userBanned, false)), ephemeral: true);
                return;
            }
            else if(userBanned == -2) {
                await modal.RespondAsync("You are permanently banned from creating suggestions", ephemeral: true);
                return;
            }

            //creates a new UID for the suggestion, this is just a random number
            string suggestion_uid = createUid();

            //logs the suggestion in the DB, only storing the users hash
            _db.logSuggestion((ulong)guildId, userHash, suggestion_uid, title, body);

            //creates the embed and sends it to the output channel
            var embed = new EmbedBuilder();
            embed.AddField(title, body);
            embed.WithFooter("Suggestion ID: " + suggestion_uid);
            await outputChannel.SendMessageAsync(embed: embed.Build());

            //responds to the user with a confirmation message, only visible to the user who sent the suggestion
            await modal.RespondAsync("Suggestion sent!", ephemeral: true);
        }
    }
}