﻿using AngleSharp;
using GameJoltLibrary.Models;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Net.Http;
using System;
using Playnite.SDK;

namespace GameJoltLibrary;

public class LibraryGamesProvider
{
    private readonly ILogger _logger;

    public LibraryGamesProvider(ILogger logger)
    {
        _logger = logger;
    }

    public IEnumerable<GameMetadata> GetLibraryGames(string userName)
    {
        var games = new List<GameMetadata>();

        try
        {
            string ownedGamesUrl = $"https://gamejolt.com/@{userName}/owned";

            var http = new HttpClient();
            http.BaseAddress = new Uri("https://gamejolt.com/site-api/");

            var result = http.GetAsync("web/library/games/owned/@mrxx99").GetAwaiter().GetResult();

            var stringContent = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            var ownedGames = Serialization.FromJsonStream<LibraryGamesResult>(result.Content.ReadAsStreamAsync().GetAwaiter().GetResult());

            var config = Configuration.Default.WithDefaultLoader();
            var context = BrowsingContext.New(config);

            foreach (var ownedGame in ownedGames.Payload.Games)
            {
                var game = new GameMetadata
                {
                    GameId = ownedGame.Id.ToString(),
                    Source = new MetadataNameProperty("Game Jolt"),
                    Name = ownedGame.Title,
                    IsInstalled = false,
                    Links = new List<Link> { new Link("store page web", ownedGame.Link) },
                    Developers = new HashSet<MetadataProperty> { new MetadataNameProperty(ownedGame.Developer.DisplayName) }
                };

                games.Add(game);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during loading library games for Game Jolt");
        }

        return games;
    }
}
