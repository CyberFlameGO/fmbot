using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using FMBot.Bot.Attributes;
using FMBot.Bot.Configurations;
using FMBot.Bot.Extensions;
using FMBot.Bot.Interfaces;
using FMBot.Bot.Resources;
using FMBot.Bot.Services;
using FMBot.Bot.Services.Guild;
using FMBot.Bot.Services.ThirdParty;
using FMBot.Bot.Services.WhoKnows;
using FMBot.Domain;
using FMBot.Domain.Models;
using FMBot.LastFM.Domain.Types;
using FMBot.LastFM.Services;
using Interactivity;

namespace FMBot.Bot.Commands.LastFM
{
    [Name("Plays")]
    public class PlayCommands : ModuleBase
    {
        private readonly CensorService _censorService;
        private readonly GuildService _guildService;
        private readonly IIndexService _indexService;
        private readonly IPrefixService _prefixService;
        private readonly IUpdateService _updateService;
        private readonly LastFmService _lastFmService;
        private readonly PlayService _playService;
        private readonly SettingService _settingService;
        private readonly UserService _userService;
        private readonly WhoKnowsPlayService _whoKnowsPlayService;
        private readonly WhoKnowsArtistService _whoKnowsArtistService;
        private readonly WhoKnowsAlbumService _whoKnowsAlbumService;
        private readonly WhoKnowsTrackService _whoKnowsTrackService;
        private InteractivityService Interactivity { get; }

        private readonly EmbedAuthorBuilder _embedAuthor;
        private readonly EmbedBuilder _embed;
        private readonly EmbedFooterBuilder _embedFooter;

        private static readonly List<DateTimeOffset> StackCooldownTimer = new();
        private static readonly List<SocketUser> StackCooldownTarget = new();


        public PlayCommands(
                GuildService guildService,
                IIndexService indexService,
                IPrefixService prefixService,
                IUpdateService updateService,
                LastFmService lastFmService,
                PlayService playService,
                SettingService settingService,
                UserService userService,
                WhoKnowsPlayService whoKnowsPlayService,
                CensorService censorService,
                WhoKnowsArtistService whoKnowsArtistService,
                WhoKnowsAlbumService whoKnowsAlbumService,
                WhoKnowsTrackService whoKnowsTrackService,
                InteractivityService interactivity)
        {
            this._guildService = guildService;
            this._indexService = indexService;
            this._lastFmService = lastFmService;
            this._playService = playService;
            this._prefixService = prefixService;
            this._settingService = settingService;
            this._updateService = updateService;
            this._userService = userService;
            this._whoKnowsPlayService = whoKnowsPlayService;
            this._censorService = censorService;
            this._whoKnowsArtistService = whoKnowsArtistService;
            this._whoKnowsAlbumService = whoKnowsAlbumService;
            this._whoKnowsTrackService = whoKnowsTrackService;
            this.Interactivity = interactivity;

            this._embedAuthor = new EmbedAuthorBuilder();
            this._embed = new EmbedBuilder()
                .WithColor(DiscordConstants.LastFmColorRed);
            this._embedFooter = new EmbedFooterBuilder();
        }

        [Command("fm", RunMode = RunMode.Async)]
        [Summary("Displays what a user is listening to.")]
        [Alias("np", "qm", "wm", "em", "rm", "tm", "ym", "om", "pm", "gm", "sm", "am", "hm", "jm", "km",
            "lm", "zm", "xm", "cm", "vm", "bm", "nm", "mm", "lastfm", "nowplaying")]
        [UsernameSetRequired]
        public async Task NowPlayingAsync(params string[] parameters)
        {
            var userSettings = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id) ?? ConfigData.Data.Bot.Prefix;

