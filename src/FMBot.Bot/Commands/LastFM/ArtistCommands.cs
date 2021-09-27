using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Discord.Commands;
using Fergun.Interactive;
using FMBot.Bot.Attributes;
using FMBot.Bot.Extensions;
using FMBot.Bot.Interfaces;
using FMBot.Bot.Models;
using FMBot.Bot.Resources;
using FMBot.Bot.Services;
using FMBot.Bot.Services.Guild;
using FMBot.Bot.Services.ThirdParty;
using FMBot.Bot.Services.WhoKnows;
using FMBot.Domain;
using FMBot.Domain.Models;
using FMBot.LastFM.Domain.Enums;
using FMBot.LastFM.Domain.Types;
using FMBot.LastFM.Repositories;
using FMBot.Persistence.Domain.Models;
using Microsoft.Extensions.Options;
using TimePeriod = FMBot.Domain.Models.TimePeriod;

namespace FMBot.Bot.Commands.LastFM
{
    [Name("Artists")]
    public class ArtistCommands : BaseCommandModule
    {
        private readonly ArtistsService _artistsService;
        private readonly CrownService _crownService;
        private readonly GuildService _guildService;
        private readonly IIndexService _indexService;

        private readonly IPrefixService _prefixService;
        private readonly IUpdateService _updateService;
        private readonly LastFmRepository _lastFmRepository;
        private readonly PlayService _playService;
        private readonly SettingService _settingService;
        private readonly SpotifyService _spotifyService;
        private readonly UserService _userService;
        private readonly GenreService _genreService;
        private readonly FriendsService _friendsService;
        private readonly WhoKnowsService _whoKnowsService;
        private readonly WhoKnowsArtistService _whoKnowArtistService;
        private readonly WhoKnowsPlayService _whoKnowsPlayService;

        private InteractiveService Interactivity { get; }

        public ArtistCommands(
                ArtistsService artistsService,
                CrownService crownService,
                GuildService guildService,
                IIndexService indexService,
                IPrefixService prefixService,
                IUpdateService updateService,
                LastFmRepository lastFmRepository,
                PlayService playService,
                SettingService settingService,
                SpotifyService spotifyService,
                UserService userService,
                WhoKnowsArtistService whoKnowsArtistService,
                WhoKnowsPlayService whoKnowsPlayService,
                InteractiveService interactivity,
                WhoKnowsService whoKnowsService,
                IOptions<BotSettings> botSettings,
                GenreService genreService,
                FriendsService friendsService) : base(botSettings)
        {
            this._artistsService = artistsService;
            this._crownService = crownService;
            this._guildService = guildService;
            this._indexService = indexService;
            this._lastFmRepository = lastFmRepository;
            this._playService = playService;
            this._prefixService = prefixService;
            this._settingService = settingService;
            this._spotifyService = spotifyService;
            this._updateService = updateService;
            this._userService = userService;
            this._whoKnowArtistService = whoKnowsArtistService;
            this._whoKnowsPlayService = whoKnowsPlayService;
            this.Interactivity = interactivity;
            this._whoKnowsService = whoKnowsService;
            this._genreService = genreService;
            this._friendsService = friendsService;
        }

        [Command("artist", RunMode = RunMode.Async)]
        [Summary("Displays information about the artist you're currently listening to or searching for.")]
        [Examples(
            "a",
            "artist",
            "a Gorillaz",
            "artist Gamma Intel")]
        [Alias("a")]
        [UsernameSetRequired]
        public async Task ArtistAsync([Remainder] string artistValues = null)
        {
            var contextUser = await this._userService.GetUserSettingsAsync(this.Context.User);

            _ = this.Context.Channel.TriggerTypingAsync();

            var artist = await GetArtist(artistValues, contextUser.UserNameLastFM, contextUser.SessionKeyLastFm);
            if (artist == null)
            {
                return;
            }

            var spotifyArtistTask = this._spotifyService.GetOrStoreArtistAsync(artist, artistValues);

            var spotifyArtist = await spotifyArtistTask;

            var footer = new StringBuilder();
            if (spotifyArtist.SpotifyImageUrl != null)
            {
                this._embed.WithThumbnailUrl(spotifyArtist.SpotifyImageUrl);
                footer.AppendLine("Image source: Spotify");
            }

            var userTitle = await this._userService.GetUserTitleAsync(this.Context);

            this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
            this._embedAuthor.WithName($"Artist info about {artist.ArtistName} for {userTitle}");
            this._embedAuthor.WithUrl(artist.ArtistUrl);
            this._embed.WithAuthor(this._embedAuthor);

            if (!this._guildService.CheckIfDM(this.Context))
            {
                var serverStats = "";
                var guild = await this._guildService.GetFullGuildAsync(this.Context.Guild.Id);

                if (guild?.LastIndexed != null)
                {
                    var usersWithArtist = await this._whoKnowArtistService.GetIndexedUsersForArtist(this.Context, guild.GuildId, artist.ArtistName);
                    var filteredUsersWithArtist = WhoKnowsService.FilterGuildUsersAsync(usersWithArtist, guild);

                    if (filteredUsersWithArtist.Count != 0)
                    {
                        var serverListeners = filteredUsersWithArtist.Count;
                        var serverPlaycount = filteredUsersWithArtist.Sum(a => a.Playcount);
                        var avgServerPlaycount = filteredUsersWithArtist.Average(a => a.Playcount);
                        var serverPlaycountLastWeek = await this._whoKnowArtistService.GetWeekArtistPlaycountForGuildAsync(guild.GuildId, artist.ArtistName);

                        serverStats += $"`{serverListeners}` {StringExtensions.GetListenersString(serverListeners)}";
                        serverStats += $"\n`{serverPlaycount}` total {StringExtensions.GetPlaysString(serverPlaycount)}";
                        serverStats += $"\n`{(int)avgServerPlaycount}` avg {StringExtensions.GetPlaysString((int)avgServerPlaycount)}";
                        serverStats += $"\n`{serverPlaycountLastWeek}` {StringExtensions.GetPlaysString(serverPlaycountLastWeek)} last week";

                        if (usersWithArtist.Count > filteredUsersWithArtist.Count)
                        {
                            var filteredAmount = usersWithArtist.Count - filteredUsersWithArtist.Count;
                            serverStats += $"\n`{filteredAmount}` users filtered";
                        }
                    }
                }
                else
                {
                    var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);
                    serverStats += $"Run `{prfx}index` to get server stats";
                }

                if (!string.IsNullOrWhiteSpace(serverStats))
                {
                    this._embed.AddField("Server stats", serverStats, true);
                }
            }

