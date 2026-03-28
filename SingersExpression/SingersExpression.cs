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

namespace SingersExpression;

public class ExpressionDef {
    public string name { get; set; }
    public string abbr { get; set; }
    public string type { get; set; }
    public float min { get; set; }
    public float max { get; set; }
    public float default_value { get; set; }
    public bool is_flag { get; set; }
    public string flag { get; set; }
    public List<string> options { get; set; }
}

public class ExpressionsConfig {
    public Dictionary<string, ExpressionDef> expressions { get; set; } = new Dictionary<string, ExpressionDef>();
}

public class SingersExpressionPlugin : BatchEdit {
    public virtual string Name => "Load Singer Expressions";

    private ExpressionsConfig LoadConfig(string path) {
        if (!File.Exists(path)) return null;
        try {
            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<ExpressionsConfig>(yaml);
        } catch (Exception ex) {
            Log.Error(ex, $"[SingersExpression] YAML Parsing Error in file: {path}");
            return null;
        }
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        if (notes.Count == 0) return;

        var track = project.tracks[part.trackNo];
        var singer = track.Singer;

        ExpressionsConfig currentConfig = null;

        // Try to load from the Voicebank's folder first
        if (singer != null && !string.IsNullOrEmpty(singer.Location)) {
            string singerConfigPath = Path.Combine(singer.Location, "expressions.yaml");
            currentConfig = LoadConfig(singerConfigPath);
        }

        // FALLBACK: If Voicebank doesn't have it, scan the Plugins folder and subfolders
        if (currentConfig == null || currentConfig.expressions == null || currentConfig.expressions.Count == 0) {
            string pluginDir = PathManager.Inst.PluginsPath;
            string fallbackPath = Path.Combine(pluginDir, "expressions.yaml");

            if (!File.Exists(fallbackPath)) {
                try {
                    string[] searchResults = Directory.GetFiles(pluginDir, "expressions.yaml", SearchOption.AllDirectories);
                    if (searchResults.Length > 0) {
                        fallbackPath = searchResults[0];
                    }
                } catch (Exception ex) {
                    Log.Warning($"[SingersExpression] Subfolder search failed: {ex.Message}");
                }
            }

            currentConfig = LoadConfig(fallbackPath);
        }

        if (currentConfig == null || currentConfig.expressions == null || currentConfig.expressions.Count == 0) {
            return;
        }

        docManager.StartUndoGroup("command.batch.plugin", true);

        foreach (var kvp in currentConfig.expressions) {
            var expr = kvp.Value;
            string abbr = expr.abbr ?? kvp.Key;

            UExpressionType expType = UExpressionType.Numerical;
            if (string.Equals(expr.type, "Options", StringComparison.OrdinalIgnoreCase)) {
                expType = UExpressionType.Options;
            } else if (string.Equals(expr.type, "Curve", StringComparison.OrdinalIgnoreCase)) {
                expType = UExpressionType.Curve;
            }

            if (project.expressions.TryGetValue(abbr, out var existingDescriptor)) {
                // UPDATE EXISTING: Override the min, max, default values, and type
                existingDescriptor.min = expr.min;
                existingDescriptor.max = expr.max;
                existingDescriptor.defaultValue = expr.default_value;
                existingDescriptor.name = expr.name;
                existingDescriptor.type = expType;
                existingDescriptor.isFlag = expr.is_flag;
                existingDescriptor.flag = expr.flag;
                
                if (expr.options != null && expr.options.Count > 0) {
                    existingDescriptor.options = expr.options.ToArray();
                }
            } else {
                var descriptor = new UExpressionDescriptor(expr.name, abbr, expr.min, expr.max, expr.default_value) {
                    type = expType,
                    isFlag = expr.is_flag,
                    flag = expr.flag
                };

                if (expr.options != null && expr.options.Count > 0) {
                    descriptor.options = expr.options.ToArray();
                }

                project.expressions.Add(abbr, descriptor);
                Log.Information($"[SingersExpression] Registered new expression: {expr.name} ({abbr})");
            }
        }

        int modifiedCount = 0;

        foreach (var note in notes) {
            var notePhonemes = part.phonemes.Where(p => p.Parent == note).OrderBy(p => p.index).ToList();
            if (notePhonemes.Count == 0) continue;

            foreach (var kvp in currentConfig.expressions) {
                var expr = kvp.Value;
                string abbr = expr.abbr ?? kvp.Key;

                if (string.Equals(expr.type, "Curve", StringComparison.OrdinalIgnoreCase)) {
                    continue; 
                }

                float?[] pValues = new float?[notePhonemes.Count];
                for (int i = 0; i < notePhonemes.Count; i++) {
                    pValues[i] = expr.default_value;
                }

                docManager.ExecuteCmd(new SetNoteExpressionCommand(
                    project, track, part, note, abbr, pValues));
            }
            
            modifiedCount++;
        }

        docManager.EndUndoGroup();
    }
}