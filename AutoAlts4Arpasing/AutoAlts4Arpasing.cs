using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Core;
using OpenUtau.Core.Editing;
using OpenUtau.Core.Ustx;
using Serilog;

namespace AutoAltParam;

public class AutoContextualAltPlugin : BatchEdit {
    public virtual string Name => "Batch Auto Contextual Alts";

    private string GetPureAlias(string rawAlias, USinger singer) {
        if (string.IsNullOrWhiteSpace(rawAlias) || singer == null || singer.Subbanks == null) 
            return rawAlias;

        string cleanAlias = rawAlias;

        // Strip Suffixes (e.g., "C4", "Power")
        var suffixes = singer.Subbanks.Select(s => s.Suffix)
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToList();

        foreach (var suffix in suffixes) {
            if (cleanAlias.EndsWith(suffix)) {
                cleanAlias = cleanAlias.Substring(0, cleanAlias.Length - suffix.Length);
                break; 
            }
        }

        // Strip Prefixes
        var prefixes = singer.Subbanks.Select(s => s.Prefix)
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToList();

        foreach (var prefix in prefixes) {
            if (cleanAlias.StartsWith(prefix)) {
                cleanAlias = cleanAlias.Substring(prefix.Length);
                break; 
            }
        }
        cleanAlias = System.Text.RegularExpressions.Regex.Replace(cleanAlias, @"\s*\d+$", "");

        return cleanAlias.Trim();
    }

    private bool TryGetMappedOtoAnyFormat(USinger singer, string baseAlias, int alt, int tone, string color, out UOto testOto) {
        testOto = null;
        var formats = alt == 0
            ? new[] { baseAlias }
            : new[] { $"{baseAlias}{alt}", $"{baseAlias} {alt}", $"{baseAlias}0{alt}" };

        foreach (var format in formats) {
            if (singer.TryGetMappedOto(format, tone, color, out testOto)) {
                return true;
            }
        }
        return false;
    }

    private string FormatForChunking(string cleanAlias, USinger singer) {
        if (string.IsNullOrEmpty(cleanAlias)) return "";
        
        string pure = cleanAlias.ToLower();
        pure = pure.Replace("-", "").Trim();
        pure = pure.Replace(" ", "_");
        while (pure.Contains("__")) pure = pure.Replace("__", "_");
        
        return pure;
    }

    private string MergeChunks(string left, string right) {
        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;

        var lParts = left.Split('_');
        var rParts = right.Split('_');

        // If the end of the left matches the start of the right, fuse them together!
        if (lParts.Length > 0 && rParts.Length > 0 && lParts.Last() == rParts.First()) {
            return left + "_" + string.Join("_", rParts.Skip(1));
        }

        return left + "_" + right;
    }

    private int FindBestAlt(string cleanAlias, string prevCleanAlias, string nextCleanAlias, UOto prevOto, USinger singer, int tone, string color, out UOto chosenOto) {
        chosenOto = null;
        string baseAlias = cleanAlias.ToLower();

        string prevChunk = FormatForChunking(prevCleanAlias, singer);
        string currChunk = FormatForChunking(cleanAlias, singer);
        string nextChunk = FormatForChunking(nextCleanAlias, singer);
        
        string prevWav = prevOto?.File;

        int bestAlt = 0;
        int highestScore = -1;

        for (int alt = 0; alt < 25; alt++) {
            if (TryGetMappedOtoAnyFormat(singer, baseAlias, alt, tone, color, out var testOto)) {
                string testWav = testOto.File;
                if (string.IsNullOrEmpty(testWav)) continue;

                int score = 0;

                // Create a padded, formatted version of the WAV filename
                string testWavNorm = Path.GetFileNameWithoutExtension(testWav).ToLower()
                    .Replace("-", "").Trim().Replace(" ", "_");
                while (testWavNorm.Contains("__")) testWavNorm = testWavNorm.Replace("__", "_");
                string paddedWav = $"_{testWavNorm}_"; 

                // Is the filename phonetic (has letters), or csv format (10.wav etc)
                bool isPhonetic = testWavNorm.Any(char.IsLetter);
                bool prevBaton = !string.IsNullOrEmpty(prevWav) && string.Equals(testWav, prevWav, StringComparison.OrdinalIgnoreCase);
                bool nextBaton = false;

                if (!string.IsNullOrEmpty(nextCleanAlias)) {
                    for (int nextAlt = 0; nextAlt < 25; nextAlt++) {
                        if (TryGetMappedOtoAnyFormat(singer, nextCleanAlias.ToLower(), nextAlt, tone, color, out var nextOto)) {
                            if (string.Equals(nextOto.File, testWav, StringComparison.OrdinalIgnoreCase)) {
                                nextBaton = true;
                                break; 
                            }
                        }
                    }
                }

                if (isPhonetic) {
                    if (prevBaton) score += 40;
                    if (nextBaton) score += 40;
                } else {
                    if (prevBaton) score += 100;
                    if (nextBaton) score += 100;
                }

                // This guarantees the phonemes are actually next to each other in the filename, not just somewhere in the name
                string forwardOverlap = MergeChunks(currChunk, nextChunk);
                string backwardOverlap = MergeChunks(prevChunk, currChunk);

                if (!string.IsNullOrEmpty(forwardOverlap) && paddedWav.Contains($"_{forwardOverlap}_")) {
                    score += 150;
                }
                if (!string.IsNullOrEmpty(backwardOverlap) && paddedWav.Contains($"_{backwardOverlap}_")) {
                    score += 150;
                }

                if (!string.IsNullOrEmpty(nextChunk) && paddedWav.Contains($"_{nextChunk}_")) score += 30;
                if (!string.IsNullOrEmpty(prevChunk) && paddedWav.Contains($"_{prevChunk}_")) score += 30;
                if (!string.IsNullOrEmpty(currChunk) && paddedWav.Contains($"_{currChunk}_")) score += 20;

                // Lock in the highest score
                if (score > highestScore) {
                    highestScore = score;
                    bestAlt = alt;
                    chosenOto = testOto; 
                }
            }
        }

        if (chosenOto == null) {
            TryGetMappedOtoAnyFormat(singer, baseAlias, 0, tone, color, out chosenOto);
        }
        return highestScore >= 0 ? bestAlt : 0;
    }

