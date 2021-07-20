using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using FMBot.Domain.Models;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using FMBot.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using SpotifyAPI.Web;
using Artist = FMBot.Persistence.Domain.Models.Artist;

namespace FMBot.Bot.Services.ThirdParty
{
    public class SpotifyService
    {
        private readonly IDbContextFactory<FMBotDbContext> _contextFactory;
        private readonly BotSettings _botSettings;
        private readonly ArtistRepository _artistRepository;
        private readonly AlbumRepository _albumRepository;
        private readonly TrackRepository _trackRepository;

        public SpotifyService(IDbContextFactory<FMBotDbContext> contextFactory, IOptions<BotSettings> botSettings, ArtistRepository artistRepository, TrackRepository trackRepository, AlbumRepository albumRepository)
        {
            this._contextFactory = contextFactory;
            this._artistRepository = artistRepository;
            this._trackRepository = trackRepository;
            this._albumRepository = albumRepository;
            this._botSettings = botSettings.Value;
        }

        public async Task<SearchResponse> GetSearchResultAsync(string searchValue, SearchRequest.Types searchType = SearchRequest.Types.Track)
        {
            var spotify = GetSpotifyWebApi();

            searchValue = searchValue.Replace("- Single", "");
            searchValue = searchValue.Replace("- EP", "");

            var searchRequest = new SearchRequest(searchType, searchValue);

            return await spotify.Search.Item(searchRequest);
        }

