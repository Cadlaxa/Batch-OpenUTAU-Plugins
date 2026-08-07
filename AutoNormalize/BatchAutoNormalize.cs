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
    public virtual string Name => "Batch Auto Normalize (P Flag) v1.1";

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

    // Checks specific_values prior to standard evaluation logic
    private float? GetSpecificValue(string rawAlias, string cleanAlias, PhonemeConfig config) {
        if (config == null || config.specific_values == null) return null;

        // 1. Try whole exact match (including suffix/prefix)
        var exactSpecific = config.specific_values.FirstOrDefault(x => string.Equals(x.alias, rawAlias, StringComparison.Ordinal));
        if (exactSpecific != null) return exactSpecific.value;

        // 2. Fallback: Try cleaned alias match (old method)
        var cleanSpecific = config.specific_values.FirstOrDefault(x => string.Equals(x.alias, cleanAlias, StringComparison.Ordinal));
        if (cleanSpecific != null) return cleanSpecific.value;

        return null;
    }

    private bool CheckIfVC(string cleanAlias, PhonemeConfig config) {
        if (string.IsNullOrWhiteSpace(cleanAlias) || config == null) return false;

        var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Strip leading boundary/ending symbols (like '-' in '- a')
        var coreParts = parts.Where(p => GetSymbolType(p, config) != "ending").ToList();
        if (coreParts.Count < 2) return false; 

        string firstType = GetSymbolType(coreParts[0], config);
        string lastType = GetSymbolType(coreParts[coreParts.Count - 1], config);

        return firstType == "vowel" && lastType != "vowel";
    }

    private string GetSymbolType(string sym, PhonemeConfig config) {
        return config?.symbols?.FirstOrDefault(x => string.Equals(x.symbol.Trim(), sym.Trim(), StringComparison.Ordinal))?.type;
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

            float baseFallback = GetTypeValue("vowel", currentConfig) ?? 86f;

            for (int i = 0; i < notePhonemes.Count; i++) {
                string rawAlias = notePhonemes[i].phoneme;
                string cleanAlias = GetPureAlias(rawAlias, singer);
                
                // 1. Check Specific Values first (exact raw alias override)
                float? specificVal = GetSpecificValue(rawAlias, cleanAlias, currentConfig);
                
                if (specificVal.HasValue) {
                    pValues[i] = specificVal.Value;
                }
                // 2. If it is a VC, calculate the average between its symbols
                else if (CheckIfVC(cleanAlias, currentConfig)) {
                    var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Where(p => GetSymbolType(p, currentConfig) != "ending")
                                           .ToArray();
                    
                    string firstType = GetSymbolType(parts[0], currentConfig);
                    string lastType = GetSymbolType(parts[parts.Length - 1], currentConfig);

                    float prevValue = GetTypeValue(firstType, currentConfig) ?? baseFallback;
                    float nextValue = GetTypeValue(lastType, currentConfig) ?? baseFallback;
                    
                    float middleValue = ((prevValue + nextValue) / 2f);
                    pValues[i] = Math.Min(100f, middleValue);
                } 
                // 3. Standard priority logic fallback
                else {
                    float? flagValue = GetStandardAliasValue(cleanAlias, currentConfig);
                    pValues[i] = flagValue ?? baseFallback; 
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

        var parts = cleanAlias.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // 1. STRICT ENDING CHECK: Only apply ending value if the VERY LAST token in the alias is of type 'ending'
        string absoluteLastToken = parts[parts.Length - 1];
        if (GetSymbolType(absoluteLastToken, config) == "ending") {
            return GetTypeValue("ending", config);
        }

        // 2. Filter out any leading/boundary 'ending' symbols (e.g. '-' in '- a' or '- ta') for phoneme type evaluation
        var nonEndingParts = parts.Where(p => GetSymbolType(p, config) != "ending").ToList();
        if (nonEndingParts.Count == 0) return null;

        var validTypes = nonEndingParts
            .Select(p => GetSymbolType(p, config))
            .Where(t => t != null)
            .ToList();

        if (validTypes.Count == 0) return null;

        string firstType = validTypes[0];
        string lastType = validTypes[validTypes.Count - 1];

        if (lastType == "vowel") return GetTypeValue("vowel", config);
        if (firstType == "vowel") return GetTypeValue("vowel", config);

        return GetTypeValue(lastType, config);
    }
}