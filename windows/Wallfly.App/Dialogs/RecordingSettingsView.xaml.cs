using System.Windows.Controls;

namespace Wallfly.App.Dialogs;

/// <summary>A settings section over <see cref="ViewModels.SettingsViewModel"/>; shared by the settings window and the onboarding.</summary>
public partial class RecordingSettingsView : UserControl
{
    public RecordingSettingsView() => InitializeComponent();
}