        public async Task<Artist> GetOrStoreArtistAsync(ArtistInfo artistInfo, string artistNameBeforeCorrect = null)
        {
            Log.Information("Executing GetOrStoreArtistImageAsync");

            await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
            await connection.OpenAsync();

            try
            {
                var dbArtist = await this._artistRepository.GetArtistForName(artistInfo.ArtistName, connection, true);

                if (dbArtist == null)
                {
                    await using var db = this._contextFactory.CreateDbContext();
                    var spotifyArtist = await GetArtistFromSpotify(artistInfo.ArtistName);

                    var artistToAdd = new Artist
                    {
                        Name = artistInfo.ArtistName,
                        LastFmUrl = artistInfo.ArtistUrl,
                        Mbid = artistInfo.Mbid
                    };

                    if (spotifyArtist != null)
                    {
                        artistToAdd.SpotifyId = spotifyArtist.Id;
                        artistToAdd.Popularity = spotifyArtist.Popularity;

                        if (spotifyArtist.Images.Any())
                        {
                            artistToAdd.SpotifyImageUrl = spotifyArtist.Images.OrderByDescending(o => o.Height).First().Url;
                            artistToAdd.SpotifyImageDate = DateTime.UtcNow;
                        }

                        await db.Artists.AddAsync(artistToAdd);
                        await db.SaveChangesAsync();

                        if (artistToAdd.Id == 0)
                        {
                            throw new Exception("Artist id is 0!");
                        }
                        if (spotifyArtist.Genres.Any())
                        {
                            await this._artistRepository
                                .AddOrUpdateArtistGenres(artistToAdd.Id, spotifyArtist.Genres.Select(s => s), connection);
                        }
                    }
                    else
                    {
                        await db.Artists.AddAsync(artistToAdd);
                        await db.SaveChangesAsync();

                        if (artistToAdd.Id == 0)
                        {
                            throw new Exception("Artist id is 0!");
                        }
                    }

                    if (spotifyArtist != null && spotifyArtist.Genres.Any())
                    {
                        artistToAdd.ArtistGenres = spotifyArtist.Genres.Select(s => new ArtistGenre
                        {
                            Name = s
                        }).ToList();
                    }

                    if (artistNameBeforeCorrect != null && !string.Equals(artistNameBeforeCorrect, artistInfo.ArtistName, StringComparison.CurrentCultureIgnoreCase))
                    {
                        await this._artistRepository
                            .AddOrUpdateArtistAlias(artistToAdd.Id, artistNameBeforeCorrect, connection);
                    }

                    return artistToAdd;
                }

                if (artistNameBeforeCorrect != null && !string.Equals(artistNameBeforeCorrect, artistInfo.ArtistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    await this._artistRepository
                        .AddOrUpdateArtistAlias(dbArtist.Id, artistNameBeforeCorrect, connection);
                }

                if (dbArtist.SpotifyImageUrl == null || dbArtist.SpotifyImageDate < DateTime.UtcNow.AddDays(-15))
                {
                    await using var db = this._contextFactory.CreateDbContext();

                    var spotifyArtist = await GetArtistFromSpotify(artistInfo.ArtistName);

                    if (spotifyArtist != null && spotifyArtist.Images.Any())
                    {
                        dbArtist.SpotifyImageUrl = spotifyArtist.Images.OrderByDescending(o => o.Height).First().Url;
                        dbArtist.Popularity = spotifyArtist.Popularity;
                    }

                    if (spotifyArtist != null && spotifyArtist.Genres.Any())
                    {
                        await this._artistRepository
                            .AddOrUpdateArtistGenres(dbArtist.Id, spotifyArtist.Genres.Select(s => s), connection);
                    }

                    dbArtist.SpotifyImageDate = DateTime.UtcNow;
                    db.Entry(dbArtist).State = EntityState.Modified;
                    await db.SaveChangesAsync();

                    if (spotifyArtist != null && spotifyArtist.Genres.Any())
                    {
                        dbArtist.ArtistGenres = spotifyArtist.Genres.Select(s => new ArtistGenre
                        {
                            Name = s
                        }).ToList();
                    }
                }

                await connection.CloseAsync();
                return dbArtist;

            }
            catch (Exception e)
            {
                Log.Error(e, "Something went wrong while retrieving artist image");
                return null;
            }
        }

        private async Task<FullArtist> GetArtistFromSpotify(string artistName)
        {
            var spotify = GetSpotifyWebApi();

            var searchRequest = new SearchRequest(SearchRequest.Types.Artist, artistName);

            var results = await spotify.Search.Item(searchRequest);

            if (results.Artists.Items?.Any() == true)
            {
                var spotifyArtist = results.Artists.Items
                    .OrderByDescending(o => o.Popularity)
                    .ThenByDescending(o => o.Followers.Total)
                    .FirstOrDefault(w => w.Name.ToLower() == artistName.ToLower());

                if (spotifyArtist != null)
                {
                    return spotifyArtist;
                }
            }

            return null;
        }

        public async Task<Track> GetOrStoreTrackAsync(TrackInfo trackInfo)
        {
            Log.Information("Executing GetOrStoreTrackAsync");

            try
            {
                await using var db = this._contextFactory.CreateDbContext();

                await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
                await connection.OpenAsync();

                var dbTrack = await this._trackRepository.GetTrackForName(trackInfo.ArtistName, trackInfo.TrackName, connection);

                if (dbTrack == null)
                {
                    var trackToAdd = new Track
                    {
                        Name = trackInfo.TrackName,
                        AlbumName = trackInfo.AlbumName,
                        ArtistName = trackInfo.ArtistName
                    };

                    var artist = await this._artistRepository.GetArtistForName(trackInfo.ArtistName, connection);

                    if (artist != null)
                    {
                        trackToAdd.ArtistId = artist.Id;
                    }

                    var spotifyTrack = await GetTrackFromSpotify(trackInfo.TrackName, trackInfo.ArtistName.ToLower());

                    if (spotifyTrack != null)
                    {
                        trackToAdd.SpotifyId = spotifyTrack.Id;
                        trackToAdd.DurationMs = spotifyTrack.DurationMs;

                        var audioFeatures = await GetAudioFeaturesFromSpotify(spotifyTrack.Id);

                        if (audioFeatures != null)
                        {
                            trackToAdd.Key = audioFeatures.Key;
                            trackToAdd.Tempo = audioFeatures.Tempo;
                        }
                    }

                    trackToAdd.SpotifyLastUpdated = DateTime.UtcNow;

                    await db.Tracks.AddAsync(trackToAdd);
                    await db.SaveChangesAsync();

                    return trackToAdd;
                }

                if (!dbTrack.ArtistId.HasValue)
                {
                    var artist = await this._artistRepository.GetArtistForName(trackInfo.ArtistName, connection);

                    if (artist != null)
                    {
                        dbTrack.ArtistId = artist.Id;
                        db.Entry(dbTrack).State = EntityState.Modified;
                    }
                }
                if (string.IsNullOrEmpty(dbTrack.SpotifyId) && dbTrack.SpotifyLastUpdated < DateTime.UtcNow.AddMonths(-2))
                {
                    var spotifyTrack = await GetTrackFromSpotify(trackInfo.TrackName, trackInfo.ArtistName.ToLower());

                    if (spotifyTrack != null)
                    {
                        dbTrack.SpotifyId = spotifyTrack.Id;
                        dbTrack.DurationMs = spotifyTrack.DurationMs;

                        var audioFeatures = await GetAudioFeaturesFromSpotify(spotifyTrack.Id);

                        if (audioFeatures != null)
                        {
                            dbTrack.Key = audioFeatures.Key;
                            dbTrack.Tempo = audioFeatures.Tempo;
                        }
                    }

                    dbTrack.SpotifyLastUpdated = DateTime.UtcNow;
                    db.Entry(dbTrack).State = EntityState.Modified;
                }

                await db.SaveChangesAsync();

                await connection.CloseAsync();

                return dbTrack;
            }
            catch (Exception e)
            {
                Log.Error(e, "Something went wrong while retrieving track info");
                return null;
            }
        }

        private async Task<FullTrack> GetTrackFromSpotify(string trackName, string artistName)
        {
            //Create the auth object
            var spotify = GetSpotifyWebApi();

            var searchRequest = new SearchRequest(SearchRequest.Types.Track, $"track:{trackName} artist:{artistName}");

            var results = await spotify.Search.Item(searchRequest);

            if (results.Tracks.Items?.Any() == true)
            {
                var spotifyTrack = results.Tracks.Items
                    .OrderByDescending(o => o.Popularity)
                    .FirstOrDefault(w => w.Name.ToLower() == trackName.ToLower() && w.Artists.Select(s => s.Name.ToLower()).Contains(artistName.ToLower()));

                if (spotifyTrack != null)
                {
                    return spotifyTrack;
                }
            }

            return null;
        }

        private async Task<FullAlbum> GetAlbumFromSpotify(string albumName, string artistName)
        {
            //Create the auth object
            var spotify = GetSpotifyWebApi();

            var searchRequest = new SearchRequest(SearchRequest.Types.Album, $"{albumName} {artistName}");

            var results = await spotify.Search.Item(searchRequest);

            if (results.Albums.Items?.Any() == true)
            {
                var spotifyAlbum = results.Albums.Items
                    .FirstOrDefault(w => w.Name.ToLower() == albumName.ToLower() && w.Artists.Select(s => s.Name.ToLower()).Contains(artistName.ToLower()));

                if (spotifyAlbum != null)
                {
                    return await GetAlbumById(spotifyAlbum.Id);
                }
            }

            return null;
        }

        public async Task<FullTrack> GetTrackById(string spotifyId)
        {
            //Create the auth object
            var spotify = GetSpotifyWebApi();

            return await spotify.Tracks.Get(spotifyId);
        }

        public async Task<FullAlbum> GetAlbumById(string spotifyId)
        {
            //Create the auth object
            var spotify = GetSpotifyWebApi();

            return await spotify.Albums.Get(spotifyId);
        }

        private async Task<TrackAudioFeatures> GetAudioFeaturesFromSpotify(string spotifyId)
        {
            //Create the auth object
            var spotify = GetSpotifyWebApi();

            var result = await spotify.Tracks.GetAudioFeatures(spotifyId);

            return result;
        }

        public async Task<Album> GetOrStoreSpotifyAlbumAsync(AlbumInfo albumInfo)
        {
            Log.Information("Executing GetOrStoreSpotifyAlbumAsync");

            await using var db = this._contextFactory.CreateDbContext();

            await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
            await connection.OpenAsync();

            var dbAlbum = await this._albumRepository.GetAlbumForName(albumInfo.ArtistName, albumInfo.AlbumName, connection);

            if (dbAlbum == null)
            {
                var albumToAdd = new Album
                {
                    Name = albumInfo.AlbumName,
                    ArtistName = albumInfo.ArtistName,
                    LastFmUrl = albumInfo.AlbumUrl,
                    Mbid = albumInfo.Mbid
                };

                var artist = await this._artistRepository.GetArtistForName(albumInfo.ArtistName, connection);

                if (artist != null && artist.Id != 0)
                {
                    albumToAdd.ArtistId = artist.Id;
                }

                var spotifyAlbum = await GetAlbumFromSpotify(albumInfo.AlbumName, albumInfo.ArtistName.ToLower());

                if (spotifyAlbum != null)
                {
                    albumToAdd.SpotifyId = spotifyAlbum.Id;
                    albumToAdd.Label = spotifyAlbum.Label;
                    albumToAdd.Popularity = spotifyAlbum.Popularity;
                    albumToAdd.SpotifyImageUrl = spotifyAlbum.Images.OrderByDescending(o => o.Height).First().Url;
                }

                albumToAdd.SpotifyImageDate = DateTime.UtcNow;

                await db.Albums.AddAsync(albumToAdd);
                await db.SaveChangesAsync();

                if (spotifyAlbum != null)
                {
                    await GetOrStoreAlbumTracks(spotifyAlbum.Tracks.Items, albumInfo, albumToAdd.Id, connection);
                }

                await connection.CloseAsync();

                return albumToAdd;
            }
            if (dbAlbum.Artist == null)
            {
                var artist = await this._artistRepository.GetArtistForName(albumInfo.ArtistName, connection);

                if (artist != null && artist.Id != 0)
                {
                    dbAlbum.ArtistId = artist.Id;
                    db.Entry(dbAlbum).State = EntityState.Modified;
                    await db.SaveChangesAsync();
                }
            }
            if (string.IsNullOrEmpty(dbAlbum.SpotifyId) && dbAlbum.SpotifyImageDate < DateTime.UtcNow.AddMonths(-2))
            {
                var spotifyAlbum = await GetAlbumFromSpotify(albumInfo.AlbumName, albumInfo.ArtistName.ToLower());

                if (spotifyAlbum != null)
                {
                    dbAlbum.SpotifyId = spotifyAlbum.Id;
                    dbAlbum.Label = spotifyAlbum.Label;
                    dbAlbum.Popularity = spotifyAlbum.Popularity;
                    dbAlbum.SpotifyImageUrl = spotifyAlbum.Images.OrderByDescending(o => o.Height).First().Url;
                }

                dbAlbum.SpotifyImageDate = DateTime.UtcNow;

                db.Entry(dbAlbum).State = EntityState.Modified;

                await db.SaveChangesAsync();

                if (spotifyAlbum != null)
                {
                    await GetOrStoreAlbumTracks(spotifyAlbum.Tracks.Items, albumInfo, dbAlbum.Id, connection);
                }
            }

            await connection.CloseAsync();

            return dbAlbum;
        }

        private async Task GetOrStoreAlbumTracks(IEnumerable<SimpleTrack> simpleTracks, AlbumInfo albumInfo,
            int albumId, NpgsqlConnection connection)
        {
            await using var db = this._contextFactory.CreateDbContext();
            var dbTracks = new List<Track>();
            foreach (var track in simpleTracks.OrderBy(o => o.TrackNumber))
            {
                var dbTrack = await this._trackRepository.GetTrackForName(albumInfo.ArtistName, track.Name, connection);

                if (dbTrack != null)
                {
                    dbTracks.Add(dbTrack);
                }
                else
                {
                    var trackToAdd = new Track
                    {
                        Name = track.Name,
                        AlbumName = albumInfo.AlbumName,
                        DurationMs = track.DurationMs,
                        SpotifyId = track.Id,
                        ArtistName = albumInfo.ArtistName,
                        SpotifyLastUpdated = DateTime.UtcNow,
                        AlbumId = albumId
                    };

                    await db.Tracks.AddAsync(trackToAdd);

                    dbTracks.Add(trackToAdd);
                }
            }

            await db.SaveChangesAsync();
        }

        public async Task<ICollection<Track>> GetExistingAlbumTracks(int albumId)
        {
            await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
            await connection.OpenAsync();

            var albumTracks =  await this._trackRepository.GetAlbumTracks(albumId, connection);
            await connection.CloseAsync();

            return albumTracks;
        }

        private SpotifyClient GetSpotifyWebApi()
        {
            var config = SpotifyClientConfig
                .CreateDefault()
                .WithAuthenticator(new ClientCredentialsAuthenticator(this._botSettings.Spotify.Key, this._botSettings.Spotify.Secret));

            return new SpotifyClient(config);
        }

        public static RecentTrack SpotifyGameToRecentTrack(SpotifyGame spotifyActivity)
        {
            return new()
            {
                TrackName = spotifyActivity.TrackTitle,
                AlbumName = spotifyActivity.AlbumTitle,
                ArtistName = spotifyActivity.Artists.First(),
                AlbumCoverUrl = spotifyActivity.AlbumArtUrl,
                TrackUrl = spotifyActivity.TrackUrl
            };
        }
    }
}
