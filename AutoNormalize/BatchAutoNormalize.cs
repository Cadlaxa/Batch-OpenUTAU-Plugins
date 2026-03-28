using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;

namespace AutoNormalize;

public class SymbolDef {
    public string symbol { get; set; }
    public string type { get; set; }
}

public class ValueDef {
    public string type { get; set; }
    public float value { get; set; }
}

public class SpecificValueDef {
    public string alias { get; set; }
    public float value { get; set; }
}

public class PhonemeConfig {
    public List<SymbolDef> symbols { get; set; } = new List<SymbolDef>();
    public List<ValueDef> values { get; set; } = new List<ValueDef>();
    public List<SpecificValueDef> specific_values { get; set; } = new List<SpecificValueDef>();
}

public class AutoNormalizePFlag : BatchEdit {
    public virtual string Name => "Batch Auto Normalize (P Flag)";

    private PhonemeConfig LoadConfig(string path) {
        if (!File.Exists(path)) return null;
        try {
            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<PhonemeConfig>(yaml);
        } catch (Exception ex) {
            Log.Error(ex, $"[AutoNormalize] YAML Parsing Error in file: {path}");
            return null;
        }
    }

    // Strips Voicebank-specific prefixes (like "_") and suffixes (like "C4", "_Power")
    private string GetPureAlias(string rawAlias, USinger singer) {
        if (string.IsNullOrWhiteSpace(rawAlias) || singer == null || singer.Subbanks == null) 
            return rawAlias;

        string cleanAlias = rawAlias;

        var suffixes = singer.Subbanks.Select(s => s.Suffix)
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToList();

        var prefixes = singer.Subbanks.Select(s => s.Prefix)
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToList();

        foreach (var suffix in suffixes) {
            if (cleanAlias.EndsWith(suffix)) {
                cleanAlias = cleanAlias.Substring(0, cleanAlias.Length - suffix.Length);
                break; 
            }
        }

        foreach (var prefix in prefixes) {
            if (cleanAlias.StartsWith(prefix)) {
                cleanAlias = cleanAlias.Substring(prefix.Length);
                break; 
            }
        }

        int lastSpaceIndex = cleanAlias.LastIndexOf(' ');
        if (lastSpaceIndex != -1) {
            string possibleAlt = cleanAlias.Substring(lastSpaceIndex + 1);
            if (int.TryParse(possibleAlt, out _)) {
                cleanAlias = cleanAlias.Substring(0, lastSpaceIndex);
            }
        }

        cleanAlias = System.Text.RegularExpressions.Regex.Replace(cleanAlias, @"\d+", "");
        cleanAlias = System.Text.RegularExpressions.Regex.Replace(cleanAlias, @"\s+", " ");

        return cleanAlias.Trim();
    }

    private bool CheckIfVC(string cleanAlias, PhonemeConfig config) {
        if (string.IsNullOrWhiteSpace(cleanAlias) || config == null) return false;

        var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        var validParts = parts.Where(p => GetSymbolType(p, config) != null).ToList();
        if (validParts.Count < 2) return false; 

        string firstType = GetSymbolType(validParts[0], config);
        string lastType = GetSymbolType(validParts[validParts.Count - 1], config);

        return firstType == "vowel" && lastType != "vowel" && lastType != "ending";
    }

    private string GetSymbolType(string sym, PhonemeConfig config) {
        return config?.symbols?.FirstOrDefault(x => string.Equals(x.symbol.Trim(), sym.Trim(), StringComparison.OrdinalIgnoreCase))?.type;
    }