    public void Run(UProject project, UVoicePart part, List<UNote> selectedNotes, DocManager docManager) {
        var notes = selectedNotes.Count > 0 ? selectedNotes : part.notes.ToList();
        if (notes.Count == 0) return;

        // process notes left-to-right across the timeline
        var sortedNotes = notes.OrderBy(n => n.position).ToList();

        var track = project.tracks[part.trackNo];
        var singer = track.Singer;

        if (singer == null || !singer.Loaded) return;
        string targetExpression = "alt"; 
        if (!project.expressions.ContainsKey(targetExpression)) return;

        docManager.StartUndoGroup("command.batch.plugin", true);
        
        var allPhonemes = part.phonemes.OrderBy(p => p.position).ThenBy(p => p.index).ToList();
        var phGlobalIndexMap = allPhonemes.Select((p, i) => new { p, i }).ToDictionary(x => x.p, x => x.i);

        int modifiedCount = 0;
        UOto previousOto = null; 
        int lastGlobalIdx = -2;
        
        UNote lastNote = null; 

        foreach (var note in sortedNotes) {
            var notePhonemes = part.phonemes.Where(p => p.Parent == note).OrderBy(p => p.index).ToList();
            if (notePhonemes.Count == 0) continue;

            if (lastNote != null && note.position > (lastNote.position + lastNote.duration)) {
                previousOto = null; 
            }

            float?[] pValues = new float?[notePhonemes.Count];

            for (int i = 0; i < notePhonemes.Count; i++) {
                var ph = notePhonemes[i];
                string cleanAlias = GetPureAlias(ph.phoneme, singer);
                
                int globalIdx = phGlobalIndexMap.ContainsKey(ph) ? phGlobalIndexMap[ph] : -1;

                if (globalIdx != lastGlobalIdx + 1) {
                    previousOto = null;
                }

                if (cleanAlias.StartsWith("-")) {
                    previousOto = null;
                }

                string prevCleanAlias = (globalIdx > 0) ? GetPureAlias(allPhonemes[globalIdx - 1].phoneme, singer) : "";
                string nextCleanAlias = (globalIdx != -1 && globalIdx < allPhonemes.Count - 1) ? GetPureAlias(allPhonemes[globalIdx + 1].phoneme, singer) : "";

                if (cleanAlias.EndsWith("-") || cleanAlias.EndsWith("R")) {
                    nextCleanAlias = "";
                }

                if (prevCleanAlias.EndsWith("-") || prevCleanAlias.EndsWith("R")) {
                    previousOto = null;
                    prevCleanAlias = "";
                }

                string color = "";
                var clrExp = note.phonemeExpressions.FirstOrDefault(e => e.abbr == "clr" && e.index == ph.index);
                if (clrExp != null && project.expressions.TryGetValue("clr", out var clrDesc) && clrDesc.options != null) {
                    int clrIdx = (int)clrExp.value;
                    if (clrIdx >= 0 && clrIdx < clrDesc.options.Length) color = clrDesc.options[clrIdx];
                }

                int bestAlt = FindBestAlt(cleanAlias, prevCleanAlias, nextCleanAlias, previousOto, singer, note.tone, color, out UOto newOto);
                pValues[i] = bestAlt;

                previousOto = newOto;
                lastGlobalIdx = globalIdx;
            }

            docManager.ExecuteCmd(new SetNoteExpressionCommand(
                project, track, part, note, targetExpression, pValues));
            
            modifiedCount++;
            lastNote = note;
        }

        docManager.EndUndoGroup();
        Log.Information($"[AutoContextualAlt] Applied strict contextual alt mappings to {modifiedCount} notes.");
    }
}