            var globalStats = "";
            globalStats += $"`{artist.TotalListeners}` {StringExtensions.GetListenersString(artist.TotalListeners)}";
            globalStats += $"\n`{artist.TotalPlaycount}` global {StringExtensions.GetPlaysString(artist.TotalPlaycount)}";
            if (artist.UserPlaycount.HasValue)
            {
                globalStats += $"\n`{artist.UserPlaycount}` {StringExtensions.GetPlaysString(artist.UserPlaycount)} by you";
                globalStats += $"\n`{await this._playService.GetWeekArtistPlaycountAsync(contextUser.UserId, artist.ArtistName)}` by you last week";
                await this._updateService.CorrectUserArtistPlaycount(contextUser.UserId, artist.ArtistName,
                    artist.UserPlaycount.Value);
            }

            this._embed.AddField("Last.fm stats", globalStats, true);

            if (artist.Description != null)
            {
                this._embed.AddField("Summary", artist.Description);
            }

            if (artist.Tags != null && artist.Tags.Any() && (spotifyArtist.ArtistGenres == null || !spotifyArtist.ArtistGenres.Any()))
            {
                var tags = LastFmRepository.TagsToLinkedString(artist.Tags);

                this._embed.AddField("Tags", tags);
            }

            if (spotifyArtist.ArtistGenres != null && spotifyArtist.ArtistGenres.Any())
            {
                footer.AppendLine(GenreService.GenresToString(spotifyArtist.ArtistGenres.ToList()));
            }

            this._embed.WithFooter(footer.ToString());
            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

            this.Context.LogCommandUsed();
        }

        [Command("artisttracks", RunMode = RunMode.Async)]
        [Summary("Displays your top tracks for an artist.")]
        [Examples(
            "at",
            "artisttracks",
            "artisttracks DMX")]
        [Alias("at", "att", "artisttrack", "artist track", "artist tracks", "artistrack", "artisttoptracks", "artisttoptrack", "favs")]
        [UsernameSetRequired]
        public async Task ArtistTracksAsync([Remainder] string artistValues = null)
        {
            var contextUser = await this._userService.GetUserSettingsAsync(this.Context.User);

            _ = this.Context.Channel.TriggerTypingAsync();

            var timeSettings = SettingService.GetTimePeriod(artistValues, TimePeriod.AllTime);

            var artist = await GetArtist(artistValues, contextUser.UserNameLastFM, contextUser.SessionKeyLastFm);
            if (artist == null)
            {
                return;
            }

            var pages = new List<PageBuilder>();

            var timeDescription = timeSettings.Description.ToLower();
            List<UserTrack> topTracks;
            switch (timeSettings.TimePeriod)
            {
                case TimePeriod.Weekly:
                    topTracks = await this._playService.GetTopTracksForArtist(contextUser.UserId, 7, artist.ArtistName);
                    break;
                case TimePeriod.Monthly:
                    topTracks = await this._playService.GetTopTracksForArtist(contextUser.UserId, 31, artist.ArtistName);
                    break;
                default:
                    timeDescription = "alltime";
                    topTracks = await this._artistsService.GetTopTracksForArtist(contextUser.UserId, artist.ArtistName);
                    break;
            }

            var userTitle = await this._userService.GetUserTitleAsync(this.Context);

            if (topTracks.Count == 0)
            {
                this._embed.WithDescription(
                    $"{userTitle} has no registered tracks for {artist.ArtistName} in .fmbot.");
                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                return;
            }

            this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
            this._embedAuthor.WithName($"Your top {timeDescription} tracks for '{artist.ArtistName}'");

            var url = $"{Constants.LastFMUserUrl}{contextUser.UserNameLastFM}/library/music/{UrlEncoder.Default.Encode(artist.ArtistName)}";
            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                this._embedAuthor.WithUrl(url);
            }

            var topTrackPages = topTracks.ChunkBy(10);

            var counter = 1;
            var pageCounter = 1;
            foreach (var topTrackPage in topTrackPages)
            {
                var albumPageString = new StringBuilder();
                foreach (var track in topTrackPage)
                {
                    albumPageString.AppendLine($"{counter}. **{track.Name}** ({track.Playcount} {StringExtensions.GetPlaysString(track.Playcount)})");
                    counter++;
                }

                var footer = $"Page {pageCounter}/{topTrackPages.Count} - {userTitle} has {artist.UserPlaycount} total scrobbles on this artist";

                pages.Add(new PageBuilder()
                    .WithDescription(albumPageString.ToString())
                    .WithAuthor(this._embedAuthor)
                    .WithFooter(footer));
                pageCounter++;
            }

            var paginator = StringService.BuildStaticPaginator(pages);

            _ = this.Interactivity.SendPaginatorAsync(paginator, this.Context.Channel, TimeSpan.FromMinutes(DiscordConstants.PaginationTimeoutInSeconds));

