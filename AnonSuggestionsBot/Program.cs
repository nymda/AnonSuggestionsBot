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
        public static Task Main(string[] args) => new Program().MainAsync();

        public async Task MainAsync() {
            Console.Write("Enter postgresql IP: ");
            string? IPAddy = Console.ReadLine();
            Console.Write("\nEnter postgresql password: ");
            string? password = Console.ReadLine();
            Console.Clear();

            _db.login(IPAddy, password); //login to the local DB        

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
            if(command.CommandName == "initialize") {
                await guildSetup(command);
            }
        }

        private async Task guildSetup(SocketSlashCommand command) {
            if(command.GuildId == null) { return; }
            SocketGuild guild = _client.GetGuild((ulong)command.GuildId);

            if(command.Data.Options.Count() < 2) {
                await command.RespondAsync("You must specify both an input and output channel");
                return;
            }

            SocketGuildChannel? inputChannel = guild.Channels.FirstOrDefault(c => c.Name == command.Data.Options.ToArray()[0].Value.ToString());
            SocketGuildChannel? outputChannel = guild.Channels.FirstOrDefault(c => c.Name == command.Data.Options.ToArray()[1].Value.ToString());

            if (inputChannel == null || outputChannel == null) {
                await command.RespondAsync("You must specify both an input and output channel");
                return;
            }

            _db.createServerEntry((ulong)command.GuildId, inputChannel.Id.ToString(), outputChannel.Id.ToString());

            var suggestionBan = new SlashCommandBuilder();
            suggestionBan.WithName("suggestion-ban");
            suggestionBan.WithDescription("Bans the creator of suggestion");
            suggestionBan.AddOption("suggestion", ApplicationCommandOptionType.String, "the ID of the suggestion to ban the creator of");
            await guild.CreateApplicationCommandAsync(suggestionBan.Build());

            var btnBuilder = new ComponentBuilder().WithButton("Suggest", "btn-send-suggestion");
            await guild.GetTextChannel(inputChannel.Id).SendMessageAsync("Click here to submit a suggestion:", components: btnBuilder.Build());

            await command.RespondAsync("Initialization complete");
        }

        public async Task buttonHandler(SocketMessageComponent component) {
        if(component.Data.CustomId == "btn-send-suggestion") {
                var mb = new ModalBuilder();
                mb.WithTitle("AnonSuggestionBot");
                mb.WithCustomId("suggestion-input");
                mb.AddTextInput("Title:", "suggestion-input-title", TextInputStyle.Short);
                mb.AddTextInput("Suggestion:", "suggestion-input-body", TextInputStyle.Paragraph);

                await component.RespondWithModalAsync(mb.Build());
            }
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

            var embed = new EmbedBuilder();
            embed.AddField(title, body);
            embed.WithFooter("Suggestion ID: placeholder");

            await outputChannel.SendMessageAsync(embed: embed.Build());

            await modal.RespondAsync();
        }
    }
}