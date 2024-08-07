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

using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Input;
using static AnonSuggestionsBot.Database;

/*
 * TODO:
 * voting
 * merge timeout / ban into one function
 * additional error handling
 * shitload of QA testing
 * DB / API optimizations
 */

namespace AnonSuggestionsBot
{
    public struct pollOption {
        public int ID;
        public string text;
        public string button;
    }

    public struct pollDataPackage {
        public ulong guildId = 0;
        public string title = "";
        public string userHash = "";
        public string pollUid = "";
            
        public List<pollOption> options = new List<pollOption> { };

        public string buttonUidResults = "";
        public string buttonUidEnd = "";

        public pollDataPackage() { }
    }

    public struct pollResultsPackage {
        public int countA = 0;
        public int countB = 0;
        public int countC = 0;
        public int countD = 0;

        public pollResultsPackage() { }
    }
    
    public enum pollButtonIds {
        INVALID = -1,
        A = 0,
        B = 1,
        C = 2,
        D = 3,
        RESULTS = 10,
        END = 11
    }
    
    internal class Program {
        private static DiscordSocketClient _client;
        private static IServiceProvider _services;
        private static Database _db = new Database();
        System.Security.Cryptography.MD5 _md5 = System.Security.Cryptography.MD5.Create();
        public static Task Main(string[] args) => new Program().MainAsync(args.Length >= 2 ? args[0] : "", args.Length >= 2 ? args[1] : "");

        public const bool USE_BETA_BOT_TOKEN = false;
        
        //takes an input string and returns the MD5 hash of it, this is used for user ID anonymization
        public string stringToMD5(string input) {
            return Convert.ToHexString(_md5.ComputeHash(System.Text.Encoding.ASCII.GetBytes(input)));
        }