    private float? GetTypeValue(string type, PhonemeConfig config) {
        if (string.IsNullOrEmpty(type)) return null;
        return config?.values?.FirstOrDefault(x => string.Equals(x.type.Trim(), type.Trim(), StringComparison.OrdinalIgnoreCase))?.value;
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        if (notes.Count == 0) return;

        string targetExpression = "norm"; 

        if (!project.expressions.ContainsKey(targetExpression)) {
            Log.Error($"[AutoNormalize] Aborted: Expression '{targetExpression}' not found.");
            return;
        }

        string pluginDir = PathManager.Inst.PluginsPath; 
        string defaultConfigPath = Path.Combine(pluginDir, "normalize-config.yaml");

        // If the config isn't in the root Plugins folder, scan all subfolders to find it
        if (!File.Exists(defaultConfigPath)) {
            try {
                string[] searchResults = Directory.GetFiles(pluginDir, "normalize-config.yaml", SearchOption.AllDirectories);
                if (searchResults.Length > 0) {
                    defaultConfigPath = searchResults[0];
                }
            } catch (Exception ex) {
                Log.Warning($"[AutoNormalize] Subfolder search failed: {ex.Message}");
            }
        }

        PhonemeConfig currentConfig = LoadConfig(defaultConfigPath) ?? new PhonemeConfig();

        var track = project.tracks[part.trackNo];
        var singer = track.Singer;

        if (singer != null && !string.IsNullOrEmpty(singer.Location)) {
            string singerConfigPath = Path.Combine(singer.Location, "normalize-config.yaml");
            var vbConfig = LoadConfig(singerConfigPath);
            if (vbConfig != null) {
                currentConfig = vbConfig;
            }
        }

        docManager.StartUndoGroup("command.batch.plugin", true);
        int modifiedCount = 0;

        foreach (var note in notes) {
            var notePhonemes = part.phonemes.Where(p => p.Parent == note).OrderBy(p => p.index).ToList();
            if (notePhonemes.Count == 0) continue;

            float?[] pValues = new float?[notePhonemes.Count];

            for (int i = 0; i < notePhonemes.Count; i++) {
                string rawAlias = notePhonemes[i].phoneme;
                string cleanAlias = GetPureAlias(rawAlias, singer);
                
                // If it is a VC, calculate the average between its symbols
                if (CheckIfVC(cleanAlias, currentConfig)) {
                    var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    string firstType = GetSymbolType(parts[0], currentConfig);
                    string lastType = GetSymbolType(parts[parts.Length - 1], currentConfig);

                    float prevValue = GetTypeValue(firstType, currentConfig) ?? 86f; // Vowel value
                    float nextValue = GetTypeValue(lastType, currentConfig) ?? 86f;  // Consonant value
                    
                    // Math: (Vowel + Consonant) / 2
                    float middleValue = ((prevValue + nextValue) / 2f);
                    
                    pValues[i] = Math.Min(100f, middleValue);

                    /*if (modifiedCount < 5) {
                        Log.Information($"[AutoNormalize-VC] '{rawAlias}' -> Clean: '{cleanAlias}' | Averaged {prevValue} and {nextValue} + 20 -> Result: {pValues[i]}");
                    }*/
                } 
                // tandard priority logic
                else {
                    float? flagValue = GetStandardAliasValue(cleanAlias, currentConfig);
                    pValues[i] = flagValue ?? 86f; 
                }
            }

            docManager.ExecuteCmd(new SetNoteExpressionCommand(
                project, track, part, note, targetExpression, pValues));
            
            modifiedCount++;
        }

        docManager.EndUndoGroup();
    }

    private float? GetStandardAliasValue(string cleanAlias, PhonemeConfig config) {
        if (string.IsNullOrWhiteSpace(cleanAlias) || config == null) return null;

        var specific = config.specific_values?.FirstOrDefault(x => string.Equals(x.alias, cleanAlias, StringComparison.Ordinal));
        if (specific != null) return specific.value;

        var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var validParts = parts.Where(p => GetSymbolType(p, config) != null).ToList();
        if (validParts.Count == 0) return null;

        string firstType = GetSymbolType(validParts[0], config);
        string lastType = GetSymbolType(validParts[validParts.Count - 1], config);

        string actualLastString = parts[parts.Length - 1];
        string actualLastType = GetSymbolType(actualLastString, config);

        if (actualLastType == "ending") return GetTypeValue("ending", config);
        if (lastType == "vowel") return GetTypeValue("vowel", config);
        if (firstType == "vowel") return GetTypeValue("vowel", config);

        return GetTypeValue(lastType, config);
    }
}