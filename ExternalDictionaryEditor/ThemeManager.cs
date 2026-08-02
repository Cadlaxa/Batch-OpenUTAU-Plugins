using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace ExternalDictionaryEditor.strings {
    public static class ThemeManager {
        /// <summary>
        /// Retrieves a localized string key from Avalonia's Application Resources.
        /// If the key is not found, returns the key name itself as a fallback.
        /// </summary>
        public static string GetString(string key) {
            TryGetString(key, out string value);
            return value;
        }

        public static bool TryGetString(string key, out string value) {
            if (Application.Current == null) {
                value = key;
                return false;
            }
            
            IResourceDictionary resDict = Application.Current.Resources;
            if (resDict.TryGetResource(key, ThemeVariant.Default, out var outVar) && outVar is string s) {
                value = s;
                return true;
            }

            value = key;
            return false;
        }
    }
}