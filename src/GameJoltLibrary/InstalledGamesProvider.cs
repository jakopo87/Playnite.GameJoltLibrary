﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GameJoltLibrary.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;

namespace GameJoltLibrary;

public class InstalledGamesProvider
{
    private static readonly string _localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string _gameJoltUserDataPath = Path.Combine(_localAppDataPath, "game-jolt-client", "User Data", "Default");
    private readonly IPlayniteAPI _playniteAPI;
    private readonly ILogger _logger;

    public InstalledGamesProvider(IPlayniteAPI playniteAPI, ILogger logger)
    {
        _playniteAPI = playniteAPI;
        _logger = logger;
    }

    public IEnumerable<GameMetadata> GetInstalledGames(CancellationToken cancelToken)
    {
        var games = new Dictionary<string, GameMetadata>();

        var installedGamesMetadata = GetGamesMetadata(_logger);
        var gamePackagesInfo = GetGamePackagesInfo();

        foreach (var gameInfo in installedGamesMetadata.Values)
        {
            cancelToken.ThrowIfCancellationRequested();

            var packagesForGame = gamePackagesInfo.Where(x => x.GameId == gameInfo.Id).ToList();

            var gameMetaData = new GameMetadata
            {
                Source = new MetadataNameProperty("Game Jolt"),
                GameId = gameInfo.Id.ToString(),
                Name = gameInfo.Title,
                IsInstalled = true,
                BackgroundImage = new MetadataFile(gameInfo.HeaderMediaItem?.ImgUrl?.AbsoluteUri),
                CoverImage = new MetadataFile(gameInfo.ThumbnailMediaItem?.ImgUrl?.AbsoluteUri),
                InstallDirectory = packagesForGame.FirstOrDefault()?.InstallDir,
                GameActions = new List<GameAction>(),
                Links = new List<Link> { new Link("Game Jolt Store Page", gameInfo.StorePageLink) },
                Developers = new HashSet<MetadataProperty> { new MetadataNameProperty(gameInfo.Developer.DisplayName) }
            };

            foreach (var package in packagesForGame.Where(p => !string.IsNullOrEmpty(p.InstallDir)))
            {
                var launchOption = GetFittingLaunchOption(package);

                if (launchOption != null)
                {
                    string relativeGameIExecutablePath = launchOption.ExecutablePath;
                    string gameExecutablePath = Path.Combine(package.InstallDir, "data", relativeGameIExecutablePath);

                    if (File.Exists(gameExecutablePath))
                    {
                        gameMetaData.GameActions.Add(new GameAction
                        {
                            IsPlayAction = true,
                            Type = GameActionType.File,
                            Path = gameExecutablePath,
                            Name = package.Title
                        });
                    }
                    else
                    {
                        _logger.Warn($"game executable does not exist ({gameExecutablePath})");
                    }
                }
            }

            if (gameMetaData.GameActions.FirstOrDefault() is GameAction primaryGameAction)
            {
                gameMetaData.Icon = new MetadataFile(primaryGameAction.Path);
            }

            games.Add(gameMetaData.GameId, gameMetaData);
        }

        return games.Values.ToList();
    }

    public void UpdatedUninstalledGames(GameMetadata[] installedGames)
    {
        using (_playniteAPI.Database.BufferedUpdate())
        {
            // Any collection changes here don't generate any events

            var existingGamesMarkedAsInstalled = _playniteAPI.Database.Games.Where(game => game.PluginId == GameJoltLibrary.PluginId && game.IsInstalled);
            var uninstalledGames = existingGamesMarkedAsInstalled.Where(game => !installedGames.Any(i => i.GameId == game.GameId));
            foreach (var uninstalledGame in uninstalledGames)
            {
                uninstalledGame.IsInstalled = false;
                _playniteAPI.Database.Games.Update(uninstalledGame);
            }
        }
    }

