using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using ExternalDictionaryEditor.control;

namespace OpenUtau.App.Plugins;

public class DictionaryEditorPlugin : BatchEdit {
    public virtual string Name => "Dictionary Editor (Plugin Edition)"; 

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        Dispatcher.UIThread.Post(() => {
            var editorControl = new DictionaryEditorControl {
                Part = part
            };

            var window = new Window {
                Title = "Dictionary Editor (External Plugin Edition)",
                Width = 400,
                MinWidth = 400, 
                Height = 650,
                MinHeight = 400,
                Content = editorControl,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            if (Avalonia.Application.Current?.ApplicationLifetime 
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) {
                window.Show(desktop.MainWindow);
            } else {
                window.Show();
            }
        });
    }
}