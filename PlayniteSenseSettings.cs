using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace PlayniteSense
{
    public class PlayniteSenseSettings : ObservableObject
    {
        public List<Guid> EnabledGames { get; set; } = new List<Guid>();
        public Dictionary<Guid, TargetType> GameModes { get; set; } = new Dictionary<Guid, TargetType>();
    }

    public class PlayniteSenseSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PlayniteSensePlugin plugin;
        public PlayniteSenseSettings Settings { get; set; }

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

        public void BeginEdit() { }
        public void CancelEdit() { }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = null;
            return true;
        }
    }
}
