using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Threading;

namespace PlayniteSense
{
    public class PlayniteSensePlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly PlayniteSenseSettingsViewModel settings;
        private readonly VirtualPadBridge bridge = new VirtualPadBridge();
        private readonly object sessionSync = new object();
        private readonly HashSet<Guid> activeGames = new HashSet<Guid>();
        private Timer idleTimer;
        private bool idleControlsEnabled;
        private bool idleBridgeOwned;
        private bool diagnosticsActive;
        private bool applicationStarted;

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
            if (CheckRequiredDrivers())
                bridge.EnsurePlayniteIsWhitelisted();

            lock (sessionSync)
            {
                applicationStarted = true;
                idleTimer = new Timer(_ => RefreshIdleBridge(), null, Timeout.Infinite, Timeout.Infinite);
            }
            ApplyDesktopControlSettings();
        }

        internal void ApplyDesktopControlSettings()
        {
            lock (sessionSync)
            {
                idleControlsEnabled = settings.Settings.EnableDesktopControlsOutsideGames;
                if (!idleControlsEnabled)
                    StopIdleBridge();
                ScheduleIdleRefresh();
            }
        }

        private void ScheduleIdleRefresh()
        {
            bool shouldListen = applicationStarted && idleControlsEnabled &&
                                activeGames.Count == 0 && !diagnosticsActive;
            idleTimer?.Change(shouldListen ? 0 : Timeout.Infinite, Timeout.Infinite);
        }

        private void RefreshIdleBridge()
        {
            lock (sessionSync)
            {
                if (!applicationStarted || !idleControlsEnabled || activeGames.Count != 0 || diagnosticsActive)
                    return;

                try
                {
                    if (!bridge.IsRunning)
                    {
                        bridge.StartDesktopOnly();
                        idleBridgeOwned = bridge.IsRunning;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not start desktop controls outside games.");
                }
                finally
                {
                    // One-shot scheduling prevents overlapping retries while HID discovery is busy.
                    idleTimer?.Change(3000, Timeout.Infinite);
                }
            }
        }

        private void StopIdleBridge()
        {
            if (!idleBridgeOwned)
                return;
            idleBridgeOwned = false;
            bridge.Stop();
        }

        private bool BeginDiagnostics()
        {
            lock (sessionSync)
            {
                if (!applicationStarted || activeGames.Count != 0)
                    return false;
                diagnosticsActive = true;
                ScheduleIdleRefresh();
                StopIdleBridge();
                return true;
            }
        }

        private void EndDiagnostics()
        {
            lock (sessionSync)
            {
                if (!diagnosticsActive)
                    return;
                if (activeGames.Count == 0)
                    bridge.Stop();
                diagnosticsActive = false;
                ScheduleIdleRefresh();
            }
        }

        internal bool CheckRequiredDrivers()
        {
            bool hidHide = bridge.IsHidHideAvailable();
            bool vigem = bridge.IsViGEmAvailable();
            if (!hidHide)
                OfferDriverInstallation("HidHide", "https://github.com/nefarius/HidHide/releases/latest");
            if (!vigem)
                OfferDriverInstallation("ViGEmBus", "https://github.com/nefarius/ViGEmBus/releases/latest");
            return hidHide && vigem;
        }

        private void OfferDriverInstallation(string name, string url)
        {
            Logger.Error("Required driver unavailable: " + name);
            if (PlayniteApi.Dialogs.ShowMessage(
                name + " is missing or is not operational. PlayniteSense needs it for virtual controller emulation.\n\n" +
                "Open the official installer download page?",
                "PlayniteSense - Required driver", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Could not open " + name + " download page: " + ex.Message);
                PlayniteApi.Dialogs.ShowErrorMessage("Could not open the download page.\n" + url, "PlayniteSense");
            }
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
            lock (sessionSync)
            {
                activeGames.Add(args.Game.Id);
                ScheduleIdleRefresh();
                StopIdleBridge();
                if (diagnosticsActive)
                    bridge.Stop();
            }
            if (!settings.Settings.EnabledGames.Contains(args.Game.Id))
            {
                if (settings.Settings.EnableDesktopControls)
                {
                    lock (sessionSync)
                        bridge.StartDesktopOnly();
                }
                return;
            }

            if (!CheckRequiredDrivers())
            {
                args.CancelStartup = true;
                string message =
                    "PlayniteSense cancelled the game launch because ViGEmBus or HidHide is missing or " +
                    "not operational. Open the PlayniteSense diagnostics and verify both drivers.";

                Logger.Error(message);
                FinishGame(args.Game.Id);
                return;
            }

            TargetType mode;
            if (!settings.Settings.GameModes.TryGetValue(args.Game.Id, out mode))
            {
                mode = TargetType.Xbox360;
            }

            // The physical pad is cloaked before Playnite creates the game process.
            lock (sessionSync)
                bridge.Start(mode, settings.Settings.EnableDesktopControls);

            if (!bridge.IsRunning)
            {
                args.CancelStartup = true;
                string message =
                    "PlayniteSense could not initialize the controller bridge. The game launch was cancelled " +
                    "to prevent duplicate controllers. Open the diagnostic log or extensions.log for the exact error.";

                Logger.Error(message);
                FinishGame(args.Game.Id);
            }
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            FinishGame(args.Game.Id);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            FinishGame(args.Game.Id);
        }

        private void FinishGame(Guid gameId)
        {
            lock (sessionSync)
            {
                // A cancelled launch can be reported twice; do not stop a new idle session.
                if (!activeGames.Remove(gameId))
                    return;
                if (activeGames.Count == 0)
                    bridge.Stop();
                ScheduleIdleRefresh();
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopApplicationSessions();
        }

        private void StopApplicationSessions()
        {
            lock (sessionSync)
            {
                applicationStarted = false;
                idleTimer?.Dispose();
                idleTimer = null;
                idleBridgeOwned = false;
                activeGames.Clear();
                bridge.Stop();
            }
        }

        public override void Dispose()
        {
            StopApplicationSessions();
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
            new PlayniteSenseSettingsView(settings, bridge, CheckRequiredDrivers, BeginDiagnostics, EndDiagnostics);
    }
}