            this.Context.LogCommandUsed();
        }

        [Command("artistalbums", RunMode = RunMode.Async)]
        [Summary("Displays your top albums for an artist.")]
        [Examples(
            "aa",
            "artistalbums",
            "artistalbums The Prodigy")]
        [Alias("aa", "aab", "atab", "artistalbum", "artist album", "artist albums", "artistopalbum", "artisttopalbums", "artisttab")]
        [UsernameSetRequired]
        public async Task ArtistAlbumsAsync([Remainder] string artistValues = null)
        {
            var user = await this._userService.GetUserSettingsAsync(this.Context.User);

            _ = this.Context.Channel.TriggerTypingAsync();

            var artist = await GetArtist(artistValues, user.UserNameLastFM, user.SessionKeyLastFm);
            if (artist == null)
            {
                return;
            }

            var topAlbums = await this._artistsService.GetTopAlbumsForArtist(user.UserId, artist.ArtistName);

            var userTitle = await this._userService.GetUserTitleAsync(this.Context);
            if (topAlbums.Count == 0)
            {
                this._embed.WithDescription(
                    $"{userTitle} has no scrobbles for this artist or their scrobbles have no album associated with them.");
            }
            else
            {
                var description = new StringBuilder();
                for (var i = 0; i < topAlbums.Count; i++)
                {
                    var album = topAlbums[i];

                    description.AppendLine($"{i + 1}. **{album.Name}** ({album.Playcount} plays)");
                }

                this._embed.WithDescription(description.ToString());

                this._embed.WithFooter($"{userTitle} has {artist.UserPlaycount} total scrobbles on this artist");
            }

            this._embed.WithTitle($"Your top albums for '{artist.ArtistName}'");

            var url = $"{Constants.LastFMUserUrl}{user.UserNameLastFM}/library/music/{UrlEncoder.Default.Encode(artist.ArtistName)}";
            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                this._embed.WithUrl(url);
            }

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("artistplays", RunMode = RunMode.Async)]
        [Summary("Displays playcount for the artist you're currently listening to or searching for.\n\n" +
                 "You can also mention another user to see their playcount.")]
        [Examples(
            "ap",
            "artistplays",
            "albumplays @user",
            "ap lfm:fm-bot",
            "artistplays Mall Grab @user")]
        [Alias("ap", "artist plays")]
        [UsernameSetRequired]
        public async Task ArtistPlaysAsync([Remainder] string artistValues = null)
        {
            var contextUser = await this._userService.GetUserSettingsAsync(this.Context.User);

            _ = this.Context.Channel.TriggerTypingAsync();

            var userSettings = await this._settingService.GetUser(artistValues, contextUser, this.Context);

            var artist = await GetArtist(userSettings.NewSearchValue, contextUser.UserNameLastFM, contextUser.SessionKeyLastFm, userSettings.UserNameLastFm);
            if (artist == null)
            {
                return;
            }

            var reply =
                $"**{userSettings.DiscordUserName.FilterOutMentions()}{userSettings.UserType.UserTypeToIcon()}** has " +
                $"`{artist.UserPlaycount}` {StringExtensions.GetPlaysString(artist.UserPlaycount)} for " +
                $"**{artist.ArtistName}**";

            if (!userSettings.DifferentUser && contextUser.LastUpdated != null)
            {
                var playsLastWeek =
                    await this._playService.GetWeekArtistPlaycountAsync(userSettings.UserId, artist.ArtistName);
                if (playsLastWeek != 0)
                {
                    reply += $" (`{playsLastWeek}` last week)";
                }
            }

            await this.Context.Channel.SendMessageAsync(reply);
            this.Context.LogCommandUsed();
        }

        [Command("topartists", RunMode = RunMode.Async)]
        [Summary("Shows a list of your or someone else their top artists over a certain time period.")]
        [Options(Constants.CompactTimePeriodList, Constants.UserMentionExample)]
        [Examples("ta", "topartists", "ta a lfm:fm-bot", "topartists weekly @user")]
        [Alias("al", "as", "ta", "artistlist", "artists", "top artists", "artistslist")]
        [UsernameSetRequired]
        [SupportsPagination]
        public async Task TopArtistsAsync([Remainder] string extraOptions = null)
        {
            var contextUser = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            _ = this.Context.Channel.TriggerTypingAsync();

            var timeSettings = SettingService.GetTimePeriod(extraOptions);
            var userSettings = await this._settingService.GetUser(extraOptions, contextUser, this.Context);

            var pages = new List<PageBuilder>();

            string userTitle;
            if (!userSettings.DifferentUser)
            {
                this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                userTitle = await this._userService.GetUserTitleAsync(this.Context);
            }
            else
            {
                userTitle =
                    $"{userSettings.UserNameLastFm}, requested by {await this._userService.GetUserTitleAsync(this.Context)}";
            }

            this._embedAuthor.WithName($"Top {timeSettings.Description.ToLower()} artists for {userTitle}");
            this._embedAuthor.WithUrl($"{Constants.LastFMUserUrl}{userSettings.UserNameLastFm}/library/artists?{timeSettings.UrlParameter}");

            try
            {
                Response<TopArtistList> artists;

                if (!timeSettings.UsePlays)
                {
                    artists = await this._lastFmRepository.GetTopArtistsAsync(userSettings.UserNameLastFm,
                        timeSettings.TimePeriod, 200);

                    if (!artists.Success || artists.Content == null)
                    {
                        this._embed.ErrorResponse(artists.Error, artists.Message, this.Context);
                        this.Context.LogCommandUsed(CommandResponse.LastFmError);
                        await ReplyAsync("", false, this._embed.Build());
                        return;
                    }
                    if (artists.Content.TopArtists == null)
                    {
                        this._embed.WithDescription("Sorry, you or the user you're searching for don't have any top artists in the selected time period.");
                        this.Context.LogCommandUsed(CommandResponse.NoScrobbles);
                        await ReplyAsync("", false, this._embed.Build());
                        return;
                    }
                }
                else
                {
                    int userId;
                    if (userSettings.DifferentUser)
                    {
                        var otherUser = await this._userService.GetUserAsync(userSettings.DiscordUserId);
                        if (otherUser.LastIndexed == null)
                        {
                            await this._indexService.IndexUser(otherUser);
                        }
                        else if (contextUser.LastUpdated < DateTime.UtcNow.AddMinutes(-15))
                        {
                            await this._updateService.UpdateUser(otherUser);
                        }

                        userId = otherUser.UserId;
                    }
                    else
                    {
                        if (contextUser.LastIndexed == null)
                        {
                            await this._indexService.IndexUser(contextUser);
                        }
                        else if (contextUser.LastUpdated < DateTime.UtcNow.AddMinutes(-15))
                        {
                            await this._updateService.UpdateUser(contextUser);
                        }

                        userId = contextUser.UserId;
                    }

                    artists = new Response<TopArtistList>
                    {
                        Content = await this._playService.GetTopArtists(userId,
                            timeSettings.PlayDays.GetValueOrDefault())
                    };
                }

                var artistPages = artists.Content.TopArtists.ChunkBy(10);

                var counter = 1;
                var pageCounter = 1;
                foreach (var artistPage in artistPages)
                {
                    var artistPageString = new StringBuilder();
                    foreach (var artist in artistPage)
                    {
                        artistPageString.AppendLine($"{counter}. **[{artist.ArtistName}]({artist.ArtistUrl})** ({artist.UserPlaycount} {StringExtensions.GetPlaysString(artist.UserPlaycount)})");
                        counter++;
                    }

                    var footer = $"Page {pageCounter}/{artistPages.Count} - {artists.Content.TotalAmount} different artists in this time period";

                    pages.Add(new PageBuilder()
                        .WithDescription(artistPageString.ToString())
                        .WithAuthor(this._embedAuthor)
                        .WithFooter(footer));
                    pageCounter++;
                }

                var paginator = StringService.BuildStaticPaginator(pages);

                _ = this.Interactivity.SendPaginatorAsync(paginator, this.Context.Channel, TimeSpan.FromSeconds(DiscordConstants.PaginationTimeoutInSeconds));

                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync("Unable to show Last.fm info due to an internal error.");
            }
        }

        [Command("taste", RunMode = RunMode.Async)]
        [Summary("Compares your top artists to another users top artists.")]
        [Options(Constants.CompactTimePeriodList, Constants.UserMentionOrLfmUserNameExample, "Mode: `table` or `embed`")]
        [Examples("t frikandel_", "t @user", "taste bitldev", "taste @user monthly embed")]
        [UsernameSetRequired]
        [Alias("t")]
        public async Task TasteAsync(string user = null, [Remainder] string extraOptions = null)
        {
            var userSettings = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            if (user == "help")
            {
                await ReplyAsync(
                    $"Usage: `{prfx}taste 'last.fm username/ discord mention' '{Constants.CompactTimePeriodList}' 'table/embed'`");
                this.Context.LogCommandUsed(CommandResponse.Help);
                return;
            }

            _ = this.Context.Channel.TriggerTypingAsync();

            var timePeriodString = extraOptions;

            var timeType = SettingService.GetTimePeriod(
                timePeriodString,
                TimePeriod.AllTime);

            var tasteSettings = new TasteSettings
            {
                ChartTimePeriod = timeType.TimePeriod
            };

            tasteSettings = this._artistsService.SetTasteSettings(tasteSettings, extraOptions);

            try
            {
                var ownLastFmUsername = userSettings.UserNameLastFM;
                string lastfmToCompare = null;

                if (user != null)
                {
                    string alternativeLastFmUserName;

                    if (await this._lastFmRepository.LastFmUserExistsAsync(user))
                    {
                        alternativeLastFmUserName = user;
                    }
                    else
                    {
                        var otherUser = await this._settingService.StringWithDiscordIdForUser(user);

                        alternativeLastFmUserName = otherUser?.UserNameLastFM;
                    }

                    if (!string.IsNullOrEmpty(alternativeLastFmUserName))
                    {
                        lastfmToCompare = alternativeLastFmUserName;
                    }
                }

                if (lastfmToCompare == null)
                {
                    this._embed.WithDescription($"Please enter a Last.fm username or mention someone to compare yourself to.\n" +
                                                $"Examples:\n" +
                                                $"- `{prfx}taste fm-bot`\n" +
                                                $"- `{prfx}taste @.fmbot`");
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                    this.Context.LogCommandUsed(CommandResponse.WrongInput);
                    return;
                }
                if (lastfmToCompare.ToLower() == userSettings.UserNameLastFM.ToLower())
                {
                    this._embed.WithDescription($"You can't compare your own taste with yourself. For viewing your top artists, use `{prfx}topartists`.\n\n" +
                                                $"Please enter a Last.fm username or mention someone to compare yourself to.\n" +
                                                $"Examples:\n" +
                                                $"- `{prfx}taste fm-bot`\n" +
                                                $"- `{prfx}taste @.fmbot`");
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                    this.Context.LogCommandUsed(CommandResponse.WrongInput);
                    return;
                }

                tasteSettings.OtherUserLastFmUsername = lastfmToCompare;

                var ownArtistsTask = this._lastFmRepository.GetTopArtistsAsync(ownLastFmUsername, timeType.TimePeriod, 1000);
                var otherArtistsTask = this._lastFmRepository.GetTopArtistsAsync(lastfmToCompare, timeType.TimePeriod, 1000);

                var ownArtists = await ownArtistsTask;
                var otherArtists = await otherArtistsTask;


                if (!ownArtists.Success || ownArtists.Content == null || !otherArtists.Success || otherArtists.Content == null)
                {
                    this._embed.ErrorResponse(ownArtists.Error, ownArtists.Message, this.Context);
                    this.Context.LogCommandUsed(CommandResponse.LastFmError);
                    await ReplyAsync("", false, this._embed.Build());
                    return;
                }

                if (ownArtists.Content.TopArtists == null || ownArtists.Content.TopArtists.Count == 0 || otherArtists.Content.TopArtists == null || otherArtists.Content.TopArtists.Count == 0)
                {
                    await ReplyAsync(
                        $"Sorry, you or the other user don't have any artist plays in the selected time period.");
                    this.Context.LogCommandUsed(CommandResponse.NoScrobbles);
                    return;
                }

                this._embedAuthor.WithIconUrl(this.Context.User.GetAvatarUrl());
                this._embedAuthor.WithName($"Top artist comparison - {ownLastFmUsername} vs {lastfmToCompare}");
                this._embedAuthor.WithUrl($"{Constants.LastFMUserUrl}{lastfmToCompare}/library/artists?{timeType.UrlParameter}");
                this._embed.WithAuthor(this._embedAuthor);

                int amount = 14;
                if (tasteSettings.TasteType == TasteType.FullEmbed)
                {
                    var taste = this._artistsService.GetEmbedTaste(ownArtists.Content, otherArtists.Content, amount, timeType.TimePeriod);

                    this._embed.WithDescription(taste.Description);
                    this._embed.AddField("Artist", taste.LeftDescription, true);
                    this._embed.AddField("Plays", taste.RightDescription, true);
                }
                else
                {
                    var taste = this._artistsService.GetTableTaste(ownArtists.Content, otherArtists.Content, amount, timeType.TimePeriod, ownLastFmUsername, lastfmToCompare);

                    this._embed.WithDescription(taste);
                }

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync("Unable to show Last.fm info due to an internal error.");
            }
        }

        [Command("whoknows", RunMode = RunMode.Async)]
        [Summary("Shows what other users listen to an artist in your server")]
        [Examples("w", "wk COMA", "whoknows", "whoknows DJ Seinfeld")]
        [Alias("w", "wk", "whoknows artist")]
        [UsernameSetRequired]
        [GuildOnly]
        [RequiresIndex]
        public async Task WhoKnowsAsync([Remainder] string artistValues = null)
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            try
            {
                var guildTask = this._guildService.GetFullGuildAsync(this.Context.Guild.Id);

                _ = this.Context.Channel.TriggerTypingAsync();

                var user = await this._userService.GetUserSettingsAsync(this.Context.User);

                string artistName;
                string artistUrl;
                string spotifyImageUrl;
                long? userPlaycount;

                var cachedArtist = await this._artistsService.GetArtistFromDatabase(artistValues);

                if (user.LastUpdated > DateTime.UtcNow.AddHours(-1) && cachedArtist != null)
                {
                    artistName = cachedArtist.Name;
                    artistUrl = cachedArtist.LastFmUrl;
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;

                    userPlaycount = await this._whoKnowArtistService.GetArtistPlayCountForUser(artistName, user.UserId);
                }
                else
                {
                    var artist = await GetArtist(artistValues, user.UserNameLastFM, user.SessionKeyLastFm);
                    if (artist == null)
                    {
                        return;
                    }

                    artistName = artist.ArtistName;
                    artistUrl = artist.ArtistUrl;

                    cachedArtist = await this._spotifyService.GetOrStoreArtistAsync(artist, artist.ArtistName);
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;
                    userPlaycount = artist.UserPlaycount;
                    if (userPlaycount.HasValue)
                    {
                        await this._updateService.CorrectUserArtistPlaycount(user.UserId, artist.ArtistName,
                            userPlaycount.Value);
                    }
                }

                var guild = await guildTask;

                var currentUser = await this._indexService.GetOrAddUserToGuild(guild, await this.Context.Guild.GetUserAsync(user.DiscordUserId), user);

                if (!guild.GuildUsers.Select(s => s.UserId).Contains(user.UserId))
                {
                    guild.GuildUsers.Add(currentUser);
                }

                await this._indexService.UpdateGuildUser(await this.Context.Guild.GetUserAsync(user.DiscordUserId), currentUser.UserId, guild);

                var usersWithArtist = await this._whoKnowArtistService.GetIndexedUsersForArtist(this.Context, guild.GuildId, artistName);

                if (userPlaycount != 0)
                {
                    usersWithArtist = WhoKnowsService.AddOrReplaceUserToIndexList(usersWithArtist, currentUser, artistName, userPlaycount);
                }

                var filteredUsersWithArtist = WhoKnowsService.FilterGuildUsersAsync(usersWithArtist, guild);

                CrownModel crownModel = null;
                if (guild.CrownsDisabled != true && filteredUsersWithArtist.Count >= 1)
                {
                    crownModel =
                        await this._crownService.GetAndUpdateCrownForArtist(filteredUsersWithArtist, guild, artistName);
                }

                var serverUsers = WhoKnowsService.WhoKnowsListToString(filteredUsersWithArtist, user.UserId, PrivacyLevel.Server, crownModel);
                if (filteredUsersWithArtist.Count == 0)
                {
                    serverUsers = "Nobody in this server (not even you) has listened to this artist.";
                }

                this._embed.WithDescription(serverUsers);

                var footer = "";
                if (cachedArtist?.ArtistGenres != null && cachedArtist.ArtistGenres.Any())
                {
                    footer += $"\n{GenreService.GenresToString(cachedArtist.ArtistGenres.ToList())}";
                }

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                footer += $"\nWhoKnows artist requested by {userTitle}";

                var rnd = new Random();
                var lastIndex = await this._guildService.GetGuildIndexTimestampAsync(this.Context.Guild);
                if (rnd.Next(0, 10) == 1 && lastIndex < DateTime.UtcNow.AddDays(-50))
                {
                    footer += $"\nMissing members? Update with {prfx}index";
                }

                if (filteredUsersWithArtist.Any() && filteredUsersWithArtist.Count > 1)
                {
                    var serverListeners = filteredUsersWithArtist.Count;
                    var serverPlaycount = filteredUsersWithArtist.Sum(a => a.Playcount);
                    var avgServerPlaycount = filteredUsersWithArtist.Average(a => a.Playcount);

                    footer += $"\n{serverListeners} {StringExtensions.GetListenersString(serverListeners)} - ";
                    footer += $"{serverPlaycount} total {StringExtensions.GetPlaysString(serverPlaycount)} - ";
                    footer += $"{(int)avgServerPlaycount} avg {StringExtensions.GetPlaysString((int)avgServerPlaycount)}";
                }

                var guildAlsoPlaying = await this._whoKnowsPlayService.GuildAlsoPlayingArtist(user.UserId,
                    this.Context.Guild.Id, artistName);

                if (guildAlsoPlaying != null)
                {
                    footer += "\n";
                    footer += guildAlsoPlaying;
                }

                if (usersWithArtist.Count > filteredUsersWithArtist.Count && !guild.WhoKnowsWhitelistRoleId.HasValue)
                {
                    var filteredAmount = usersWithArtist.Count - filteredUsersWithArtist.Count;
                    footer += $"\n{filteredAmount} inactive/blocked users filtered";
                }
                if (guild.WhoKnowsWhitelistRoleId.HasValue)
                {
                    footer += $"\nUsers with WhoKnows whitelisted role only";
                }

                this._embed.WithTitle($"{artistName} in {this.Context.Guild.Name}");

                if (Uri.IsWellFormedUriString(artistUrl, UriKind.Absolute))
                {
                    this._embed.WithUrl(artistUrl);
                }

                this._embedFooter.WithText(footer);
                this._embed.WithFooter(this._embedFooter);

                if (spotifyImageUrl != null)
                {
                    this._embed.WithThumbnailUrl(spotifyImageUrl);
                }

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
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
                    await ReplyAsync("Something went wrong while using whoknows.");
                }
            }
        }

        [Command("globalwhoknows", RunMode = RunMode.Async)]
        [Summary("Shows what other users listen to an artist in .fmbot")]
        [Examples("gw", "gwk COMA", "globalwhoknows", "globalwhoknows DJ Seinfeld")]
        [Alias("gw", "gwk", "globalwk", "globalwhoknows artist")]
        [UsernameSetRequired]
        [GuildOnly]
        [RequiresIndex]
        public async Task GlobalWhoKnowsAsync([Remainder] string artistValues = null)
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            try
            {
                var guildTask = this._guildService.GetFullGuildAsync(this.Context.Guild.Id);
                _ = this.Context.Channel.TriggerTypingAsync();

                var user = await this._userService.GetUserSettingsAsync(this.Context.User);

                var currentSettings = new WhoKnowsSettings
                {
                    HidePrivateUsers = false,
                    ShowBotters = false,
                    NewSearchValue = artistValues
                };
                var settings = this._settingService.SetWhoKnowsSettings(currentSettings, artistValues);

                string artistName;
                string artistUrl;
                string spotifyImageUrl;
                long? userPlaycount;

                var cachedArtist = await this._artistsService.GetArtistFromDatabase(settings.NewSearchValue);

                if (user.LastUpdated > DateTime.UtcNow.AddHours(-1) && cachedArtist != null)
                {
                    artistName = cachedArtist.Name;
                    artistUrl = cachedArtist.LastFmUrl;
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;

                    userPlaycount = await this._whoKnowArtistService.GetArtistPlayCountForUser(artistName, user.UserId);
                }
                else
                {
                    var artist = await GetArtist(settings.NewSearchValue, user.UserNameLastFM, user.SessionKeyLastFm);
                    if (artist == null)
                    {
                        return;
                    }

                    artistName = artist.ArtistName;
                    artistUrl = artist.ArtistUrl;

                    cachedArtist = await this._spotifyService.GetOrStoreArtistAsync(artist, artist.ArtistName);
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;
                    userPlaycount = artist.UserPlaycount;
                    if (userPlaycount.HasValue)
                    {
                        await this._updateService.CorrectUserArtistPlaycount(user.UserId, artist.ArtistName,
                            userPlaycount.Value);
                    }
                }

                var usersWithArtist = await this._whoKnowArtistService.GetGlobalUsersForArtists(this.Context, artistName);

                if (userPlaycount != 0 && this.Context.Guild != null)
                {
                    var discordGuildUser = await this.Context.Guild.GetUserAsync(user.DiscordUserId);
                    var guildUser = new GuildUser
                    {
                        UserName = discordGuildUser != null ? discordGuildUser.Nickname ?? discordGuildUser.Username : user.UserNameLastFM,
                        User = user
                    };
                    usersWithArtist = WhoKnowsService.AddOrReplaceUserToIndexList(usersWithArtist, guildUser, artistName, userPlaycount);
                }

                var filteredUsersWithArtist = await this._whoKnowsService.FilterGlobalUsersAsync(usersWithArtist);

                var guild = await guildTask;

                filteredUsersWithArtist =
                    WhoKnowsService.ShowGuildMembersInGlobalWhoKnowsAsync(filteredUsersWithArtist, guild.GuildUsers.ToList());

                var serverUsers = WhoKnowsService.WhoKnowsListToString(filteredUsersWithArtist, user.UserId, PrivacyLevel.Global, hidePrivateUsers: settings.HidePrivateUsers);
                if (filteredUsersWithArtist.Count == 0)
                {
                    serverUsers = "Nobody that uses .fmbot has listened to this artist.";
                }

                this._embed.WithDescription(serverUsers);

                var footer = "";
                if (cachedArtist.ArtistGenres != null && cachedArtist.ArtistGenres.Any())
                {
                    footer += $"\n{GenreService.GenresToString(cachedArtist.ArtistGenres.ToList())}";
                }

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                footer += $"\nGlobal WhoKnows artist requested by {userTitle}";

                if (filteredUsersWithArtist.Any() && filteredUsersWithArtist.Count > 1)
                {
                    var globalListeners = filteredUsersWithArtist.Count;
                    var globalPlaycount = filteredUsersWithArtist.Sum(a => a.Playcount);
                    var avgPlaycount = filteredUsersWithArtist.Average(a => a.Playcount);

                    footer += $"\n{globalListeners} {StringExtensions.GetListenersString(globalListeners)} - ";
                    footer += $"{globalPlaycount} total {StringExtensions.GetPlaysString(globalPlaycount)} - ";
                    footer += $"{(int)avgPlaycount} avg {StringExtensions.GetPlaysString((int)avgPlaycount)}";
                }

                var guildAlsoPlaying = await this._whoKnowsPlayService.GuildAlsoPlayingArtist(user.UserId,
                    this.Context.Guild.Id, artistName);

                if (guildAlsoPlaying != null)
                {
                    footer += "\n";
                    footer += guildAlsoPlaying;
                }

                if (user.PrivacyLevel != PrivacyLevel.Global)
                {
                    footer += $"\nYou are currently not globally visible - use '{prfx}privacy global' to enable.";
                }

                if (settings.HidePrivateUsers)
                {
                    footer += "\nAll private users are hidden from results";
                }

                this._embed.WithTitle($"{artistName} globally");

                if (Uri.IsWellFormedUriString(artistUrl, UriKind.Absolute))
                {
                    this._embed.WithUrl(artistUrl);
                }

                this._embedFooter.WithText(footer);
                this._embed.WithFooter(this._embedFooter);

                if (spotifyImageUrl != null)
                {
                    this._embed.WithThumbnailUrl(spotifyImageUrl);
                }

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
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
                    await ReplyAsync("Something went wrong while using global whoknows.");
                }
            }
        }

        [Command("friendwhoknows", RunMode = RunMode.Async)]
        [Summary("Shows who of your friends listen to an artist in .fmbot")]
        [Examples("fw", "fwk COMA", "friendwhoknows", "friendwhoknows DJ Seinfeld")]
        [Alias("fw", "fwk", "friendwhoknows artist", "friend whoknows", "friends whoknows", "friend whoknows artist", "friends whoknows artist")]
        [UsernameSetRequired]
        [GuildOnly]
        [RequiresIndex]
        public async Task FriendWhoKnowsAsync([Remainder] string artistValues = null)
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            try
            {
                _ = this.Context.Channel.TriggerTypingAsync();

                var user = await this._userService.GetUserWithFriendsAsync(this.Context.User);

                if (user.Friends?.Any() != true)
                {
                    await ReplyAsync("We couldn't find any friends. To add friends:\n" +
                                     $"`{prfx}addfriends {Constants.UserMentionOrLfmUserNameExample.Replace("`", "")}`");
                    this.Context.LogCommandUsed(CommandResponse.NotFound);
                    return;
                }

                var guild = await this._guildService.GetGuildAsync(this.Context.Guild.Id);

                string artistName;
                string artistUrl;
                string spotifyImageUrl;
                long? userPlaycount;

                var cachedArtist = await this._artistsService.GetArtistFromDatabase(artistValues);

                if (user.LastUpdated > DateTime.UtcNow.AddHours(-1) && cachedArtist != null)
                {
                    artistName = cachedArtist.Name;
                    artistUrl = cachedArtist.LastFmUrl;
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;

                    userPlaycount = await this._whoKnowArtistService.GetArtistPlayCountForUser(artistName, user.UserId);
                }
                else
                {
                    var artist = await GetArtist(artistValues, user.UserNameLastFM, user.SessionKeyLastFm);
                    if (artist == null)
                    {
                        return;
                    }

                    artistName = artist.ArtistName;
                    artistUrl = artist.ArtistUrl;

                    cachedArtist = await this._spotifyService.GetOrStoreArtistAsync(artist, artist.ArtistName);
                    spotifyImageUrl = cachedArtist.SpotifyImageUrl;
                    userPlaycount = artist.UserPlaycount;
                    if (userPlaycount.HasValue)
                    {
                        await this._updateService.CorrectUserArtistPlaycount(user.UserId, artist.ArtistName,
                            userPlaycount.Value);
                    }
                }

                var usersWithArtist = await this._whoKnowArtistService.GetFriendUsersForArtists(this.Context, guild.GuildId, user.UserId, artistName);

                if (userPlaycount != 0 && this.Context.Guild != null)
                {
                    var discordGuildUser = await this.Context.Guild.GetUserAsync(user.DiscordUserId);
                    var guildUser = new GuildUser
                    {
                        UserName = discordGuildUser != null ? discordGuildUser.Nickname ?? discordGuildUser.Username : user.UserNameLastFM,
                        User = user
                    };
                    usersWithArtist = WhoKnowsService.AddOrReplaceUserToIndexList(usersWithArtist, guildUser, artistName, userPlaycount);
                }

                var serverUsers = WhoKnowsService.WhoKnowsListToString(usersWithArtist, user.UserId, PrivacyLevel.Server);
                if (usersWithArtist.Count == 0)
                {
                    serverUsers = "None of your friends has listened to this artist.";
                }

                this._embed.WithDescription(serverUsers);

                var footer = "";

                if (cachedArtist.ArtistGenres != null && cachedArtist.ArtistGenres.Any())
                {
                    footer += $"\n{GenreService.GenresToString(cachedArtist.ArtistGenres.ToList())}";
                }

                var amountOfHiddenFriends = user.Friends.Count(c => !c.FriendUserId.HasValue);
                if (amountOfHiddenFriends > 0)
                {
                    footer += $"\n{amountOfHiddenFriends} non-fmbot {StringExtensions.GetFriendsString(amountOfHiddenFriends)} not visible";
                }

                var userTitle = await this._userService.GetUserTitleAsync(this.Context);
                footer += $"\nFriends WhoKnow artist requested by {userTitle}";

                if (usersWithArtist.Any() && usersWithArtist.Count > 1)
                {
                    var globalListeners = usersWithArtist.Count;
                    var globalPlaycount = usersWithArtist.Sum(a => a.Playcount);
                    var avgPlaycount = usersWithArtist.Average(a => a.Playcount);

                    footer += $"\n{globalListeners} {StringExtensions.GetListenersString(globalListeners)} - ";
                    footer += $"{globalPlaycount} total {StringExtensions.GetPlaysString(globalPlaycount)} - ";
                    footer += $"{(int)avgPlaycount} avg {StringExtensions.GetPlaysString((int)avgPlaycount)}";
                }

                this._embed.WithTitle($"{artistName} with friends");

                if (Uri.IsWellFormedUriString(artistUrl, UriKind.Absolute))
                {
                    this._embed.WithUrl(artistUrl);
                }

                this._embedFooter.WithText(footer);
                this._embed.WithFooter(this._embedFooter);

                if (spotifyImageUrl != null)
                {
                    this._embed.WithThumbnailUrl(spotifyImageUrl);
                }

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

                this.Context.LogCommandUsed();
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
                    await ReplyAsync("Something went wrong while using friend whoknows.");
                }
            }
        }

        [Command("serverartists", RunMode = RunMode.Async)]
        [Summary("Shows top artists for your server")]
        [Options("Time periods: `weekly`, `monthly` and `alltime`", "Order options: `plays` and `listeners`")]
        [Examples("sa", "sa a p", "serverartists", "serverartists alltime", "serverartists listeners weekly")]
        [Alias("sa", "sta", "servertopartists", "server artists", "serverartist")]
        [GuildOnly]
        [RequiresIndex]
        public async Task GuildArtistsAsync(params string[] extraOptions)
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);
            var guild = await this._guildService.GetFullGuildAsync(this.Context.Guild.Id);

            _ = this.Context.Channel.TriggerTypingAsync();

            var serverArtistSettings = new GuildRankingSettings
            {
                ChartTimePeriod = TimePeriod.Weekly,
                TimeDescription = "weekly",
                OrderType = OrderType.Listeners,
                AmountOfDays = 7
            };

            serverArtistSettings = SettingService.SetGuildRankingSettings(serverArtistSettings, extraOptions);
            var timePeriod = SettingService.GetTimePeriod(extraOptions, serverArtistSettings.ChartTimePeriod);

            if (timePeriod.UsePlays || timePeriod.TimePeriod is TimePeriod.AllTime or TimePeriod.Monthly or TimePeriod.Weekly)
            {
                serverArtistSettings.ChartTimePeriod = timePeriod.TimePeriod;
                serverArtistSettings.TimeDescription = timePeriod.Description;
                serverArtistSettings.AmountOfDays = timePeriod.PlayDays.GetValueOrDefault();
            }

            var description = "";
            var footer = "";

            if (guild.GuildUsers != null && guild.GuildUsers.Count > 500 && serverArtistSettings.ChartTimePeriod == TimePeriod.Monthly)
            {
                serverArtistSettings.AmountOfDays = 7;
                serverArtistSettings.ChartTimePeriod = TimePeriod.Weekly;
                serverArtistSettings.TimeDescription = "weekly";
                footer += "Sorry, monthly time period is not supported on large servers.\n";
            }

            try
            {
                IReadOnlyList<ListArtist> topGuildArtists;
                if (serverArtistSettings.ChartTimePeriod == TimePeriod.AllTime)
                {
                    topGuildArtists = await this._whoKnowArtistService.GetTopAllTimeArtistsForGuild(guild.GuildId, serverArtistSettings.OrderType);
                }
                else
                {
                    topGuildArtists = await this._whoKnowsPlayService.GetTopArtistsForGuild(guild.GuildId, serverArtistSettings.OrderType, serverArtistSettings.AmountOfDays);
                }

                this._embed.WithTitle($"Top {serverArtistSettings.TimeDescription} artists in {this.Context.Guild.Name}");

                if (serverArtistSettings.OrderType == OrderType.Listeners)
                {
                    footer += "Listeners / Plays - Ordered by listeners\n";
                }
                else
                {
                    footer += "Listeners / Plays - Ordered by plays\n";
                }

                foreach (var artist in topGuildArtists)
                {
                    description += $"`{artist.ListenerCount}` / `{artist.TotalPlaycount}` | **{artist.ArtistName}**\n";
                }

                this._embed.WithDescription(description);

                var rnd = new Random();
                var randomHintNumber = rnd.Next(0, 5);
                if (randomHintNumber == 1)
                {
                    footer += $"View specific artist listeners with {prfx}whoknows\n";
                }
                else if (randomHintNumber == 2)
                {
                    footer += $"Available time periods: alltime, weekly and daily\n";
                }
                else if (randomHintNumber == 3)
                {
                    footer += $"Available sorting options: plays and listeners\n";
                }
                if (guild.LastIndexed < DateTime.UtcNow.AddDays(-7) && randomHintNumber == 4)
                {
                    footer += $"Missing members? Update with {prfx}index\n";
                }

                this._embedFooter.WithText(footer);
                this._embed.WithFooter(this._embedFooter);

                await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                this.Context.LogCommandUsed();
            }
            catch (Exception e)
            {
                this.Context.LogCommandException(e);
                await ReplyAsync(
                    "Something went wrong while using serverartists. Please report this issue.");
            }
        }

        [Command("affinity", RunMode = RunMode.Async)]
        [Summary("Shows what other users in the same server listen to the same music as you.\n\n" +
                 "This command is still a work in progress.")]
        [Alias("n", "aff", "neighbors")]
        [UsernameSetRequired]
        [GuildOnly]
        [RequiresIndex]
        public async Task AffinityAsync()
        {
            var userSettings = await this._userService.GetUserSettingsAsync(this.Context.User);
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            var guild = await this._guildService.GetFullGuildAsync(this.Context.Guild.Id);
            var filteredGuildUsers = GuildService.FilterGuildUsersAsync(guild);

            _ = this.Context.Channel.TriggerTypingAsync();

            var users = filteredGuildUsers.Select(s => s.User).ToList();
            var neighbors = await this._whoKnowArtistService.GetNeighbors(users, userSettings.UserId);

            var description = new StringBuilder();

            foreach (var neighbor in neighbors.Take(15))
            {
                description.AppendLine($"**[{neighbor.Name}]({Constants.LastFMUserUrl}{neighbor.LastFMUsername})** " +
                                       $"- {neighbor.MatchPercentage:0.0}%");
            }

            var userTitle = await this._userService.GetUserTitleAsync(this.Context);

            this._embed.WithTitle($"Neighbors for {userTitle}");
            this._embed.WithFooter("Experimental command - work in progress");

            this._embed.WithDescription(description.ToString());

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

        }

        private async Task<ArtistInfo> GetArtist(string artistValues, string lastFmUserName, string sessionKey = null, string otherUserUsername = null)
        {
            if (!string.IsNullOrWhiteSpace(artistValues) && artistValues.Length != 0)
            {
                if (otherUserUsername != null)
                {
                    lastFmUserName = otherUserUsername;
                }

                var artistCall = await this._lastFmRepository.GetArtistInfoAsync(artistValues, lastFmUserName);
                if (!artistCall.Success && artistCall.Error == ResponseStatus.MissingParameters)
                {
                    this._embed.WithDescription($"Artist `{artistValues}` could not be found, please check your search values and try again.");
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                    this.Context.LogCommandUsed(CommandResponse.NotFound);
                    return null;
                }
                if (!artistCall.Success || artistCall.Content == null)
                {
                    this._embed.ErrorResponse(artistCall.Error, artistCall.Message, this.Context, "artist");
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                    this.Context.LogCommandUsed(CommandResponse.LastFmError);
                    return null;
                }

                return artistCall.Content;
            }
            else
            {
                var recentScrobbles = await this._lastFmRepository.GetRecentTracksAsync(lastFmUserName, 1, true, sessionKey);

                if (await GenericEmbedService.RecentScrobbleCallFailedReply(recentScrobbles, lastFmUserName, this.Context))
                {
                    return null;
                }

                if (otherUserUsername != null)
                {
                    lastFmUserName = otherUserUsername;
                }

                var lastPlayedTrack = recentScrobbles.Content.RecentTracks[0];
                var artistCall = await this._lastFmRepository.GetArtistInfoAsync(lastPlayedTrack.ArtistName, lastFmUserName);

                if (artistCall.Content == null || !artistCall.Success)
                {
                    this._embed.WithDescription($"Last.fm did not return a result for **{lastPlayedTrack.ArtistName}**.");
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                    this.Context.LogCommandUsed(CommandResponse.NotFound);
                    return null;
                }

                return artistCall.Content;
            }
        }
    }
}
