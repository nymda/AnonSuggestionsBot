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

namespace AnonSuggestionsBot
{
    internal class Program {
        private static DiscordSocketClient _client;
        private static IServiceProvider _services;
        private static Database _db = new Database();
        System.Security.Cryptography.MD5 _md5 = System.Security.Cryptography.MD5.Create();
        public static Task Main(string[] args) => new Program().MainAsync();

        public string stringToMD5(string input) {
            return Convert.ToHexString(_md5.ComputeHash(System.Text.Encoding.ASCII.GetBytes(input)));
        }

        public async Task MainAsync() {
            bool login = false;

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

            string token = await _db.getBotToken();

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

        private async Task ReadyAsync() {
            _client.SlashCommandExecuted += SlashCommandHandler;

            //initialize the global /initialize command
            var initializeCmd = new SlashCommandBuilder();
            initializeCmd.WithName("initialize");
            initializeCmd.WithDescription("initializes AnonSuggestionsBot for this server");
            initializeCmd.AddOption("input", ApplicationCommandOptionType.Channel, "the channel to post the create suggestions message in");
            initializeCmd.AddOption("output", ApplicationCommandOptionType.Channel, "the channel to send anonymous suggestions to");
            await _client.CreateGlobalApplicationCommandAsync(initializeCmd.Build());
        }

        private async Task SlashCommandHandler(SocketSlashCommand command) {
            if(command.GuildId == null) {
                await command.RespondAsync("Commands cannot be run in DMs");
                return;
            }

            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);
            bool userIsAdmin = guild.GetUser(command.User.Id).GuildPermissions.Administrator;
            bool userIsModerator = guild.GetUser(command.User.Id).GuildPermissions.KickMembers; //this isnt a great method of testing for mod rights, but it works (?)

            SocketRole? overrideRole = guild.Roles.Where(r => r.Name == "SuggestionPerms").FirstOrDefault();
            bool userHasOverrideRole = false;
            if (overrideRole != null) {
                userHasOverrideRole = guild.GetUser(command.User.Id).Roles.Contains(overrideRole);
            }

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

        }
        private async Task banSuggestionCreator(SocketSlashCommand command) {
            if(command.GuildId == null) { return; }

            if(command.Data.Options.ToArray().Count() < 1) {
                await command.RespondAsync("You must specify a suggestion UID");
                return;
            }   

            string? suggestion_uid = command.Data.Options.ToArray()[0].Value.ToString();

            if(suggestion_uid == null) {
                await command.RespondAsync("Suggestion UID is required");
                return;
            }

            string userHash = await _db.getUserHashFromSuggestionUID((ulong)command.GuildId, suggestion_uid);

            if(userHash == "") {
                await command.RespondAsync("Suggestion UID not found");
                return;
            }

            _db.createBanEntry((ulong)command.GuildId, userHash, 0, true);

            await command.RespondAsync("User has been banned from creating suggestions");
        }

        private async Task timeoutSuggestionCreator(SocketSlashCommand command) {
            await command.RespondAsync("Timeouts are not implemented yet, sorry!");
        }

        private async Task guildSetup(SocketSlashCommand command) {
            if(command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            if(command.Data.Options.Count() < 2) {
                await command.RespondAsync("You must specify both an input text channel and output text channel");
                return;
            }

            SocketGuildChannel? inputChannel = guild.Channels.FirstOrDefault(c => c.Name == command.Data.Options.ToArray()[0].Value.ToString());
            SocketGuildChannel? outputChannel = guild.Channels.FirstOrDefault(c => c.Name == command.Data.Options.ToArray()[1].Value.ToString());

            if (inputChannel == null || inputChannel.GetChannelType() != ChannelType.Text || outputChannel == null || outputChannel.GetChannelType() != ChannelType.Text) {
                await command.RespondAsync("You must specify both an input text channel and output text channel");
                return;
            }

            _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString());

            var suggestionBan = new SlashCommandBuilder();
            suggestionBan.WithName("suggestion-ban");
            suggestionBan.WithDescription("Bans the creator of suggestion");
            suggestionBan.AddOption("ID", ApplicationCommandOptionType.String, "the ID of the suggestion to ban the creator of");
            await guild.CreateApplicationCommandAsync(suggestionBan.Build());

            var suggestionTimeout = new SlashCommandBuilder();
            suggestionTimeout.WithName("suggestion-timeout");
            suggestionTimeout.WithDescription("Timeouts the creator of a suggestion");
            suggestionTimeout.AddOption("ID", ApplicationCommandOptionType.String, "the ID of the suggestion to timeout the creator of");
            suggestionTimeout.AddOption("Length", ApplicationCommandOptionType.Number, "timeout length in minutes");
            await guild.CreateApplicationCommandAsync(suggestionTimeout.Build());

            var btnBuilder = new ComponentBuilder().WithButton("Suggest", "btn-send-suggestion");
            await guild.GetTextChannel(inputChannel.Id).SendMessageAsync("Click here to submit a suggestion:", components: btnBuilder.Build());

            await command.RespondAsync("Initialization complete");
        }

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

        public async Task modalResponseHandler(SocketModal modal) {
            ulong? guildId = modal.GuildId;
            if(guildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)guildId);
            string title = modal.Data.Components.First(x => x.CustomId == "suggestion-input-title").Value;
            string body = modal.Data.Components.First(x => x.CustomId == "suggestion-input-body").Value;

            ulong outputChannelId = await _db.lookupServerOutputChannel((ulong)guildId);

            SocketTextChannel? outputChannel = guild.GetTextChannel(outputChannelId);

            if(outputChannel == null) { return; }

            bool userBanned = await _db.checkUserBanned((ulong)guildId, stringToMD5(modal.User.Id.ToString()));
            if(userBanned) {
                await modal.RespondAsync("You are banned from creating suggestions", ephemeral: true);
                return;
            }

            string suggestion_uid = createUid();
            _db.logSuggestion((ulong)guildId, stringToMD5(modal.User.Id.ToString()), suggestion_uid, title, body);

            var embed = new EmbedBuilder();
            embed.AddField(title, body);
            embed.WithFooter("Suggestion ID: " + suggestion_uid);

            await outputChannel.SendMessageAsync(embed: embed.Build());
            RequestOptions rs = new RequestOptions();
            await modal.RespondAsync("Suggestion sent!", ephemeral: true);
        }
    }
}