        public async Task MainAsync(string dbIp, string dbPw) {
            bool login = false;
            //login process for the postgres database, not included in github repo

            if (dbIp != "" && dbPw != "") {
                _db.login(dbIp, dbPw); //login to the local DB
                login = true;
            }
            else {
                while (!login) {
                    Console.Write("Enter postgresql IP: ");
                    string? IPAddy = Console.ReadLine();
                    Console.Write("Enter postgresql password: ");
                    string? password = Console.ReadLine();
                    Console.Clear();

                    if (IPAddy == null || password == null) { continue; }

                    try {
                        _db.login(IPAddy, password); //login to the local DB
                        login = true;
                    }
                    catch { }
                }
            }

            //get the bot token from the DB
            string token = await _db.getBotToken(USE_BETA_BOT_TOKEN);

            if (USE_BETA_BOT_TOKEN) {
                Console.WriteLine("WARNING: USING BETA TOKEN, CHANGE BEFORE DEPLOYING");
            }
            
            //set up discord client
            _client = new DiscordSocketClient(new DiscordSocketConfig {
                LogLevel = LogSeverity.Info,
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });

            CommandService commandService = new CommandService(new CommandServiceConfig {
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
            _client.SlashCommandExecuted += SlashCommandHandler;
            
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private async Task LogAsync(LogMessage log) {
            Console.WriteLine(log);
        }

        //first setup, creates the global /initialize command
        private async Task ReadyAsync() {
            //initialize the global /initialize command
            SlashCommandBuilder initializeCmd = new SlashCommandBuilder();
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
            if (command.CommandName == "suggestion-unban") {
                await unbanSuggestionCreator(command);
            }
            if(command.CommandName == "suggestion-slowmode") {
                await setServerSlowmode(command);
            }
            if(command.CommandName == "anon-poll") {
                await createNewAnonVote(command);
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

            if (suggestion_uid == null) {
                await command.RespondAsync("Suggestion UID is required");
                return;
            }

            //gets the hashed UID of the user who created the suggestion
            string userHash = await _db.getUserHashFromSuggestionUID((ulong)command.GuildId, suggestion_uid);

            if(userHash == "") {
                await command.RespondAsync("Suggestion UID not found");
                return;
            }

            //creates a ban entry using the retrieved user hash
            _db.createBanEntry((ulong)command.GuildId, userHash, suggestion_uid, 0, true);

            //log the ban in the logging channel if one is selected
            ulong loggingChannelId = await _db.getServerLoggingChannel((ulong)command.GuildId);

            SocketTextChannel? loggingChannel = null;
            if (loggingChannelId != 0 && (loggingChannel = guild.GetTextChannel(loggingChannelId)) != null) {
                string[] bannedSuggestion = await _db.getSuggestionFromUid((ulong)command.GuildId, suggestion_uid);

                string title = string.Format("@{0} has banned a user from creating suggestions", command.User.Username);
                string body = string.Format("Suggestion UID: {0}\n Suggestion time: {1} \nBan length: Permenant", suggestion_uid, bannedSuggestion[0]);

                EmbedBuilder embedNotification = new EmbedBuilder();
                embedNotification.AddField(title, body);

                EmbedBuilder embedBannedSuggestion = new EmbedBuilder();
                embedBannedSuggestion.AddField(bannedSuggestion[1], bannedSuggestion[2]);
                embedBannedSuggestion.WithColor(100, 31, 34);

                Embed[] embedPackage = { embedNotification.Build(), embedBannedSuggestion.Build() };
                await loggingChannel.SendMessageAsync(embeds: embedPackage);

                ulong bannedMessageID = ulong.Parse(bannedSuggestion[3]);
                IMessage? bannedMessage = null;
                ulong outputChannelId = await _db.getServerOutputChannel((ulong)command.GuildId);
                if (bannedMessageID != 0 && (bannedMessage = await guild.GetTextChannel(outputChannelId).GetMessageAsync(bannedMessageID)) != null) {
                    await guild.GetTextChannel(outputChannelId).DeleteMessageAsync(bannedMessage);
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
            _db.createBanEntry((ulong)command.GuildId, userHash, suggestion_uid, Convert.ToInt32((double)ban_length_option.Value), false);

            //log the ban in the logging channel if one is selected
            ulong loggingChannelId = await _db.getServerLoggingChannel((ulong)command.GuildId);

            SocketTextChannel? loggingChannel = null;
            if (loggingChannelId != 0 && (loggingChannel = guild.GetTextChannel(loggingChannelId)) != null) {
                string[] bannedSuggestion = await _db.getSuggestionFromUid((ulong)command.GuildId, suggestion_uid);

                string title = string.Format("@{0} has banned a user from creating suggestions", command.User.Username);

                string body = string.Format("Suggestion UID: {0}\n Suggestion time: {1} \nBan length: {2}", suggestion_uid, bannedSuggestion[0], timeString(Convert.ToInt32((double)ban_length_option.Value), true));

                EmbedBuilder embedNotification = new EmbedBuilder();
                embedNotification.AddField(title, body);

                EmbedBuilder embedBannedSuggestion = new EmbedBuilder();
                embedBannedSuggestion.AddField(bannedSuggestion[1], bannedSuggestion[2]);
                embedBannedSuggestion.WithColor(100, 31, 34);

                Embed[] embedPackage = { embedNotification.Build(), embedBannedSuggestion.Build() };
                await loggingChannel.SendMessageAsync(embeds: embedPackage);

                ulong bannedMessageID = ulong.Parse(bannedSuggestion[3]);
                IMessage? bannedMessage = null;
                            ulong outputChannelId = await _db.getServerOutputChannel((ulong)command.GuildId);
                if (bannedMessageID != 0 && (bannedMessage = await guild.GetTextChannel(outputChannelId).GetMessageAsync(bannedMessageID)) != null) {
                    await guild.GetTextChannel(outputChannelId).DeleteMessageAsync(bannedMessage);
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
            int[] userBans = await _db.getUserPreviousBans((ulong)command.GuildId, userHash);

            await command.RespondAsync(string.Format("The creator of `{0}` has `{1}` ban{2} from `{3}` suggestion{4}", suggestion_uid, userBans[0], userBans[0] == 1 ? "" : "s", userBans[1], userBans[1] == 1 ? "" : "s"));
        }

        private async Task setServerSlowmode(SocketSlashCommand command) {
            if (command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            SocketSlashCommandDataOption? length_option = command.Data.Options.ToArray().Where(x => x.Name == "length").FirstOrDefault();

            if(length_option == null) {
                await command.RespondAsync("Slowmode length is required");
                return;
            }

            int length = Convert.ToInt32((double)length_option.Value);

            if(length < 0) {
                await command.RespondAsync("Slowmode length must be a positive number");
                return;
            }

            await _db.setServerSlowMode((ulong)command.GuildId, length);

            await command.RespondAsync(string.Format("Slowmode set to {0}", timeString(length, false)));
        }

        private async Task unbanSuggestionCreator(SocketSlashCommand command) {
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
            
            await _db.setBanExpired((ulong)command.GuildId, userHash);

            await command.RespondAsync(string.Format("Bans removed for the creator of `{0}`", suggestion_uid));
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

            await command.RespondAsync("Initializing, please wait...");

            if (loggingChannel != null) {
                _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString(), loggingChannel.Id.ToString());
            }
            else {
                _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString());
            }

            //creates guild specific commands suggestion-ban and suggestion-timeout
            SlashCommandBuilder suggestionBan = new SlashCommandBuilder();
            suggestionBan.WithName("suggestion-ban");
            suggestionBan.WithDescription("Bans the creator of suggestion");
            suggestionBan.AddOption("id", ApplicationCommandOptionType.String, "the ID of the suggestion");
            await guild.CreateApplicationCommandAsync(suggestionBan.Build());

            SlashCommandBuilder suggestionTimeout = new SlashCommandBuilder();
            suggestionTimeout.WithName("suggestion-timeout");
            suggestionTimeout.WithDescription("Timeouts the creator of a suggestion");
            suggestionTimeout.AddOption("id", ApplicationCommandOptionType.String, "the UID of the suggestion");
            suggestionTimeout.AddOption("length", ApplicationCommandOptionType.Number, "timeout length in minutes");
            await guild.CreateApplicationCommandAsync(suggestionTimeout.Build());

            SlashCommandBuilder suggestionLookup = new SlashCommandBuilder();
            suggestionLookup.WithName("suggestion-lookup");
            suggestionLookup.WithDescription("Returns the number of previous bans the creator of a suggestion has");
            suggestionLookup.AddOption("id", ApplicationCommandOptionType.String, "the UID of the suggestion");
            await guild.CreateApplicationCommandAsync(suggestionLookup.Build());

            SlashCommandBuilder suggestionUnban = new SlashCommandBuilder();
            suggestionUnban.WithName("suggestion-unban");
            suggestionUnban.WithDescription("Unbans the creator of a suggestion");
            suggestionUnban.AddOption("id", ApplicationCommandOptionType.String, "the UID of the suggestion");
            await guild.CreateApplicationCommandAsync(suggestionUnban.Build());

            SlashCommandBuilder suggestionSlowmode = new SlashCommandBuilder();
            suggestionSlowmode.WithName("suggestion-slowmode");
            suggestionSlowmode.WithDescription("Sets the slowmode for the server");
            suggestionSlowmode.AddOption("length", ApplicationCommandOptionType.Number, "slowmode length in minutes");
            await guild.CreateApplicationCommandAsync(suggestionSlowmode.Build());

            SlashCommandBuilder createAnonPoll = new SlashCommandBuilder();
            createAnonPoll.WithName("anon-poll");
            createAnonPoll.WithDescription("Creates a new anonymous poll");
            await guild.CreateApplicationCommandAsync(createAnonPoll.Build());

            //posts the "create suggestion" message with the button to the input channel
            ComponentBuilder btnBuilder = new ComponentBuilder().WithButton("Suggest", "btn-send-suggestion");

            string postMessageText = await _db.getServerPostText((ulong)command.GuildId);

            await guild.GetTextChannel(inputChannel.Id).SendMessageAsync(postMessageText, components: btnBuilder.Build());

            await guild.GetTextChannel((ulong)command.ChannelId).SendMessageAsync("Initialization complete.");
        }

        public string renderProgressBar(int total, float percentage, int length) {
            int squaresFilled = (int)(percentage * length);
            int squaresEmpty = length - squaresFilled;
            string response = "";
            response += string.Format("[{0}]", total.ToString("0000"));
            response += string.Format("[{0}{1}]", new string('#', squaresFilled), new string(' ', squaresEmpty));
            response += string.Format("[{0}%]", (percentage * 100f).ToString("000"));
            return response;
        }

        public async Task handlePollButtonPress(SocketMessageComponent component) {
            if (component.GuildId == null) { return; }
            pollButtonResponse pbr = await _db.getClickedPollButton((ulong)component.GuildId, component.Data.CustomId);
            if(pbr.option_num == -1) {
                await component.RespondAsync("Internal error, invalid button ID or null response"); 
                return; 
            }

            if(pbr.option_num < 10) {
                string userHash = stringToMD5(component.User.Id.ToString());
                if (await _db.hasUserVotedOnPoll((ulong)component.GuildId, pbr.poll_uid, userHash)) {
                    await component.RespondAsync("You have already voted on this poll", ephemeral: true);
                    return;
                }

                _db.voteOnPoll((ulong)component.GuildId, pbr.poll_uid, userHash, pbr.option_num);

                await component.RespondAsync("Vote counted, thanks!", ephemeral: true);
            }
            else if(pbr.option_num == 10) { //results
                int[] pollUsedOptions = await _db.getValidPollOptions((ulong)component.GuildId, pbr.poll_uid);
                pollResultsPackage PRP = await _db.getPollResults((ulong)component.GuildId, pbr.poll_uid);
                string pollTitle = await _db.getPollTitle((ulong)component.GuildId, pbr.poll_uid);
                string body = "```";

                int totalVotes = PRP.countA + PRP.countB + PRP.countC + PRP.countD;
                float percentA = (float)PRP.countA / (float)totalVotes;
                float percentB = (float)PRP.countB / (float)totalVotes;
                float percentC = (float)PRP.countC / (float)totalVotes;
                float percentD = (float)PRP.countD / (float)totalVotes;

                foreach (int i in pollUsedOptions) {
                    switch (i) {
                        case 0:
                            body += "A: " + renderProgressBar(PRP.countA, percentA, 25) + "\n";
                            break;
                        case 1:
                            body += "B: " + renderProgressBar(PRP.countB, percentB, 25) + "\n";
                            break;
                        case 2:
                            body += "C: " + renderProgressBar(PRP.countC, percentC, 25) + "\n";
                            break;
                        case 3:
                            body += "D: " + renderProgressBar(PRP.countD, percentD, 25) + "\n";
                            break;
                    }
                }

                body += "```";
                
                EmbedBuilder embedNotification = new EmbedBuilder();
                embedNotification.AddField(pollTitle + " | Current results", body);
                embedNotification.WithFooter("Total votes: " + totalVotes);
                
                await component.RespondAsync(embed: embedNotification.Build(), ephemeral: true);
            }
            else if (pbr.option_num == 11) { //end

            }

        }
        
        //run when the create suggestion button is clicked, shows the input box
        public async Task buttonHandler(SocketMessageComponent component) {
            if(component.Data.CustomId == "btn-send-suggestion") {
                ModalBuilder mb = new ModalBuilder();
                mb.WithTitle("AnonSuggestionBot");
                mb.WithCustomId("modal-suggestion");
                mb.AddTextInput("Title:", "modal-suggestion-title", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true);
                mb.AddTextInput("Suggestion:", "modal-suggestion-body", TextInputStyle.Paragraph, minLength: 1, maxLength: 1000, required: true);
                await component.RespondWithModalAsync(mb.Build());
            }
            else if (component.Data.CustomId.StartsWith("PC_")) {
                //poll button
                await handlePollButtonPress(component);
            }
        }

        public async Task createNewAnonVote(SocketSlashCommand command) {
            ModalBuilder mb = new ModalBuilder();
            mb.WithTitle("AnonSuggestionBot - Anonymous poll");
            mb.WithCustomId("modal-poll");
            mb.AddTextInput("Title:", "poll-input-title", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true);
            mb.AddTextInput("Option 1:", "poll-input-A", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true);
            mb.AddTextInput("Option 2:", "poll-input-B", TextInputStyle.Short, minLength: 1, maxLength: 100, required: true);
            mb.AddTextInput("Option 3:", "poll-input-C", TextInputStyle.Short, minLength: 1, maxLength: 100, required: false);
            mb.AddTextInput("Option 4:", "poll-input-D", TextInputStyle.Short, minLength: 1, maxLength: 100, required: false);
            await command.RespondWithModalAsync(mb.Build());
        }

        public string createUid() {
            return Guid.NewGuid().ToString();
        }

        public string createShortUid() {
            return Guid.NewGuid().ToString().Split("-")[0];
        }

        //handles actually sending a suggestion
        public async Task postAnonSuggestion(SocketModal modal) {
            ulong? guildId = modal.GuildId;
            if (guildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)guildId);
            string title = modal.Data.Components.First(x => x.CustomId == "modal-suggestion-title").Value;
            string body = modal.Data.Components.First(x => x.CustomId == "modal-suggestion-body").Value;

            //gets the output channel for the current server
            ulong outputChannelId = await _db.getServerOutputChannel((ulong)guildId);
            SocketTextChannel? outputChannel = guild.GetTextChannel(outputChannelId);
            if (outputChannel == null) { return; }

            //hashes the sending users ID for storage and use later
            string userHash = stringToMD5(modal.User.Id.ToString());

            //checks if the user is rate limited
            DateTime userTimeout = await _db.getUserLastSuggestionTime((ulong)guildId, userHash);
            int serverSlowMode = await _db.getServerSlowMode((ulong)guildId);
            DateTime userTimeoutEnd = userTimeout.AddMinutes(serverSlowMode);

            if (userTimeout != DateTime.MinValue && serverSlowMode > 0) {
                if (DateTime.Now < userTimeoutEnd) {
                    await modal.RespondAsync(string.Format("Slow mode is enabled, try again in {0}", timeString((int)(userTimeoutEnd - DateTime.Now).TotalMinutes, false)), ephemeral: true);
                    return;
                }
            }

            //checks if the sending user is banned using their hash
            int userBanned = await _db.checkUserBanned((ulong)guildId, userHash); //modal.User.Id is the user's discord ID, this is never stored
            if (userBanned > 0) {
                await modal.RespondAsync(string.Format("You are banned from creating suggestions, try again in {0}", timeString(userBanned, false)), ephemeral: true);
                return;
            }
            else if (userBanned == -2) {
                await modal.RespondAsync("You are permanently banned from creating suggestions", ephemeral: true);
                return;
            }

            //creates a new UID for the suggestion, this is just a random number
            string suggestion_uid = createUid();

            //logs the suggestion in the DB, only storing the users hash
            _db.logSuggestion((ulong)guildId, userHash, suggestion_uid, title, body);

            //creates the embed and sends it to the output channel
            EmbedBuilder embed = new EmbedBuilder();
            embed.AddField(title, body);
            embed.WithFooter("Suggestion ID: " + suggestion_uid);
            RestUserMessage nmsg = await outputChannel.SendMessageAsync(embed: embed.Build());

            //log the message ID for if it needs to be removed later
            _db.setSuggestionMessageId((ulong)guildId, nmsg.Id, suggestion_uid);

            //responds to the user with a confirmation message, only visible to the user who sent the suggestion
            await modal.RespondAsync("Suggestion sent!", ephemeral: true);
        }

        public async Task postAnonPoll(SocketModal modal) {            
            ulong? guildId = modal.GuildId;
            if (guildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)guildId);
            string title = modal.Data.Components.First(x => x.CustomId == "poll-input-title").Value;
            SocketMessageComponentData? A = modal.Data.Components.FirstOrDefault(x => x.CustomId == "poll-input-A");
            SocketMessageComponentData? B = modal.Data.Components.FirstOrDefault(x => x.CustomId == "poll-input-B");
            SocketMessageComponentData? C = modal.Data.Components.FirstOrDefault(x => x.CustomId == "poll-input-C");
            SocketMessageComponentData? D = modal.Data.Components.FirstOrDefault(x => x.CustomId == "poll-input-D");

            if (A == null || B == null) { return; }

            string[] options = new string[4];
            options[0] = A.Value;
            options[1] = B.Value;
            if (C != null) { options[2] = C.Value; }
            if (D != null) { options[3] = D.Value; }

            string poll_uid = createUid();
            string userHash = stringToMD5(modal.User.Id.ToString());

            string body = "```";
            string[] optionLetters = { "A", "B", "C", "D" };
            ComponentBuilder btnBuilder = new ComponentBuilder();

            pollDataPackage PDP = new pollDataPackage {
                title = title,
                userHash = userHash,
                guildId = (ulong)guildId,
                pollUid = poll_uid
            };

            for (int i = 0; i < 4; i++) {
                if (options[i] == "") { continue; }
                string thisButtonnId = "PC_" + createShortUid();
                string thisOptionLetter = optionLetters[i];
                string thisOptionText = options[i];
                PDP.options.Add(new pollOption() { ID = i, text = thisOptionText, button = thisButtonnId });
                body += string.Format("{0}: {1}\n", thisOptionLetter, thisOptionText);
                btnBuilder.WithButton(thisOptionLetter, thisButtonnId);
            }

            string resultsBtnUID = "PC_" + createShortUid();
            btnBuilder.WithButton("Results", resultsBtnUID, row: 1, style: ButtonStyle.Success);
            string endBtnUID = "PC_" + createShortUid();
            btnBuilder.WithButton("End", endBtnUID, row: 1, style: ButtonStyle.Danger);

            PDP.buttonUidResults = resultsBtnUID;
            PDP.buttonUidEnd = endBtnUID;

            await _db.createNewPoll(PDP);

            EmbedBuilder embedNotification = new EmbedBuilder();
            embedNotification.AddField(title, body + "```");
            embedNotification.WithFooter("Poll ID: " + poll_uid);

            RestUserMessage nmsg = await modal.Channel.SendMessageAsync(embed: embedNotification.Build(), components: btnBuilder.Build());

            _db.setPollMessageId((ulong)guildId, poll_uid, nmsg.Id);

            await modal.RespondAsync("Poll created!", ephemeral: true);
        }
        
        public async Task modalResponseHandler(SocketModal modal) {

            if (modal.Data.CustomId == "modal-suggestion") {
                await postAnonSuggestion(modal);
            }
            else if(modal.Data.CustomId == "modal-poll") {
                await postAnonPoll(modal);
            }         
        }
    }
}