    private LaunchOption GetFittingLaunchOption(InstalledGameInfo package)
    {
        var launchOptions = package.LaunchOptions.EmptyIfNull().Where(l => l.IsValid());
        var windowsLaunchOptions = launchOptions.Where(l => l.Os.Contains("windows", StringComparison.OrdinalIgnoreCase));
        var windows32BitVersions = windowsLaunchOptions.Where(l => !l.Os.Contains("64"));
        var windows64BitVersions = windowsLaunchOptions.Where(l => l.Os.Contains("64"));
        var fittingBitVersions = Utility.Is64BitOs ? windows64BitVersions.Concat(windows32BitVersions) : windows32BitVersions;

        if (fittingBitVersions.Any())
        {
            return fittingBitVersions.FirstOrDefault();
        }

        return GetLaunchOptionFromGameManifestFile(package);
    }

    private LaunchOption GetLaunchOptionFromGameManifestFile(InstalledGameInfo package)
    {
        var manifestFile = Path.Combine(package.InstallDir, ".manifest");

        if (File.Exists(manifestFile))
        {
            try
            {
                var gameManifest = Serialization.FromJsonFile<GameManifest>(manifestFile);

                if (!string.IsNullOrEmpty(gameManifest?.GameInfo?.Dir) && !string.IsNullOrEmpty(gameManifest?.LaunchOptions?.Executable)
                    && gameManifest.Os.EmptyIfNull().Contains("windows", StringComparison.OrdinalIgnoreCase)
                    && (Utility.Is64BitOs || gameManifest.Arch != "64"))
                {
                    string gameExecutablePath = Path.Combine(package.InstallDir, gameManifest.GameInfo.Dir, gameManifest.LaunchOptions.Executable);
                    return new LaunchOption
                    {
                        ExecutablePath = gameExecutablePath,
                        Os = gameManifest.Os,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to read launch settings from game manifest file {manifestFile}");
            }
        }
        else
        {
            _logger.Warn($"Failed to find game manifest file {manifestFile}");
        }

        return null;
    }

    public static IReadOnlyDictionary<long, GameJoltGameMetadata> GetGamesMetadata(ILogger logger)
    {
        var installedGamesMetadataFile = Path.Combine(_gameJoltUserDataPath, "games.wttf");

        InstalledGamesMetadata installedGamesMetadata = null;

        if (File.Exists(installedGamesMetadataFile))
        {
            try
            {
                installedGamesMetadata = Serialization.FromJsonFile<InstalledGamesMetadata>(installedGamesMetadataFile);
                logger.Info($"Read GameJolt file {installedGamesMetadataFile}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to deserialize {installedGamesMetadataFile}");
            }
        }
        else
        {
            logger.Warn($"Not found GameJolt file {installedGamesMetadataFile}");
        }

        if (installedGamesMetadata?.Objects?.Values is null)
        {
            return new Dictionary<long, GameJoltGameMetadata>();
        }

        var filtered = installedGamesMetadata?.Objects?.Where(i => i.Value != null);

        return filtered.ToDictionary(x => x.Key, x => x.Value);
    }

    private IEnumerable<InstalledGameInfo> GetGamePackagesInfo()
    {
        var installedGamesInfoFile = Path.Combine(_gameJoltUserDataPath, "packages.wttf");

        InstalledGamesInfo installedGamesMetadata = null;

        if (File.Exists(installedGamesInfoFile))
        {
            try
            {
                installedGamesMetadata = Serialization.FromJsonFile<InstalledGamesInfo>(installedGamesInfoFile);
                _logger.Info($"Read GameJolt file {installedGamesInfoFile}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to deserialize {installedGamesInfoFile}");
            }
        }
        else
        {
            _logger.Warn($"Not found GameJolt file {installedGamesInfoFile}");
        }

        return installedGamesMetadata?.Objects?.Values?.Where(i => i != null) ?? Enumerable.Empty<InstalledGameInfo>();
    }
}
