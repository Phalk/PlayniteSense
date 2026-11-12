using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace PlayniteSense
{
    public class PlayniteSenseSettings : ObservableObject
    {
        public List<Guid> EnabledGames { get; set; } = new List<Guid>();
        public Dictionary<Guid, TargetType> GameModes { get; set; } = new Dictionary<Guid, TargetType>();
        public bool EnableDesktopControls { get; set; }
        public bool EnableDesktopControlsOutsideGames { get; set; }
    }

    public class PlayniteSenseSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PlayniteSensePlugin plugin;
        public PlayniteSenseSettings Settings { get; set; }
        private bool originalDesktopControls;
        private bool originalDesktopControlsOutsideGames;

        public PlayniteSenseSettingsViewModel(PlayniteSensePlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<PlayniteSenseSettings>() ?? new PlayniteSenseSettings();

            if (Settings.EnabledGames == null)
            {
                Settings.EnabledGames = new List<Guid>();
            }

            if (Settings.GameModes == null)
            {
                Settings.GameModes = new Dictionary<Guid, TargetType>();
            }
        }

        public void BeginEdit()
        {
            originalDesktopControls = Settings.EnableDesktopControls;
            originalDesktopControlsOutsideGames = Settings.EnableDesktopControlsOutsideGames;
        }

        public void CancelEdit()
        {
            Settings.EnableDesktopControls = originalDesktopControls;
            Settings.EnableDesktopControlsOutsideGames = originalDesktopControlsOutsideGames;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
            plugin.ApplyDesktopControlSettings();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = null;
            return true;
        }
    }
}