            if (parameters.Length > 0 && parameters.First() == "set")
            {
                await ReplyAsync(
                    "Please remove the space between `.fm` and `set` to set your last.fm username.");
                this.Context.LogCommandUsed(CommandResponse.WrongInput);
                return;
            }
            if (userSettings?.UserNameLastFM == null)
            {
                var userNickname = (this.Context.User as SocketGuildUser)?.Nickname;
                this._embed.UsernameNotSetErrorResponse(prfx, userNickname ?? this.Context.User.Username);

                await ReplyAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed(CommandResponse.UsernameNotSet);
                return;
            }
            if (parameters.Length > 0 && parameters.First() == "help")
            {
                var fmString = "fm";
                if (prfx == ".fm")
                {
                    fmString = "";
                }

                var replyString = $"`{prfx}{fmString}` shows you your last scrobble(s). \n " +
                                  $"This command can also be used on others, for example `{prfx}{fmString} lastfmusername` or `{prfx}{fmString} @discorduser`\n \n" +

                                  $"You can change your .fm mode and displayed count with the `{prfx}mode` command.\n";

                var differentMode = userSettings.FmEmbedType == FmEmbedType.EmbedMini ? "embedfull" : "embedmini";
                replyString += $"`{prfx}mode {differentMode}` \n \n" +
                               $"For more info, use `{prfx}mode help`.";


                this._embed.WithUrl($"{Constants.DocsUrl}/commands/tracks/");
                this._embed.WithTitle($"Using the {prfx}{fmString} command");
                this._embed.WithDescription(replyString);
                this._embed.WithFooter("For more information on the bot in general, use .fmhelp");

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            try
            {
                var existingFmCooldown = await this._guildService.GetChannelCooldown(this.Context.Channel.Id);
                if (existingFmCooldown.HasValue)
                {
                    var msg = this.Context.Message as SocketUserMessage;
                    if (StackCooldownTarget.Contains(this.Context.Message.Author))
                    {
                        if (StackCooldownTimer[StackCooldownTarget.IndexOf(msg.Author)].AddSeconds(existingFmCooldown.Value) >= DateTimeOffset.Now)
                        {
                            var secondsLeft = (int)(StackCooldownTimer[
                                    StackCooldownTarget.IndexOf(this.Context.Message.Author as SocketGuildUser)]
                                .AddSeconds(existingFmCooldown.Value) - DateTimeOffset.Now).TotalSeconds;
                            if (secondsLeft <= existingFmCooldown.Value - 2)
                            {
                                this.Interactivity.DelayedDeleteMessageAsync(
                                    await this.Context.Channel.SendMessageAsync($"This channel has a `{existingFmCooldown.Value}` second cooldown on `.fm`. Please wait for this to expire before using this command again."),
                                    TimeSpan.FromSeconds(6));
                                this.Context.LogCommandUsed(CommandResponse.Cooldown);
                            }

                            return;
                        }

                        StackCooldownTimer[StackCooldownTarget.IndexOf(msg.Author)] = DateTimeOffset.Now;
                    }
                    else
                    {
                        StackCooldownTarget.Add(msg.Author);
                        StackCooldownTimer.Add(DateTimeOffset.Now);
                    }
                }


                var lastFmUserName = userSettings.UserNameLastFM;
                var self = true;

                if (parameters.Length > 0 && !string.IsNullOrEmpty(parameters.First()) && parameters.Count() == 1)
                {
                    var alternativeLastFmUserName = await FindUser(parameters.First());
                    if (!string.IsNullOrEmpty(alternativeLastFmUserName))
                    {
                        lastFmUserName = alternativeLastFmUserName;
                        self = false;
                    }
                }

                _ = this.Context.Channel.TriggerTypingAsync();

                string sessionKey = null;
                if (self && !string.IsNullOrEmpty(userSettings.SessionKeyLastFm))
                {
                    sessionKey = userSettings.SessionKeyLastFm;
                }

                Response<RecentTrackList> recentTracks;

                if (self)
                {
                    if (userSettings.LastIndexed == null)
                    {
                        _ = this._indexService.IndexUser(userSettings);
                        recentTracks = await this._lastFmService.GetRecentTracksAsync(lastFmUserName, useCache: true, sessionKey: sessionKey);
                    }
                    else
                    {
                        recentTracks = await this._updateService.UpdateUserAndGetRecentTracks(userSettings);
                    }
                }
                else
                {
                    recentTracks = await this._lastFmService.GetRecentTracksAsync(lastFmUserName, useCache: true);
                }

                long totalPlaycount = 0;

                var spotifyUsed = false;

                RecentTrack currentTrack;
                RecentTrack previousTrack = null;

                if (ErrorService.RecentScrobbleCallFailed(recentTracks, lastFmUserName))
                {
                    var listeningActivity =
                        this.Context.User.Activities.FirstOrDefault(a => a.Type == ActivityType.Listening);
                    if (listeningActivity != null)
                    {
                        var spotifyActivity = (SpotifyGame)listeningActivity;
                        currentTrack = SpotifyService.SpotifyGameToRecentTrack(spotifyActivity);
                        this._embed.Color = DiscordConstants.SpotifyColorGreen;
                        spotifyUsed = true;
                    }
                    else
                    {
                        await ErrorService.RecentScrobbleCallFailedReply(recentTracks, lastFmUserName, this.Context);
                        return;
                    }
                }
                else
                {
                    totalPlaycount = recentTracks.Content.TotalAmount;

                    currentTrack = recentTracks.Content.RecentTracks[0];
                    previousTrack = recentTracks.Content.RecentTracks.Count > 1 ? recentTracks.Content.RecentTracks[1] : null;
                    if (!self)
                    {
                        totalPlaycount = recentTracks.Content.TotalAmount;
                    }
                }

                if (self)
                {
                    this._whoKnowsPlayService.AddRecentPlayToCache(userSettings.UserId, currentTrack);
                }

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                var embedTitle = self ? userTitle : $"{lastFmUserName}, requested by {userTitle}";

                var fmText = "";
                var footerText = "";

                var embedType = userSettings.FmEmbedType;

                if (this.Context.Guild != null)
                {
                    var guild = await this._guildService.GetGuildAsync(this.Context.Guild.Id);
                    if (guild?.FmEmbedType != null)
                    {
                        embedType = guild.FmEmbedType.Value;
                    }
                }

                if (embedType == FmEmbedType.TextMini || embedType == FmEmbedType.TextFull || embedType == FmEmbedType.EmbedTiny)
                {
                    if (self)
                    {
                        footerText +=
                            $"{userTitle} has ";
                    }
                    else
                    {
                        footerText +=
                            $"{lastFmUserName} (requested by {userTitle}) has ";
                    }
                }
                else
                {
                    footerText +=
                        $"{lastFmUserName} has ";
                }

                if (self)
                {
                    switch (userSettings.FmCountType)
                    {
                        case FmCountType.Track:
                            var trackPlaycount = await WhoKnowsTrackService.GetTrackPlayCountForUser(currentTrack.ArtistName, currentTrack.TrackName, userSettings.UserId);
                            if (trackPlaycount.HasValue)
                            {
                                footerText += $"{trackPlaycount} scrobbles on this track | ";
                            }
                            break;
                        case FmCountType.Album:
                            if (!string.IsNullOrEmpty(currentTrack.AlbumName))
                            {
                                var albumPlaycount = await this._whoKnowsAlbumService.GetAlbumPlayCountForUser(currentTrack.ArtistName, currentTrack.AlbumName, userSettings.UserId);
                                if (albumPlaycount.HasValue)
                                {
                                    footerText += $"{albumPlaycount} scrobbles on this album | ";
                                }
                            }
                            break;
                        case FmCountType.Artist:
                            var artistPlaycount = await WhoKnowsArtistService.GetArtistPlayCountForUser(currentTrack.ArtistName, userSettings.UserId);
                            if (artistPlaycount.HasValue)
                            {
                                footerText += $"{artistPlaycount} scrobbles on this artist | ";
                            }
                            break;
                        case null:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                footerText += $"{totalPlaycount} total scrobbles";

                switch (embedType)
                {
                    case FmEmbedType.TextMini:
                    case FmEmbedType.TextFull:
                        if (embedType == FmEmbedType.TextMini)
                        {
                            fmText += LastFmService.TrackToString(currentTrack).FilterOutMentions();
                        }
                        else if (previousTrack != null)
                        {
                            fmText += $"**Current track**:\n";

                            fmText += LastFmService.TrackToString(currentTrack).FilterOutMentions();

                            fmText += $"\n" +
                                      $"**Previous track**:\n";

                            fmText += LastFmService.TrackToString(previousTrack).FilterOutMentions();
                        }

                        fmText +=
                            $"`{footerText.FilterOutMentions()}`";

                        await this.Context.Channel.SendMessageAsync(fmText);
                        break;
                    default:
                        if (embedType == FmEmbedType.EmbedMini || embedType == FmEmbedType.EmbedTiny)
                        {
                            fmText += LastFmService.TrackToLinkedString(currentTrack, userSettings.RymEnabled);
                            this._embed.WithDescription(fmText);
                        }
                        else if (previousTrack != null)
                        {
                            this._embed.AddField("Current:", LastFmService.TrackToLinkedString(currentTrack, userSettings.RymEnabled));
                            this._embed.AddField("Previous:", LastFmService.TrackToLinkedString(previousTrack, userSettings.RymEnabled));
                        }

                        string headerText;
                        if (currentTrack.NowPlaying)
                        {
                            headerText = "Now playing - ";
                        }
                        else
                        {
                            headerText = embedType == FmEmbedType.EmbedMini
                                ? "Last track for "
                                : "Last tracks for ";
                        }

                        headerText += embedTitle;

                        if (!currentTrack.NowPlaying && currentTrack.TimePlayed.HasValue)
                        {
                            footerText += " | Last scrobble:";
                            this._embed.WithTimestamp(currentTrack.TimePlayed.Value);
                        }

                        this._embedAuthor.WithName(headerText);
                        this._embedAuthor.WithUrl(recentTracks.Content.UserUrl);

                        if (this.Context.Guild != null && self)
                        {
                            var guildAlsoPlaying = await this._whoKnowsPlayService.GuildAlsoPlayingTrack(userSettings.UserId,
                                this.Context.Guild.Id, currentTrack.ArtistName, currentTrack.TrackName);

                            if (guildAlsoPlaying != null)
                            {
                                footerText += "\n";
                                footerText += guildAlsoPlaying;
                            }
                        }

                        if (spotifyUsed)
                        {
                            footerText = "\nSpotify status used due to no playing tracks on Last.fm";
                        }

                        if (!string.IsNullOrWhiteSpace(footerText))
                        {
                            this._embedFooter.WithText(footerText);
                            this._embed.WithFooter(this._embedFooter);
                        }

                        if (embedType != FmEmbedType.EmbedTiny)
                        {
                            this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                            this._embed.WithAuthor(this._embedAuthor);
                            this._embed.WithUrl(recentTracks.Content.UserUrl);
                        }

                        if (currentTrack.AlbumCoverUrl != null && embedType != FmEmbedType.EmbedTiny)
                        {
                            var safeForChannel = await this._censorService.IsSafeForChannel(this.Context,
                                currentTrack.AlbumName, currentTrack.ArtistName, currentTrack.AlbumCoverUrl);
                            if (safeForChannel)
                            {
                                this._embed.WithThumbnailUrl(currentTrack.AlbumCoverUrl);
                            }
                        }

                        var message = await ReplyAsync("", false, this._embed.Build());

                        try
                        {
                            if (!this._guildService.CheckIfDM(this.Context))
                            {
                                await this._guildService.AddReactionsAsync(message, this.Context.Guild);
                            }
                        }
                        catch (Exception e)
                        {
                            this.Context.LogCommandException(e, "Could not add emote reactions");
                            await ReplyAsync(
                                "Couldn't add emote reactions to `.fm`. If you have recently changed changed any of the configured emotes please use `.fmserverreactions` to reset the automatic emote reactions.");
                        }

                        break;
                }

                this.Context.LogCommandUsed();

                if (!this._guildService.CheckIfDM(this.Context))
                {
                    await this._indexService.UpdateUserNameWithoutGuildUser(await this.Context.Guild.GetUserAsync(userSettings.DiscordUserId), userSettings);
                }
            }
            catch (Exception e)
            {
                if (!string.IsNullOrEmpty(e.Message) && e.Message.Contains("The server responded with error 50013: Missing Permissions"))
                {
                    this.Context.LogCommandException(e);
                    await ReplyAsync("Error while replying: The bot is missing permissions.\n" +
                                     "Make sure it has permission to 'Embed links' and 'Attach Images'");
                }
                else
                {
                    this.Context.LogCommandException(e);
                    await ReplyAsync("Something went wrong while showing info from Last.fm. Please try again later or contact staff on our support server.");
                }
            }
        }

        [Command("recent", RunMode = RunMode.Async)]
        [Summary("Displays a user's recent tracks.")]
        [Alias("recenttracks", "recents", "r")]
        [UsernameSetRequired]
        public async Task RecentAsync([Remainder] string extraOptions = null)
        {
            var user = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id) ?? ConfigData.Data.Bot.Prefix;

            if (!string.IsNullOrWhiteSpace(extraOptions) && extraOptions.ToLower() == "help")
            {
                await ReplyAsync($"{prfx}recent 'number of items (max 8)' 'lastfm username/@discord user'");
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            _ = this.Context.Channel.TriggerTypingAsync();

            var userSettings = await this._settingService.GetUser(extraOptions, user, this.Context);
            var amount = SettingService.GetAmount(extraOptions, 5, 10);

            try
            {
                string sessionKey = null;
                if (!userSettings.DifferentUser && !string.IsNullOrEmpty(user.SessionKeyLastFm))
                {
                    sessionKey = user.SessionKeyLastFm;
                }

                var recentTracks = await this._lastFmService.GetRecentTracksAsync(userSettings.UserNameLastFm, amount, useCache: true, sessionKey: sessionKey);

                if (await ErrorService.RecentScrobbleCallFailedReply(recentTracks, userSettings.UserNameLastFm, this.Context))
                {
                    return;
                }

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                var title = !userSettings.DifferentUser ? userTitle : $"{userSettings.UserNameLastFm}, requested by {userTitle}";
                this._embedAuthor.WithName($"Latest tracks for {title}");

                this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                this._embedAuthor.WithUrl(recentTracks.Content.UserRecentTracksUrl);
                this._embed.WithAuthor(this._embedAuthor);

                var fmRecentText = "";
                var resultAmount = recentTracks.Content.RecentTracks.Count;
                if (recentTracks.Content.RecentTracks.Any(a => a.NowPlaying))
                {
                    resultAmount -= 1;
                }
                for (var i = 0; i < resultAmount; i++)
                {
                    var track = recentTracks.Content.RecentTracks[i];

                    if (i == 0)
                    {
                        if (track.AlbumCoverUrl != null)
                        {
                            this._embed.WithThumbnailUrl(track.AlbumCoverUrl);
                        }
                    }

                    var trackString = resultAmount > 6 ? LastFmService.TrackToString(track) : LastFmService.TrackToLinkedString(track, user.RymEnabled);

                    if (track.NowPlaying)
                    {
                        fmRecentText += $"🎶 - {trackString}\n";
                    }
                    else
                    {
                        fmRecentText += $"`{i + 1}` - {trackString}\n";
                    }
                }

                this._embed.WithDescription(fmRecentText);

                string footerText;
                var firstTrack = recentTracks.Content.RecentTracks[0];
                if (firstTrack.NowPlaying)
                {
                    footerText =
                        $"{userSettings.UserNameLastFm} has {recentTracks.Content.TotalAmount} scrobbles | Now Playing";
                }
                else
                {
                    footerText =
                        $"{userSettings.UserNameLastFm} has {recentTracks.Content.TotalAmount} scrobbles";

                    if (!firstTrack.NowPlaying && firstTrack.TimePlayed.HasValue)
                    {
                        footerText += " | Last scrobble:";
                        this._embed.WithTimestamp(firstTrack.TimePlayed.Value);
                    }
                }

                this._embedFooter.WithText(footerText);

                this._embed.WithFooter(this._embedFooter);

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync(
                    "Unable to show your recent tracks on Last.fm due to an internal error. Please try again later or contact .fmbot support.");
            }
        }

        [Command("overview", RunMode = RunMode.Async)]
        [Summary("Displays a week overview.")]
        [Alias("o", "ov")]
        [UsernameSetRequired]
        public async Task OverviewAsync(string amount = "4")
        {
            var userSettings = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id) ?? ConfigData.Data.Bot.Prefix;

            if (amount == "help")
            {
                await ReplyAsync($"{prfx}overview 'number of days (max 8)'");
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            if (!int.TryParse(amount, out var amountOfDays))
            {
                await ReplyAsync("Please enter a valid amount. \n" +
                                 $"`{prfx}overview 'number of days (max 8)'` \n" +
                                 $"Example: `{prfx}overview 8`");
                this.Context.LogCommandUsed(CommandResponse.WrongInput);
                return;
            }

            if (amountOfDays > 8)
            {
                amountOfDays = 8;
            }

            if (amountOfDays < 1)
            {
                amountOfDays = 1;
            }

            if (userSettings.LastIndexed == null)
            {
                _ = this.Context.Channel.TriggerTypingAsync();
                await this._indexService.IndexUser(userSettings);
            }
            else if (userSettings.LastUpdated < DateTime.UtcNow.AddMinutes(-20))
            {
                _ = this.Context.Channel.TriggerTypingAsync();
                await this._updateService.UpdateUser(userSettings);
            }

            try
            {
                var week = await this._playService.GetDailyOverview(userSettings, amountOfDays);

                foreach (var day in week.Days)
                {
                    this._embed.AddField(
                        $"{day.Playcount} plays - {day.Date.ToString("dddd MMMM d", CultureInfo.InvariantCulture)}",
                        $"{day.TopArtist}\n" +
                        $"{day.TopAlbum}\n" +
                        $"{day.TopTrack}"
                    );
                }

                var description = $"Top artist, album and track for last {amountOfDays} days";

                if (week.Days.Count < amountOfDays)
                {
                    description += $"\n{amountOfDays - week.Days.Count} days not shown because of no plays.";
                }

                this._embed.WithDescription(description);

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                this._embedAuthor.WithName($"Daily overview for {userTitle}");
                this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                this._embedAuthor.WithUrl($"{Constants.LastFMUserUrl}{userSettings.UserNameLastFM}/library?date_preset=LAST_7_DAYS");
                this._embed.WithAuthor(this._embedAuthor);

                this._embedFooter.WithText($"{week.Uniques} unique tracks - {week.Playcount} total plays - avg {Math.Round(week.AvgPerDay, 1)} per day");
                this._embed.WithFooter(this._embedFooter);

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync(
                    "Unable to show your overview on Last.fm due to an internal error. Please try again later or contact .fmbot support.");
            }
        }

        [Command("pace", RunMode = RunMode.Async)]
        [Summary("Displays the date a goal amount of scrobbles is reached")]
        [UsernameSetRequired]
        [Alias("pc")]
        public async Task PaceAsync([Remainder] string extraOptions = null)
        {
            var user = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id) ?? ConfigData.Data.Bot.Prefix;

            if (!string.IsNullOrWhiteSpace(extraOptions) && extraOptions.ToLower() == "help")
            {
                this._embed.WithTitle($"{prfx}pace");

                var helpDescription = new StringBuilder();
                helpDescription.AppendLine("Displays the date you reach a scrobble goal based on average scrobbles per day.");
                helpDescription.AppendLine();
                helpDescription.AppendLine($"Time periods: {Constants.CompactTimePeriodList}");
                helpDescription.AppendLine("Optional goal amount: For example `10000`");
                helpDescription.AppendLine("User to check pace for: Mention or user id");

                this._embed.WithDescription(helpDescription.ToString());

                this._embed.AddField("Examples",
                    $"`{prfx}pc` \n" +
                    $"`{prfx}pc 100000 q` \n" +
                    $"`{prfx}pc 40000 h @user` \n" +
                    $"`{prfx}pace` \n" +
                    $"`{prfx}pace yearly @user 250000`");

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            var userSettings = await this._settingService.GetUser(extraOptions, user, this.Context);
            var userInfo = await this._lastFmService.GetFullUserInfoAsync(userSettings.UserNameLastFm);

            var goalAmount = SettingService.GetGoalAmount(extraOptions, userInfo.Playcount);

            var timePeriodString = extraOptions;

            var timeType = SettingService.GetTimePeriod(timePeriodString, ChartTimePeriod.AllTime);

            long timeFrom;
            if (timeType.ChartTimePeriod != ChartTimePeriod.AllTime && timeType.PlayDays != null)
            {
                var dateAgo = DateTime.UtcNow.AddDays(-timeType.PlayDays.Value);
                timeFrom = ((DateTimeOffset)dateAgo).ToUnixTimeSeconds();
            }
            else
            {
                timeFrom = userInfo.Registered.Unixtime;
            }

            var count = await this._lastFmService.GetScrobbleCountFromDateAsync(userSettings.UserNameLastFm, timeFrom);

            if (count == null || count == 0)
            {
                var errorReply = $"<@{this.Context.User.Id}> No plays found in the {timeType.Description} time period.";

                await this.Context.Channel.SendMessageAsync(errorReply);
            }

            var age = DateTimeOffset.FromUnixTimeSeconds(timeFrom);
            var totalDays = (DateTime.UtcNow - age).TotalDays;

            var playsLeft = goalAmount - userInfo.Playcount;

            var avgPerDay = count / totalDays;

            var goalDate = (DateTime.Now.AddDays(playsLeft / avgPerDay.Value)).ToString("dd MMM yyyy");

            var reply = new StringBuilder();

            var determiner = "your";
            if (userSettings.DifferentUser)
            {
                reply.Append($"<@{this.Context.User.Id}> My estimate is that the user '{userSettings.UserNameLastFm.FilterOutMentions()}'");
                determiner = "their";
            }
            else
            {
                reply.Append($"<@{this.Context.User.Id}> My estimate is that you");
            }

            reply.AppendLine($" will reach **{goalAmount}** scrobbles on **{goalDate}**.");

            if (timeType.ChartTimePeriod == ChartTimePeriod.AllTime)
            {
                reply.AppendLine(
                    $"This is based on {determiner} alltime avg of {Math.Round(avgPerDay.GetValueOrDefault(0), 1)} per day. ({count} in {Math.Round(totalDays, 0)} days)");
            }
            else
            {
                reply.AppendLine(
                    $"This is based on {determiner} avg of {Math.Round(avgPerDay.GetValueOrDefault(0), 1)} per day in the last {Math.Round(totalDays, 0)} days ({count} total)");
            }

            await this.Context.Channel.SendMessageAsync(reply.ToString());

            this.Context.LogCommandUsed();
        }

        [Command("plays", RunMode = RunMode.Async)]
        [Summary("Displays track info and stats.")]
        [Alias("p", "scrobbles")]
        [UsernameSetRequired]
        public async Task PlaysAsync([Remainder] string extraOptions = null)
        {
            var user = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id) ?? ConfigData.Data.Bot.Prefix;

            if (!string.IsNullOrWhiteSpace(extraOptions) && extraOptions.ToLower() == "help")
            {
                this._embed.WithTitle($"{prfx}plays");
                this._embed.WithDescription($"Shows your total plays from the track you're currently listening to or searching for.");

                this._embed.AddField("Examples",
                    $"`{prfx}tp` \n" +
                    $"`{prfx}trackplays` \n" +
                    $"`{prfx}trackplays Mac DeMarco Here Comes The Cowboy` \n" +
                    $"`{prfx}trackplays Cocteau Twins | Heaven or Las Vegas`");

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            _ = this.Context.Channel.TriggerTypingAsync();

            var userSettings = await this._settingService.GetUser(extraOptions, user, this.Context, true);

            var count = await this._lastFmService.GetScrobbleCountFromDateAsync(userSettings.UserNameLastFm);

            var userTitle = $"{userSettings.DiscordUserName.FilterOutMentions()}{userSettings.UserType.UserTypeToIcon()}";

            var reply =
                $"**{userTitle}** has `{count}` total scrobbles";


            await this.Context.Channel.SendMessageAsync(reply);
            this.Context.LogCommandUsed();
        }

        [Command("streak", RunMode = RunMode.Async)]
        [Summary("Shows you your streak")]
        [UsernameSetRequired]
        [Alias("str", "combo", "cb")]
        public async Task StreakAsync([Remainder] string extraOptions = null)
        {
            var user = await this._userService.GetUserSettingsAsync(this.Context.User);

            _ = this.Context.Channel.TriggerTypingAsync();

            if (user.LastIndexed == null)
            {
                await this._indexService.IndexUser(user);
            }

            try
            {
                var userSettings = await this._settingService.GetUser(extraOptions, user, this.Context);

                var userWithStreak = await this._userService.GetUserAsync(userSettings.DiscordUserId);

                var recentTracks = await this._updateService.UpdateUserAndGetRecentTracks(userWithStreak);

                if (await ErrorService.RecentScrobbleCallFailedReply(recentTracks, userSettings.UserNameLastFm,
                    this.Context))
                {
                    return;
                }

                var streak = await this._playService.GetStreak(userSettings.UserId, recentTracks);
                this._embed.WithDescription(streak);

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);

                this._embedAuthor.WithName($"{userTitle} streak overview");
                this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                this._embedAuthor.WithUrl($"{Constants.LastFMUserUrl}{userSettings.UserNameLastFm}/library");
                this._embed.WithAuthor(this._embedAuthor);

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync("Something went wrong while showing streak and the error has been logged. Please try again later or contact staff on our support server.");
            }
        }

        private async Task<string> FindUser(string user)
        {
            if (await this._lastFmService.LastFmUserExistsAsync(user))
            {
                return user;
            }

            if (!this._guildService.CheckIfDM(this.Context))
            {
                var guildUser = await this._settingService.GetUserFromString(user);

                if (guildUser != null)
                {
                    return guildUser.UserNameLastFM;
                }
            }

            return null;
        }
    }
}
