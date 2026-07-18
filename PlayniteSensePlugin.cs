using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteSense
{
    public class PlayniteSensePlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly PlayniteSenseSettingsViewModel settings;
        private readonly VirtualPadBridge bridge = new VirtualPadBridge();

        public VirtualPadBridge Bridge => bridge;

        public override Guid Id { get; } = Guid.Parse("ceb0583b-3a53-4448-989b-e3943a6d397b");

        public PlayniteSensePlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };

            settings = new PlayniteSenseSettingsViewModel(this);
            bridge.StatusChanged += OnBridgeStatusChanged;

        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // HidHide registration is automatic in Desktop and Fullscreen.
            bridge.EnsurePlayniteIsWhitelisted();
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var game = args.Games.FirstOrDefault();
            if (game == null)
            {
                yield break;
            }

            bool enabled = settings.Settings.EnabledGames.Contains(game.Id);
            if (enabled)
            {
                yield return new GameMenuItem
                {
                    MenuSection = "PlayniteSense",
                    Description = "Disable PlayniteSense",
                    Action = a =>
                    {
                        settings.Settings.EnabledGames.Remove(game.Id);
                        settings.Settings.GameModes.Remove(game.Id);
                        settings.EndEdit();
                    }
                };
                yield break;
            }

            yield return new GameMenuItem
            {
                MenuSection = "PlayniteSense",
                Description = "Enable PlayniteSense (DS4)",
                Action = a => EnableGame(game.Id, TargetType.DualShock4)
            };

            yield return new GameMenuItem
            {
                MenuSection = "PlayniteSense",
                Description = "Enable PlayniteSense (X360)",
                Action = a => EnableGame(game.Id, TargetType.Xbox360)
            };
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (!settings.Settings.EnabledGames.Contains(args.Game.Id))
            {
                return;
            }

            if (!bridge.VerifyDependenciesExist())
            {
                args.CancelStartup = true;
                string message =
                    "PlayniteSense cancelled the game launch because ViGEmBus or HidHide is missing or " +
                    "not operational. Open the PlayniteSense diagnostics and verify both drivers.";

                Logger.Error(message);
                return;
            }

            TargetType mode;
            if (!settings.Settings.GameModes.TryGetValue(args.Game.Id, out mode))
            {
                mode = TargetType.Xbox360;
            }

            // The physical pad is cloaked before Playnite creates the game process.
            bridge.Start(mode);

            if (!bridge.IsRunning)
            {
                args.CancelStartup = true;
                string message =
                    "PlayniteSense could not initialize the controller bridge. The game launch was cancelled " +
                    "to prevent duplicate controllers. Open the diagnostic log or extensions.log for the exact error.";

                Logger.Error(message);
            }
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            bridge.Stop();
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            bridge.Stop();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            bridge.Stop();
        }

        public override void Dispose()
        {
            bridge.StatusChanged -= OnBridgeStatusChanged;
            bridge.Dispose();
            base.Dispose();
        }

        private void EnableGame(Guid gameId, TargetType targetType)
        {
            if (!settings.Settings.EnabledGames.Contains(gameId))
            {
                settings.Settings.EnabledGames.Add(gameId);
            }

            settings.Settings.GameModes[gameId] = targetType;
            settings.EndEdit();
        }

        private void OnBridgeStatusChanged(string status)
        {
            if (IsErrorStatus(status))
            {
                Logger.Error("PlayniteSense bridge: " + status);
            }
            else
            {
                Logger.Info("PlayniteSense bridge: " + status);
            }
        }

        private static bool IsErrorStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            string normalized = status.ToLowerInvariant();
            return normalized.Contains("failed") ||
                   normalized.Contains("could not") ||
                   normalized.Contains("cannot") ||
                   normalized.Contains("unavailable") ||
                   normalized.Contains("timed out") ||
                   normalized.Contains("stopped:");
        }

        public override ISettings GetSettings(bool firstRun) => settings;

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRun) =>
            new PlayniteSenseSettingsView(settings, bridge);
    }
}
